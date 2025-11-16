
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using StackExchange.Redis;


  /* UDP Hole punching client
  Technique to enable direct communication between two clients behind NATs by using a third-party server to coordinate the connection.
  1. 2 clients A and B want to communicate.
  2. Both clients connect to a public server S and send their external IP and port
  3. Server S shares A's external IP/port with B and B's external IP/port with A
  4. Both clients send UDP packets to each other's external IP/port
  5. NATs create mappings for these outbound packets, allowing direct communication
  6. Clients A and B can now communicate directly via UDP
  
  NAT Types:
  - Full Cone NAT: Maps internal IP:port to same external IP:port for all destinations (easiest for hole punching)
  - Restricted Cone NAT: Reuses mapping but only accepts packets from IPs the client has sent to
  - Port Restricted Cone NAT: Like Restricted Cone but also checks source port
  - Symmetric NAT: Creates different external port for each destination (hardest - hole punching often fails)
  
  Note: Success depends on NAT types; symmetric NATs typically block this technique.

  Both clients open ephemeral ports acting as clients making outbound connections.
  NAT thinks both peers are "replying" to outbound connections.
  and thus without any listening sockets you have peer to peer communication.
  */
enum HolePunchingStates
{
  Initial = 0,
  RegisteredWithServer,
  ReceivedPeerInfo,
  SentPunchPacket,
  EstablishedConnection,
  Failed
}

// Protocol state machine for hole punching process
internal class HolePunchingStateMachine : IDisposable
{


  private readonly IConnectionMultiplexer _connectionMultiplexer;
  private readonly string _selfIp;
  // small buffer for receiving punch response
  private readonly byte[] _internalBuffer = new byte[1024]; 


  // State variables
  private IPAddress? _peerIp;
  private int _peerPort;
  private IPEndPoint? _peerEndPoint;
  private Socket? _udpSocket;

  // IDisposable support
  private bool disposedValue;

  private HolePunchingStates CurrentState { get; set; } = HolePunchingStates.Initial;

  // This is the primary inteface to get the hole punched socket once connection is established
  // The user should not dispose this socket, it will be disposed when the state machine is disposed
  public Socket HolePunchedSocket
  {
    get
    {
      if (CurrentState != HolePunchingStates.EstablishedConnection)
      {
        throw new InvalidOperationException("Connection not yet established.");
      }
      Debug.Assert(_udpSocket != null);
      return _udpSocket;
    }
  }

  public HolePunchingStateMachine(string registrationServerAddr)
  {
    _connectionMultiplexer = ConnectionMultiplexer.Connect(registrationServerAddr);
    _selfIp = Dns.GetHostEntry(Dns.GetHostName()).AddressList[0].ToString();
  }

  public async Task<bool> ConnectAsync(IPAddress destinationIp)
  {
    // Can only connect on initial or failed state
    if (CurrentState != HolePunchingStates.Initial || CurrentState != HolePunchingStates.Failed)
    {
      throw new InvalidOperationException("Connection process already started.");
    }

    _peerIp = destinationIp;

    // Main loop to progress through states till connection is established or failed
    while (CurrentState != HolePunchingStates.EstablishedConnection && CurrentState != HolePunchingStates.Failed)
    {
      await Next();
    }
  
    // Connection established or failed
    return CurrentState == HolePunchingStates.EstablishedConnection;
  }

