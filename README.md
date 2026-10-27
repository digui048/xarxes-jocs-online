# XarxesSocketAndEveryoneElse

Our project for Xarxes i Jocs Online:

- Lab 4 (Deliverable 3): Leaf Leap, a small climbing race for 2 or more players over UDP. It starts from the lobby. It is the first section.
- Lab 2 (Deliverable 2): a lobby with a waiting room and a chat, working over TCP and over UDP.
- Lab 3: binary serialization of the player data and of every message.

## Project Deliverable: Leaf Leap

Every player is a capsule at the bottom of its own beanstalk, and all the beanstalks are the same. Each leaf is on the left or on the right of the stalk. Pressing the side of the next leaf climbs to it, and pressing the wrong side makes you fall. After 30 seconds, the highest player wins.

### How to run it

1. Run the game's .exe once for each player. You can also open the project in Unity 6000.3.25f1, open `Assets/Scenes/MainMenu` and press Play.
2. Pick UDP at the top. The game only works over UDP: a TCP room has the chat, but no Start button.
3. One player hosts and the others join, the same way as in the lobby (see Lab 2 below). On another PC use the IP shown in "Your IP", and allow the game through the Windows Firewall.
4. When everybody is in the waiting room, the host presses Start, and every player moves to the game.
5. Controls:
   - A or Left arrow: jump to the leaf on the left.
   - D or Right arrow: jump to the leaf on the right.
   - Space: super jump, when the bar at the bottom is full.
   - Leave, at the top left: back to the menu.
6. When the round ends, everybody sees the results. The host can press Play again to start another round with a new beanstalk.

### Main scene

`Assets/Scenes/MainMenu` is the scene to start from, and the first one in the build. The game is `Assets/Lab4/Scenes/LeafLeap`, which the lobby loads when the host presses Start. If you open LeafLeap directly, it sends you back to the menu, because there is no connection yet.

The game code is in `My project/My project/Assets/Lab4/Scripts`: `LeafLeapGame` for the game and `GameDTO` for the data of the new messages. The network code is still in `Lab2/Scripts`, and the messages in `Lab3/Scripts`.

### State or input

We send the state. 20 times a second, each player sends where its capsule is and which way it faces, in a `Transform` message.

Why:

- The game is about pressing fast. With the state, your own capsule jumps the moment you press the key, without waiting for the server.
- A lost state does not matter, because the next one arrives 50 ms later and replaces it. With input, a lost "jump left" would leave the server with the capsule on a different leaf than the one the player sees.
- The other PCs only have to move each capsule towards its last position, so the climbing code only runs on the PC of its owner.

How it travels:

- Each client sends its `Transform` to the server. The server keeps the last one of each player and sends it to all the others.
- The server only accepts the state of the capsule that belongs to that client, so nobody can move someone else's capsule.
- The host plays too. Its game has a client connected to its own server, so the host's capsule is the object the server controls, and it reaches the others the same way.
- The downside of sending the state is that a modified client could send any height. For this game we accept it.

### Messages

Same rules as in Lab 3: a type byte first, and then the data written with `BinaryWriter`. Lab 4 adds three types:

| Byte | Type | Content | Who sends it |
|---|---|---|---|
| 3 | Transform | id, x, y, z and rotation (21 bytes with the type byte) | Each client to the server, and the server to the others |
| 4 | GameStart | seed, round length, your id, how many players, and then the id and name of each one | The server to each client, when the host presses Start or Play again |
| 5 | GameOver | how many players, and then the id and metres of each one, highest first | The server to everyone, when the round ends |

- The beanstalk is not sent. Every PC builds it from the seed in GameStart, so it is the same on every screen.
- The `PING:` of the lobby keeps going during the game. The server still drops players who disappear, and the NAT row of each player stays open.

### Extras

- Jump. All the movement is jumping: every key press is a jump with an arc to the next leaf, and the capsule turns to the side it jumps to (the dark visor shows it). A jump to the wrong side makes it fall to the first leaf below on that side, or to the ground, and it cannot move for a moment. The state is sent during the whole jump, so the others see the arcs and the falls too.
- Other actions: the super jump. 10 right jumps in a row fill the bar at the bottom of the screen, and then Space jumps 3 leaves at once. A wrong jump empties the bar.
- More than 2 players, all seeing each other. Every player in the room gets a beanstalk and a colour, and sees all the other capsules move. We tested it with 3 players.
- A complete game experience, even with lag, with a win condition and a restart:
  - Each round lasts 30 seconds, with a timer at the top.
  - When the time is up, the host decides the ranking from the last state of every player (each leaf is 10 m) and sends it in a GameOver. Everybody sees the results and the winner. If several players end at the same height, they all win.
  - The host can press Play again: everyone gets a new beanstalk and a new round starts. The others see "Waiting for the host to play again".
  - With lag or lost packets:
    - GameStart is sent 3 times, 0.2 s apart, so one lost copy does not leave a player behind. The copies of a round that already started are ignored.
    - GameOver is sent again every second until the next round.
    - The host waits half a second after the end before deciding, so the last states can arrive.
    - The other capsules glide towards their last state instead of jumping from one to the next.
    - A capsule that gets no states for 3 seconds is hidden, because that player has left.
    - If the connection with the server is lost, the player goes back to the menu with a message.

### Known bugs

