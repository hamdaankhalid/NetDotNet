// Initiator is the one who after BULLET packet is sent, sends SYN packet to Responder
// All states in Responder must be able to handle the arrival of any of these messages.
enum MessageTypeFromInitiator {
  Bullet, // it can send a bullet packet
  Syn, // it can send syn packet
  Ack, // it can send ack packet
  WaitingForSynAck // it can send that it is waiting for synack packet
}

// Messages sent by responder.
// All states in Initiator must be able to handle the arrival of any of these messages.
enum ResponderMessages {
  Initial, // waiting for syn
  SynAck, // synack received, this is the responder sending synack to initiator
  WaitingForAck // waiting for ack
}

type tInitiatorToResponder = (msg: MessageTypeFromInitiator, seqNum: int);

// messages from responder to initiator
type tResponderToInitiator = (msg: ResponderMessages, seqNum: int);

machine Initiator {
  var _lastSentSeq: int;
  var _lastPeerSeq: int;

  start state Init {
    entry (payload: any) {
      _lastSentSeq = 0;
      _lastPeerSeq = 0;
      goto SendSyn;
    }
  }

  state SendSyn {
  }

  state WaitingForSynAck {
  }

  state SendAck {
  }

  state EstablishedForInitiator {
  }
}

// Responder is the one who after receiving SYN packet from Initiator, sends SYN-ACK packet back

machine Responder {
  var _lastSentSeq: int;
  var _lastPeerSeq: int;

  start state Init {
    entry (payload: any) {
      _lastSentSeq = 0;
      _lastPeerSeq = 0;
      goto WaitingForSyn;
    }
  }

  state WaitingForSyn {
    // publish that it is waiting for SYN
  }

  state SendSynAck {
  }

  state WaitingForAck {
  }

  state EstablishedForResponder {
  }
}