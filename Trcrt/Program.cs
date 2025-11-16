using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Trcrt;

/*
    Trace route implementation in C#. To explore Ipv4 and Ipv6 routing.
*/

enum IcmpType : byte
{
    EchoRequest = 8,
    EchoReply = 0,
    DestinationUnreachable = 3,
    TimeExceeded = 11,

    // ICMPv6 types
    EchoRequestV6 = 128,
    EchoReplyV6 = 129,
    TimeExceededV6 = 3,  // Same as IPv4
    DestinationUnreachableV6 = 1,

    // Everything below is unused for this implementation but included for completeness during debugging
    SourceQuench = 4,
    Redirect = 5,
    ParameterProblem = 12,
    TimestampRequest = 13,
    TimestampReply = 14,
    InformationRequest = 15,
    InformationReply = 16,
    AddressMaskRequest = 17,
    AddressMaskReply = 18
}

// used for both echo request and echo reply
[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct IcmpEchoPacket
{
    public byte Type;          // Byte 0
    public byte Code;          // Byte 1
    public ushort Checksum;    // Bytes 2-3
    public ushort Identifier;  // Bytes 4-5
    public ushort SequenceNum; // Bytes 6-7
    public ushort Data; // Bytes 8+; for sending minimal data I will only ever use 2 bytes here
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
unsafe struct IcmpTimeExceededPacket
{
    public byte Type;          // Byte 0
    public byte Code;          // Byte 1
    public ushort Checksum;    // Bytes 2-3
    public uint Unused;        // Bytes 4-7
    public fixed byte InternetHeaderAnd64BitsOfOgData[68]; // Bytes 8+ (60 for max IP header + 8 for ICMP)
}

/*
    ICMP messages are sent using the basic IP header.  The first octet of
   the data portion of the datagram is a ICMP type field; the value of
   this field determines the format of the remaining data.  Any field
   labeled "unused" is reserved for later extensions and must be zero
   when sent, but receivers should not use these fields (except to
   include them in the checksum).  Unless otherwise noted under the
   individual format descriptions, the values of the internet header
   fields are as follows:

   Version
      4

   IHL
      Internet header length in 32-bit words.

   Type of Service
      0

   Total Length
      Length of internet header and data in octets.

   Identification, Flags, Fragment Offset
      Used in fragmentation, see [1].

   Time to Live
      Time to live in seconds; as this field is decremented at each
      machine in which the datagram is processed, the value in this
      field should be at least as great as the number of gateways which
      this datagram will traverse.

   Protocol
      ICMP = 1

   Header Checksum
      The 16 bit one's complement of the one's complement sum of all 16
      bit words in the header.  For computing the checksum, the checksum
      field should 

NOTE: This struct is used to recieve and parse the IP header portion of the packet on time exceeded messages.
*/
[StructLayout(LayoutKind.Sequential, Pack = 1, Size = InternetHeaderFormat.Size)]
struct InternetHeaderFormat
{
    public const int Size = 12;

    public byte VersionAndIhl;      // Byte 0
    public byte TypeOfService;      // Byte 1
    public ushort TotalLength;      // Bytes 2-3
    public Int32 IdentificationFlagsAndFragmentOffset; // Bytes 4-7 
    public byte TimeToLive;         // Byte 8
    public byte Protocol;           // Byte 9
    public ushort HeaderChecksum;   // Bytes 10-11
}

class Program
{
    unsafe static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.WriteLine("Usage: trcrt <destination> [--ipv6|-6]");
            return;
        }

        string destinationAddr = args[0];
        bool preferIPv6 = args.Contains("--ipv6") || args.Contains("-6");

        IPAddress[] destinationIps = Dns.GetHostEntry(destinationAddr).AddressList;
        
        // Select IP based on preference
        IPAddress? destinationIp = null;
        if (preferIPv6)
        {
            destinationIp = destinationIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetworkV6);
        }
        else
        {
            destinationIp = destinationIps.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork);
        }
        
        // Fallback to any available
        destinationIp ??= destinationIps.FirstOrDefault();
        
        if (destinationIp == null)
        {
            Console.WriteLine("No suitable IP address found for destination.");
            return;
        }

        bool isIPv6 = destinationIp.AddressFamily == AddressFamily.InterNetworkV6;
        Console.WriteLine($"Tracing route to {destinationAddr} [{destinationIp}] using {(isIPv6 ? "IPv6" : "IPv4")}");

        const ushort echoData = (ushort)('H' << 8 | 'K');
        
        IPAddress sourceIp = isIPv6 ? IPAddress.IPv6Any : IPAddress.Any;
        IPEndPoint remoteEndPoint = new IPEndPoint(destinationIp, 0);
        
        // Create socket matching the destination's address family
        Socket socket = new Socket(
            destinationIp.AddressFamily,
            SocketType.Raw,
            isIPv6 ? ProtocolType.IcmpV6 : ProtocolType.Icmp
        );

        try
        {
            List<IPAddress> routeHops = new List<IPAddress>() { sourceIp };
            bool completedTraversal = false;
            socket.Bind(new IPEndPoint(sourceIp, 0));
            
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 5_000);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendTimeout, 5_000);

            byte[] bufferRaw = new byte[512]; // Larger buffer for IPv6
            Span<byte> buffer = new Span<byte>(bufferRaw);
            
            int ttl = 1;
            while (true)
            {
                // Set TTL/Hop Limit
                if (isIPv6)
                {
                    socket.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.HopLimit, ttl);
                }
                else
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.IpTimeToLive, ttl);
                }

                // Build ICMP packet
                fixed (byte* bufferPtr = buffer)
                {
                    IcmpEchoPacket* echoRequest = (IcmpEchoPacket*)bufferPtr;
                    echoRequest->Type = isIPv6 ? (byte)IcmpType.EchoRequestV6 : (byte)IcmpType.EchoRequest;
                    echoRequest->Code = 0;
                    echoRequest->Checksum = 0;
                    echoRequest->Identifier = 1;
                    echoRequest->SequenceNum = 1;
                    echoRequest->Data = echoData;
                    echoRequest->Checksum = ComputeCheckSum(new ReadOnlySpan<byte>(bufferPtr, sizeof(IcmpEchoPacket)));
                }

                socket.SendTo(buffer.Slice(0, sizeof(IcmpEchoPacket)), remoteEndPoint);
                
                EndPoint senderEndPoint = isIPv6 
                    ? new IPEndPoint(IPAddress.IPv6Any, 0) 
                    : new IPEndPoint(IPAddress.Any, 0);
                
                int bytesReceived;
                try
                {
                    bytesReceived = socket.ReceiveFrom(bufferRaw, ref senderEndPoint);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    Console.WriteLine($"{ttl}\t* Request timed out");
                    Thread.Sleep(1000);
                    ttl++;
                    continue;
                }

                IPAddress routerIp = ((IPEndPoint)senderEndPoint).Address;

                // Parse response based on IP version
                ReadOnlySpan<byte> icmpBody;
                if (isIPv6)
                {
                    // IPv6 raw sockets don't include IP header
                    icmpBody = buffer.Slice(0, bytesReceived);
                }
                else
                {
                    // IPv4 includes IP header
                    if (bytesReceived < 20)
                    {
                        Console.WriteLine("Received packet is too small to process.");
                        return;
                    }

                    int totalLen = ParseInternetHeader(buffer, out IPAddress responseSrcIp, out IPAddress responseDestIp, out int ipHeaderSize, out icmpBody);
                    if (totalLen < 0)
                    {
                        Console.WriteLine("Failed to parse IP header from response.");
                        return;
                    }
                }

                if (icmpBody.Length < 1)
                {
                    Console.WriteLine("Received packet too small after IP header.");
                    return;
                }

                byte responseType = icmpBody[0];
                
                // Check for Echo Reply
                bool isEchoReply = (!isIPv6 && responseType == (byte)IcmpType.EchoReply) ||
                                   (isIPv6 && responseType == (byte)IcmpType.EchoReplyV6);
                
                // Check for Time Exceeded
                bool isTimeExceeded = (!isIPv6 && responseType == (byte)IcmpType.TimeExceeded) ||
                                      (isIPv6 && responseType == (byte)IcmpType.TimeExceededV6);
                
                // Check for Destination Unreachable
                bool isDestUnreachable = (!isIPv6 && responseType == (byte)IcmpType.DestinationUnreachable) ||
                                         (isIPv6 && responseType == (byte)IcmpType.DestinationUnreachableV6);

                if (isEchoReply)
                {
                    if (icmpBody.Length < sizeof(IcmpEchoPacket))
                    {
                        Console.WriteLine("Received ICMP Echo Reply packet is too small to process.");
                        return;
                    }

                    IcmpEchoPacket* echoReply = (IcmpEchoPacket*)Unsafe.AsPointer(ref Unsafe.AsRef(in icmpBody[0]));
                    if (echoReply->Identifier != 1 || echoReply->SequenceNum != 1 || echoReply->Data != echoData)
                    {
                        Console.WriteLine("Received ICMP Echo Reply packet data does not match sent packet.");
                        return;
                    }

                    Console.WriteLine($"{ttl}\t{routerIp} - Destination reached!");
                    routeHops.Add(routerIp);
                    completedTraversal = true;
                    break;
                }
                else if (isTimeExceeded)
                {
                    Console.WriteLine($"{ttl}\t{routerIp}");
                    routeHops.Add(routerIp);
                    Thread.Sleep(1000);
                    ttl++;
                }
                else if (isDestUnreachable)
                {
                    Console.WriteLine($"{ttl}\t{routerIp} - Destination Unreachable");
                    routeHops.Add(routerIp);
                    break;
                }
                else
                {
                    Console.WriteLine($"Received unexpected ICMP type: {responseType}");
                    return;
                }
            }

            Console.WriteLine($"\n****** Trace completed | Success: {completedTraversal} | Total hops: {routeHops.Count} ******");
            
            // Optional: Fetch geolocation data
            using HttpClient httpClient = new HttpClient();
            foreach (IPAddress hop in routeHops)
            {
                if (!IPAddress.IsLoopback(hop) && hop != IPAddress.Any && hop != IPAddress.IPv6Any)
                {
                    try
                    {
                        var response = httpClient.GetStringAsync($"http://ip-api.com/json/{hop}").GetAwaiter().GetResult();
                        Console.WriteLine($"{hop}: {response}");
                    }
                    catch { }
                }
            }
        }
        finally
        {
            socket.Close();
        }
    }

    private static ushort ComputeCheckSum(ReadOnlySpan<byte> data)
    {
        uint sum = 0;
        int i = 0;
        while (i < data.Length - 1)
        {
            sum += (uint)((data[i] << 8) | data[i + 1]);
            i += 2;
        }

        Debug.Assert(i == data.Length, "Index should be at the end");

        while ((sum >> 16) != 0)
        {
            sum = (sum & 0xFFFF) + (sum >> 16);
        }
        return (ushort)~sum;
    }

    private static int ParseInternetHeader(ReadOnlySpan<byte> data, out IPAddress sourceIp, out IPAddress destIp, out int ipHeaderSize, out ReadOnlySpan<byte> icmpBody)
    {
        sourceIp = IPAddress.None;
        destIp = IPAddress.None;
        ipHeaderSize = 0;
        icmpBody = ReadOnlySpan<byte>.Empty;

        if (data.Length < 20)
        {
            Console.WriteLine("Data too small to contain valid IP header.");
            return -1;
        }

        byte ihl = (byte)(data[0] & 0x0F);
        ipHeaderSize = ihl * 4;

        if (data.Length < ipHeaderSize)
        {
            Console.WriteLine($"Data too small for IP header size {ipHeaderSize}.");
            return -1;
        }

        InternetHeaderFormat ipHeader = MemoryMarshal.Read<InternetHeaderFormat>(data);

        sourceIp = new IPAddress(BitConverter.ToUInt32(data.Slice(InternetHeaderFormat.Size, 4)));
        destIp = new IPAddress(BitConverter.ToUInt32(data.Slice(InternetHeaderFormat.Size + 4, 4)));

        if (data.Length > ipHeaderSize)
        {
            icmpBody = data.Slice(ipHeaderSize);
        }

        int totalLen = ipHeader.TotalLength;
        return totalLen;
    }
}