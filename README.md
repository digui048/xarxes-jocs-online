# XarxesSocketAndEveryoneElse

Lab 2 (Deliverable 2) of Xarxes i Jocs Online: a lobby with a waiting room and a chat, working over TCP and over UDP.

## How to run it

1. Run `MainMenu.exe` once for each player. You can also open the project in Unity 6000.3.25f1, open `Assets/Scenes/MainMenu` and press Play.
2. Pick the protocol at the top, TCP or UDP. Every player has to pick the same one.
3. One player hosts: write a name and a port (9050 by default) and press Host.
4. The other players join: write a name, the IP of the host and the same port, and press Join.
   - On the same PC, leave `127.0.0.1`.
   - On another PC in the same network, use the IP shown in the "Your IP" field of the host. The host may have to allow the game through the Windows Firewall.
5. In the waiting room, write in the box and press Enter or Send to chat. Press Leave to go back to the menu.

## What we built

- A menu to create a game (Host) or to join one (Join).
- A waiting room with the list of players. It updates every time somebody joins or leaves.
- A chat where every line reaches all the players in the room. It also says when a player leaves.
- All of it in TCP and in UDP.
- In UDP, each client sends a ping every second. If the server hears nothing from a client for 5 seconds, it removes it from the list.

The code is in `My project/My project/Assets/Lab2/Scripts`: the TCP and UDP servers and clients, and `LobbyUI` for the menu and the waiting room.

## Bonus parts

- The host also plays: it appears in the list and can chat.

## Known problems

- We thought the UDP timeout was not working. The assignment says that when a client closes its window or crashes, no LEAVE arrives, so the server needs a timeout. But when we closed a client window, the chat showed "Player left" right away instead of waiting 5 seconds. In the end the timeout does work: closing the window normally still sends LEAVE. If you end the client from Task Manager > Details > End Task, it cannot send anything, and after about 5 seconds the chat shows "Player X timed out".
- In UDP, if the host disappears, players on other PCs may stay in the waiting room, because nothing tells them that the server is gone. In TCP they are sent back to the menu.
- Two players can join with the same name.
- The chat only shows the latest lines. You cannot scroll back to the older ones.
