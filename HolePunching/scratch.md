Problem 1 in this model is that we would need to detect who is "intiator" and who is "listener"
Syn           -> 
              <-  Syn-Ack
Ack           ->
              <- Established
Established

Problem 2 is that we need both to be sending messages and we can't stop till both can send the data

In syn ack model we have an initiator and a responder, but this won't work for opening the NAT hole

What if both sides open holes and are sending punch packets

Now the issue is to understand how do we know if we can stop

After 5 seconds of sending packets maybe we can do our syn-ack-synack phase to make sure that atleast one recv was recvd in that 5 seconds
if not keep trying.
In the above case the issue is that 

Why do syn-ack-synack after 5 second spam based filtering? can every control packet be sent with some level of session identificaiton

Maybe I can do ignored pings as a background thread job?