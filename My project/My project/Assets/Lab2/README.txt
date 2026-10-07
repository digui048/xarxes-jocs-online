LAB 2 - SOCKETS (TCP / UDP)
XJO - Xarxes per a Jocs Online

WHAT IS IN THIS PACKAGE
  Assets/Lab2/Scenes/S_TCP_Server.unity      TCP server, 5 TODOs
  Assets/Lab2/Scenes/S_TCP_Client.unity      TCP client, 4 TODOs
  Assets/Lab2/Scenes/S_UDP_Server.unity      UDP server, 4 TODOs
  Assets/Lab2/Scenes/S_UDP_Client.unity      UDP client, 5 TODOs

  Assets/Lab2/Scripts/SocketsTCPServer.cs            what you fill in
  Assets/Lab2/Scripts/SocketsTCPClient.cs
  Assets/Lab2/Scripts/SocketsUDPServer.cs
  Assets/Lab2/Scripts/SocketsUDPClient.cs
  Assets/Lab2/Scripts/SocketsTCPServerSolution.cs    the finished version
  Assets/Lab2/Scripts/SocketsTCPClientSolution.cs
  Assets/Lab2/Scripts/SocketsUDPServerSolution.cs
  Assets/Lab2/Scripts/SocketsUDPClientSolution.cs

  Every scene has two GameObjects, the same way as the threads exercise of Lab 1:
      TCPServer              your version, active
      TCPServerSolution      the finished version, disabled
  Do the exercise on the active one. If you get stuck, disable yours, enable the solution one,
  and see what it does. Never leave both enabled: they would fight for the same port.

THE EXERCISE
  The client sends its user name. The server receives it and answers with the server name.
  Both sides print what happens, in the Console and on screen.
  Same behaviour in TCP and in UDP: what changes is how you get there.

HOW TO RUN TWO INSTANCES ON ONE PC
  1. File > Build Settings, add the server scene, and build it.
  2. Run the build: that is your server.
  3. In the Editor, open the client scene and press Play: that is your client.
  4. Server Ip stays 127.0.0.1 while both run on the same PC.
  (Or build both scenes and run the two executables.)

HOW TO RUN IT WITH A CLASSMATE
  Server: run the server scene and tell your classmate your IP ("ipconfig" in a terminal).
  Client: write that IP in the Server Ip field before pressing Connect.
  If nothing arrives, check the Windows Firewall.

BEFORE YOU START
  The project compiles and runs from minute one. Unfinished TODOs print a warning like
      [TODO 2] not done yet: AcceptClient() returned null
  so you can do them in any order and test after each one.

IF YOUR CODE DOES NOT WORK YET
  Use the four reference builds handed out with this package to test one side at a time:
      Lab2_TCP_Server, Lab2_TCP_Client, Lab2_UDP_Server, Lab2_UDP_Client
  Run the reference server against your client, or the reference client against your server.

WHAT TO HAND IN
  Nothing from this session. The graded deliverable is explained in session 2.2 and it starts
  from the code you write here.