  private async Task Next()
  {
    switch (CurrentState)
    {
      case HolePunchingStates.Initial: // At this stage we don't have an active UDP socket yet and have not registered our ephemeral port with server since we don't know it yet
        Debug.Assert(_udpSocket == null, "Invariant Violation: No UDP socket should exist in initial state"); 
        Debug.Assert(_peerIp != null, "Invariant Violation: Set by ConnectAsync before calling Next()");
        Debug.Assert(_peerPort == 0, "Invariant Violation: Will be set once we get peer info from server so currenty should be 0"); 
        Debug.Assert(_peerEndPoint == null, "Invariant Violation: Will be set once we get peer info from server so currenty should be null");

        // Initialize UDP socket and register self and port with server
        _udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // get which is the port the above socket is bound to
        Debug.Assert(_udpSocket.LocalEndPoint != null);
        IPEndPoint? localEndPoint = _udpSocket.LocalEndPoint as IPEndPoint; // HK TODO: Why is this safe?
        Debug.Assert(localEndPoint != null);
        int ephemeralPort = localEndPoint.Port; // HK TODO: I don't this PORT is correct here... Need to verify
        await RegisterWithServerAsync(ephemeralPort); // Let any failure in registration propagate up, redis stack exchange client should have already retried internally if needed
        CurrentState = HolePunchingStates.RegisteredWithServer;
        break;
      case HolePunchingStates.RegisteredWithServer:
        Debug.Assert(_udpSocket != null, "Invariant Violation: UDP socket should have been created in previous state Initial");
        Debug.Assert(_peerIp != null, "Invariant Violation: Set by ConnectAsync before calling Next() this should still hold");
        Debug.Assert(_peerPort == 0, "Invariant Violation: Will be set once we get peer info from server so currenty should be 0"); 
        Debug.Assert(_peerEndPoint == null, "Invariant Violation: Will be set once we get peer info from server so currenty should be null");

        // Wait for peer info from server
        IDatabase db = _connectionMultiplexer.GetDatabase();
        string? peerEphemeralPort = await db.StringGetAsync(_peerIp!.ToString()); // Any stackexchange issues will throw exceptions and propagate up and that is okay.
        if (peerEphemeralPort == null)
        {
          // Peer info not yet available
          await Task.Delay(500); // Wait and let the next next() invocation retry
          break;
        }
      
        // Got peer info!
        _peerPort = int.Parse(peerEphemeralPort);
        _peerEndPoint = new IPEndPoint(_peerIp!, _peerPort);
        CurrentState = HolePunchingStates.ReceivedPeerInfo;
        break;
      case HolePunchingStates.ReceivedPeerInfo:
        Debug.Assert(_udpSocket != null, "Invariant Violation: UDP socket should have been created in previous state Initial");
        Debug.Assert(_peerIp != null, "Invariant Violation: Set by ConnectAsync before calling Next() this should still hold");
        Debug.Assert(_peerPort >= 0, "Invariant Violation: Should have been received from server in previous state RegisteredWithServer"); 
        Debug.Assert(_peerEndPoint != null, "Invariant Violation: Should have been created in previous state RegisteredWithServer");
      
        // Send punch packet to peer
        byte[] punchPacket = System.Text.Encoding.UTF8.GetBytes("Punch");
        try
        {
          await _udpSocket.SendToAsync(punchPacket, SocketFlags.None, _peerEndPoint);
        }
        catch (SocketException)
        {
          // HK TODO: Add retry limit to avoid infinite loop here!
          // Retry from RegisteredWithServer state
          CurrentState = HolePunchingStates.RegisteredWithServer;
          _peerPort = 0; // reset the state so RegisteredWithServer can re-fetch peer info from server
          _peerEndPoint = null;
          break;
        }
      
        await Task.Delay(100); // Give some time for NAT to process the punch packet? HK TODO: not sure if this is needed.
        CurrentState = HolePunchingStates.SentPunchPacket;
        break;
      case HolePunchingStates.SentPunchPacket:
        Debug.Assert(_udpSocket != null, "Invariant Violation: UDP socket should have been created in previous state Initial");
        Debug.Assert(_peerIp != null, "Invariant Violation: Set by ConnectAsync before calling Next() this should still hold");
        Debug.Assert(_peerPort >= 0, "Invariant Violation: Should have been received from server in previous state RegisteredWithServer");
        Debug.Assert(_peerEndPoint != null, "Invariant Violation: Should have been created in previous state RegisteredWithServer");
  
        // Wait for response from peer
        SocketReceiveFromResult receiveResult = await _udpSocket.ReceiveFromAsync(_internalBuffer, SocketFlags.None, _peerEndPoint);
        if (receiveResult.ReceivedBytes > 0)
        {
          // Successfully received response from peer
          CurrentState = HolePunchingStates.EstablishedConnection;
        }
        else
        {
          // No response yet, retry sending punch packet by moving one level back on state where we will resend punch packet incase the first was not received
          // HK TODO: add retry limit to avoid infinite loop here!
          CurrentState = HolePunchingStates.ReceivedPeerInfo;
        }

        break;
      case HolePunchingStates.EstablishedConnection:
        Debug.Assert(_udpSocket != null, "Invariant Violation: UDP socket should have been created in previous state Initial");
        Debug.Assert(_peerIp != null, "Invariant Violation: Set by ConnectAsync before calling Next() this should still hold");
        Debug.Assert(_peerPort >= 0, "Invariant Violation: Should have been received from server in previous state RegisteredWithServer");
        Debug.Assert(_peerEndPoint != null, "Invariant Violation: Should have been created in previous state RegisteredWithServer");

        // If next was called on this state, then the want is to close the connection.
        ResetForFutureConnections();
        break;
      case HolePunchingStates.Failed:
        // If someone calls connect again on a failed connection we can restart from initial
        ResetForFutureConnections();
        break;
    }
  }