- If the host closes the game, the others only notice it if their PC gets an error back from the host's PC, and between two PCs this does not always happen. Then they stay in the game: the round ends, but the results never arrive. Leave takes them back to the menu.
- A player who joins while a round is being played waits in the waiting room until the host presses Play again.
- The camera does not move sideways. With more than 6 players, or in a window that is taller than wide, the beanstalks at the sides can be outside the screen. With more than 6 players the colours also repeat.
- With a lot of lag, the host can decide the results before the last jump of a player reaches it, so that jump may not count.
- Builds from Deliverable 2 or Lab 3 cannot join a room of this version.

### What changed since Deliverable 2

- Lab 3: every message starts with a type byte, and the data travels in binary. When joining, each player sends its level and its Pokemon team (`PlayerDTO2`), and the waiting room shows them. This works in TCP and in UDP.
- The host has a Start button in UDP rooms.
- Three new messages: Transform, GameStart and GameOver.
- The new game scene, LeafLeap.
- The client, and the server on the host, are kept when the scene changes, so the lobby and the game use the same UDP socket and the same keep-alive. Leaving the game closes them and goes back to the menu.
- The menu and the game screen fit any window size.
- We removed the Solution scripts of the Lab 2 handout, which the project did not use.

### What each member did

- Mario:
  - Lab 3: the type byte in front of every lobby message, in the four socket scripts, and the `Messages` class that adds it and reads it.
  - Lab 4: the Start button, the GameStart message, moving every player to the game scene while keeping the connection, and the LeafLeap scene with one beanstalk per player built from the seed.
- Didac:
  - Lab 3: joining with the `PlayerDTO2` (Player message) in TCP and UDP, and the server sending the data of every player to everyone.
  - Lab 4: climbing with left and right, falling after a wrong jump, the Transform message 20 times a second, the server passing each state to the others, and the smooth movement of the other capsules.
- Ton:
  - Lab 3: the `Serializer` (binary and JSON) with its test scene, and the level and team of each player in the menu and in the waiting room.
  - Lab 4: the 30-second round, the results and the winner (GameOver message), Play again, sending GameStart and GameOver more than once so they arrive, the super jump with its charge bar, the top bar that always stays on screen, and removing the unused Solution scripts.

## Lab 2: lobby and chat (Deliverable 2)

### How to run it

1. Run `MainMenu.exe` once for each player. You can also open the project in Unity 6000.3.25f1, open `Assets/Scenes/MainMenu` and press Play.
2. Pick the protocol at the top, TCP or UDP. Every player has to pick the same one.
3. One player hosts: write a name and a port (9050 by default) and press Host.
4. The other players join: write a name, the IP of the host and the same port, and press Join.
   - On the same PC, leave `127.0.0.1`.
   - On another PC in the same network, use the IP shown in the "Your IP" field of the host. The host may have to allow the game through the Windows Firewall.
5. In the waiting room, write in the box and press Enter or Send to chat. Press Leave to go back to the menu.

### What we built

- A menu to create a game (Host) or to join one (Join).
- A waiting room with the list of players. It updates every time somebody joins or leaves.
- A chat where every line reaches all the players in the room. It also says when a player leaves.
- All of it in TCP and in UDP.
- In UDP, each client sends a ping every second. If the server hears nothing from a client for 5 seconds, it removes it from the list.

The code is in `My project/My project/Assets/Lab2/Scripts`: the TCP and UDP servers and clients, and `LobbyUI` for the menu and the waiting room.

### Bonus parts

- The host also plays: it appears in the list and can chat.

### Known problems

- We thought the UDP timeout was not working. The assignment says that when a client closes its window or crashes, no LEAVE arrives, so the server needs a timeout. But when we closed a client window, the chat showed "Player left" right away instead of waiting 5 seconds. In the end the timeout does work: closing the window normally still sends LEAVE. If you end the client from Task Manager > Details > End Task, it cannot send anything, and after about 5 seconds the chat shows "Player X timed out".
- In UDP, if the host disappears, players on other PCs may stay in the waiting room, because nothing tells them that the server is gone. In TCP they are sent back to the menu.
- Two players can join with the same name.
- The chat only shows the latest lines. You cannot scroll back to the older ones.

## Lab 3: data serialization

### What we added

- `Serializer` turns a `PlayerDTO` and a `PlayerDTO2` (name, level and a list of Pokemon) into bytes with `BinaryWriter`, and back with `BinaryReader`. It also does the same with JSON to compare: the test player is 90 bytes in JSON and 31 in binary.
- To check it, open `Assets/Lab3/Scenes/S_SerializationTest` and press Play. Every test says PASS.
- In the lobby menu, each player sets a level and a list of Pokemon. The waiting room shows the level and the team of every player.

### Message format

Every message starts with one byte that says what it is:

| Byte | Type | Content |
|---|---|---|
| 1 | Text | A string written with `BinaryWriter`: `CHAT:hello`, `PLAYERS:Anna,Marc`, `LEAVE:` or `PING:` |
| 2 | Player | A `PlayerDTO2`: the name, the level, how many Pokemon, and then the name of each one |

- To join, a client sends its `PlayerDTO2`. If it sends it again later, the server updates its data.
- Every time a player joins, leaves or changes its data, the server sends the `PlayerDTO2` of every player to everyone, and then the `PLAYERS:` list.
- In TCP every message also has its length in front, in 4 bytes. In UDP one datagram is one message.
- Byte order: both ends use `BinaryWriter` and `BinaryReader`, which are always little-endian, so nothing has to be converted.

### Known problems

- Builds from Lab 2 and builds from Lab 3 cannot play together, because the messages are different. Everyone needs a build of the same version.
- If two players use the same name, both show the same level and team in the list.