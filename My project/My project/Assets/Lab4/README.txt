LAB 4 - NAT CHECK
XJO - Xarxes per a Jocs Online

WHAT IS IN THIS HANDOUT
  Scenes/S_NatCheck        one scene. Press Play
  Scripts/NatCheck.cs      the whole tool. No TODOs: read it, it is the code of slides 18 to 20

WHAT IT DOES
  Opens one UDP socket and asks two public STUN servers which address they see:
    stun.l.google.com:19302    stun.cloudflare.com:3478
  Then compares that with your private address (the one ipconfig shows) and tells you:
    - No NAT: this PC has a public address
    - Behind NAT, same public port from both servers: "cone" NAT, hole punching would work
    - Behind NAT, a different port per server: symmetric NAT, a relay is needed
    - No answer: this network blocks outgoing UDP to STUN

IN CLASS (slide 28)
  Run ipconfig in a terminal, then press Play. Compare with your neighbour:
    Is your ipconfig address private? Is there NAT between you and the Internet?
    Do you and your neighbour get the same public address? Why?
    Same public port from both servers? What type of NAT is it?
    Could your neighbour host a game that someone at home joins?
  Try it at home too: the answer is usually different.

NOTHING TO HAND IN
  Deliverable 3 (mid-term demo) is explained in slides 30 to 40.