  private void ResetForFutureConnections()
  {
    if (_udpSocket != null)
    {
      _udpSocket.Dispose();
      _udpSocket = null;
    }

    // if state was past RegisteredWithServer we need to deregister from server as a best effort way otherwise TTL will expire our registration in worst case!
    if (CurrentState != HolePunchingStates.Initial)
    {
      DeregisterFromServer();
    }
  
    _peerIp = null;
    _peerPort = 0;
    _peerEndPoint = null!;

    CurrentState = HolePunchingStates.Initial; // Reset to initial for potential future connections
  }

  private Task RegisterWithServerAsync(int ephemeralPort)
  {
    IDatabase db = _connectionMultiplexer.GetDatabase();
    return db.StringSetAsync(_selfIp, ephemeralPort, expiry: TimeSpan.FromMinutes(10)); // Sessions are 10 minutes long...
  }

  private void DeregisterFromServer()
  {
    IDatabase db = _connectionMultiplexer.GetDatabase();
    db.KeyDelete(_selfIp);
  }

  protected virtual void Dispose(bool disposing)
  {
    if (!disposedValue)
    {
      if (disposing)
      {
        // dispose managed state (managed objects)
        _udpSocket?.Dispose();
        _connectionMultiplexer.Dispose();
      }

      // TODO: free unmanaged resources (unmanaged objects) and override finalizer
      // TODO: set large fields to null
      disposedValue = true;
    }
  }

  // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
  // ~State()
  // {
  //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
  //     Dispose(disposing: false);
  // }

  public void Dispose()
  {
    // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
    Dispose(disposing: true);
    GC.SuppressFinalize(this);
  }
}

public sealed class HOPPeer : IDisposable
{
  private readonly HolePunchingStateMachine _stateMachine;

  public HOPPeer(string registrationServerAddr)
  {
    _stateMachine = new HolePunchingStateMachine(registrationServerAddr);
  }

  public async Task<bool> ConnectAsync(string peerIp)
  {
    IPAddress destinationIp = IPAddress.Parse(peerIp);
    return await _stateMachine.ConnectAsync(destinationIp); // use await here to propagate exceptions showing connection failure explicitly here
  }

  public int Send(ReadOnlySpan<byte> data) => _stateMachine.HolePunchedSocket.Send(data);

  public int Receive(Span<byte> buffer) => _stateMachine.HolePunchedSocket.Receive(buffer);

  public void Close() => throw new NotImplementedException();

  public void Dispose() => _stateMachine.Dispose();
}


// Sample program showing how to use the above Hole Punched Peer class to establish a hole punched connection and send/receive data
internal class Program
{
  // args[0] = peer IP to connect to
  // args[1] = registration server address [Garnet Connection String]

  private static async Task Main(string[] args)
  {
    string registrationServerAddr = args[1];
    using HOPPeer peer = new HOPPeer(registrationServerAddr);

    if (!await peer.ConnectAsync(args[0]))
    {
      Console.WriteLine("Failed to establish connection.");
      return;
    }

    // once established create 2 separate execution flows for sending and receiving data
    Console.WriteLine("Connection established!");
    Console.WriteLine("Press Enter to send a ping message to peer"); 

    // one thread for receiving pings
    Task receivingTask = Task.Run(async () =>
    {
      byte[] receiveBuffer = new byte[1024];
      while (true)
      {
        int receivedBytes = peer.Receive(receiveBuffer);
        string message = System.Text.Encoding.UTF8.GetString(receiveBuffer, 0, receivedBytes);
        Console.WriteLine($"Received: {message}");
        await Task.Delay(100); // simulate processing delay
      }
    });

    while (true)
    {
      Console.ReadLine(); // wait for user to press enter
      string pingMessage = "Ping from peer!";
      byte[] pingData = System.Text.Encoding.UTF8.GetBytes(pingMessage);
      peer.Send(pingData);
      Console.WriteLine("Sent ping message to peer.");
    }
  }
}