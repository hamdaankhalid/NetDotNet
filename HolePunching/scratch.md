State machines doing connection handshake use a store to publish information such that we know for sure it is reachable by another peer.

The semantics we want is that A says I can see you B, and waits till B says I can see you A
Then both can upgrade their states to established

A upon recieving B's packets updates the state store saying B I see you at this sessionID X, with my sessionID as Y.
Now it must wait for B to say I see you with this sessionID J, and my sessionID K.

If X == K AND Y == J then the session was established for the 2 same peers that are online right now.
this should prevent a situation where A could see B at a previous version say Y, then times out and while B has not timed out (it's version remains the same), B thinks A has been seen seen before even though this a brand new connection start by A that might not have penetrated the NAT.

The session that penetrated the NAT must be the one seen by both parties.

NAT bullets must therefore be sending session Ids.