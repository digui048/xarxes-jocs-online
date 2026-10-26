// =================================================================================================
//  Lab 2 - Sockets: TCP SERVER
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  Session 2.2: the server is now a lobby with a player list and a chat.
//  Scene: S_TCP_Server. The client is in S_TCP_Client.
//
//  MESSAGES (TYPE:content. Since Lab 3 each one travels as a Text message, see Lab3 Messages.cs)
//    JOIN:Anna          client -> server   register the player
//    PLAYERS:Anna,Marc  server -> all      full player list, sent on every join and every leave
//    CHAT:hello         client -> server   a chat line
//    CHAT:Anna: hello   server -> all      the same line with the name of who wrote it
//    LEAVE:             client -> server   the player leaves the room
//
//  HOW TO RUN IT
//    Build the project with S_TCP_Server, or press Play with this scene open.
//    Then run the client on the other side. Both on the same PC: IP 127.0.0.1.
//
//  The threads, the framing ([4-byte length][payload]) and the Update() pump come from session 2.1.
//  The lobby code is at the bottom of the file.
//
// =================================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class SocketsTCPServer : MonoBehaviour
{
    public int port = 9050;
    public string serverName = "MyServer";
    public bool autoStart = true;
    public bool showDebugView = true;   // the OnGUI text below. A scene with its own UI turns it off

    // =============================================================================================
    //  NETWORK CODE - threads, framing and the Update() pump from session 2.1
    // =============================================================================================

    const int MaxPacketSize = 64 * 1024;

    // disconnected = true is not a real packet: it tells Update() that this socket is gone
    struct Packet { public byte[] data; public Socket from; public bool disconnected; }

    Socket m_listener;                                        // only accepts connections
    readonly List<Socket> m_clients = new List<Socket>();     // one socket per connected client
    readonly Dictionary<Socket, string> m_players = new Dictionary<Socket, string>();   // who sent JOIN
    readonly object m_playersLock = new object();
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<Packet> m_inbox = new ConcurrentQueue<Packet>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;
    volatile bool m_listening;

    public bool IsRunning { get { return m_running; } }
    public bool IsListening { get { return m_running && m_listening; } }   // the socket is bound
    public int PlayerCount { get { lock (m_playersLock) return m_players.Count; } }

    void Start()
    {
        Application.runInBackground = true;   // or the window without focus is paused
        ParseCommandLine();
        if (autoStart) StartNetwork();
    }

    public void StartNetwork()
    {
        if (m_running) return;
        m_running = true;
        StartThread(ServerThread);
    }

    // Closing the sockets is what unblocks the threads waiting in Accept() or Receive() (slide 14).
    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;
        m_listening = false;

        Socket[] clients;
        lock (m_clients) { clients = m_clients.ToArray(); m_clients.Clear(); }
        foreach (Socket c in clients) CloseSocket(c);

        lock (m_playersLock) m_players.Clear();

        CloseSocket(m_listener); m_listener = null;

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);

        Log("[SERVER] Stopped");
    }

    void OnDestroy() { Disconnect(); }

    // Main thread: the only safe place to touch the Unity API.
    void Update()
    {
        Packet packet;
        while (m_inbox.TryDequeue(out packet))
        {
            if (packet.disconnected) RemovePlayer(packet.from);
            else OnPacketReceived(packet.data, packet.from);
        }
    }

    void ServerThread()
    {
        try { m_listener = StartServer(); }                                          // TODO 1
        catch (SocketException e)
        {
            Log("[SERVER] Could not start: " + e.SocketErrorCode +
                (e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? " (port " + port + " is still used by another instance, slide 34)" : ""));
            m_running = false;   // nothing was started, so whoever asked can see it and try again
            return;
        }

        if (m_listener == null) { WarnTodo(1, "StartServer() returned null"); return; }
        Log("[SERVER] Listening on " + port);
        m_listening = true;

        while (m_running)
        {
            Socket client;
            try { client = AcceptClient(); }                                         // TODO 2
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            if (client == null) { WarnTodo(2, "AcceptClient() returned null"); return; }

            lock (m_clients) m_clients.Add(client);
            Log("[SERVER] Client connected: " + client.RemoteEndPoint);

            Socket captured = client;
            StartThread(delegate { ClientThread(captured); });
        }
    }

    // One of these per connected client: in TCP every client has its own socket (slide 8).
    void ClientThread(Socket client)
    {
        ReceiveLoop(client);
        lock (m_clients) m_clients.Remove(client);
        Log("[SERVER] Client disconnected");
        CloseSocket(client);

        // The player list is handled in Update(), so the disconnect goes through the queue too.
        // It ends up behind the packets this client sent before closing, so the order is kept.
        m_inbox.Enqueue(new Packet { from = client, disconnected = true });
    }

    // Sends one packet to one client. Lab 3 will call it with serialized data instead of text.
    public void SendPacket(byte[] payload, Socket to)
    {
        if (to == null || payload == null) return;

        byte[] framed = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(framed, 0);
        payload.CopyTo(framed, 4);

        try
        {
            int sent = SendRaw(to, framed);                                          // TODO 3
            if (sent < 0) WarnTodo(3, "SendRaw() is not implemented yet");
        }
        catch (SocketException e) { Log("[SERVER] Send failed: " + e.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    // Lab 3: the text goes out as a Text message, with its type byte in front (Messages.cs).
    public void SendString(string text, Socket to)
    {
        SendPacket(Messages.Text(text), to);
    }

    void ReceiveLoop(Socket socket)
    {
        byte[] header = new byte[4];
        while (m_running)
        {
            if (!ReadExactly(socket, header, 4)) return;

            int size = BitConverter.ToInt32(header, 0);
            if (size <= 0 || size > MaxPacketSize) { Log("[SERVER] Invalid packet size: " + size); return; }

            byte[] payload = new byte[size];
            if (!ReadExactly(socket, payload, size)) return;

            m_inbox.Enqueue(new Packet { data = payload, from = socket });
        }
    }

    // One Receive() can return less than you asked for, so we insist until we have it all.
    bool ReadExactly(Socket socket, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read;
            try { read = ReceiveRaw(socket, buffer, total, count - total); }         // TODO 4
            catch (SocketException e) { Log("[SERVER] Connection lost: " + e.SocketErrorCode); return false; }
            catch (ObjectDisposedException) { return false; }

            if (read < 0) { WarnTodo(4, "ReceiveRaw() is not implemented yet"); return false; }
            if (read == 0) return false;   // 0 bytes = the client closed the connection (slide 8)
            total += read;
        }
        return true;
    }

    void StartThread(ThreadStart work)
    {
        Thread t = new Thread(work);
        t.IsBackground = true;
        lock (m_threads) m_threads.Add(t);
        t.Start();
    }

    void CloseSocket(Socket socket)
    {
        if (socket == null) return;
        try { socket.Shutdown(SocketShutdown.Both); } catch { }
        try { socket.Close(); } catch { }
    }

    public void Log(string message)
    {
        Debug.Log(message);
        lock (m_log) { m_log.Add(message); if (m_log.Count > 100) m_log.RemoveAt(0); }
    }

    void WarnTodo(int number, string detail)
    {
        lock (m_warned) { if (!m_warned.Add(number)) return; }
        Log("[TODO " + number + "] not done yet: " + detail);
    }

    void ParseCommandLine()
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "-port" && i + 1 < args.Length) int.TryParse(args[++i], out port);
            else if (args[i] == "-name" && i + 1 < args.Length) serverName = args[++i];
            else if (args[i] == "-autostart") autoStart = true;
            else if (args[i] == "-noautostart") autoStart = false;
        }
    }

    // Temporary debug view, so you can also see what happens in a build.
    // In the deliverable you replace it with your own scenes and UI.
    void OnGUI()
    {
        if (!showDebugView) return;

        GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
        GUILayout.Label("TCP SERVER   port " + port + "   name " + serverName);

        if (!m_running) { if (GUILayout.Button("Start server", GUILayout.Width(110))) StartNetwork(); }
        else if (GUILayout.Button("Stop", GUILayout.Width(110))) Disconnect();

        GUILayout.Label("Players (" + PlayerCount + "): " + string.Join(", ", GetPlayerNames()));

        lock (m_log)
            for (int i = Mathf.Max(0, m_log.Count - 18); i < m_log.Count; i++)
                GUILayout.Label(m_log[i]);

        GUILayout.EndArea();
    }

    // =============================================================================================
    //  TODO AREA - this is the only part you have to write
    // =============================================================================================

    // --------------------------------------------------------------------------------- TODO 1 ---
    //  Goal: leave the server socket ready to receive connections on 'port', and return it.
    //  Three steps: create a TCP socket, give it a local address, and put it in listening mode.
    //  Look it up: slide 8 (TCP sequence), slide 6 (who binds and why).
    //      Socket      https://learn.microsoft.com/dotnet/api/system.net.sockets.socket
    //      IPEndPoint  https://learn.microsoft.com/dotnet/api/system.net.ipendpoint
    //  Think: which IP does a server listen on if you want a classmate's PC to reach it? (slide 5)
    //  Console when it works:  [SERVER] Listening on 9050
    Socket StartServer()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));   // Any: reachable from other machines
        socket.Listen(10);                                  // 10 = pending connections queue
        return socket;
    }

    // --------------------------------------------------------------------------------- TODO 2 ---
    //  Goal: wait for one client and return the socket that talks to it.
    //  Careful: it is NOT the listener. Check the sequence diagram (slide 8) to see which socket
    //  the data travels through.
    //  It blocks until somebody connects, which is fine: we are on a network thread.
    //  Console when it works:  [SERVER] Client connected: 127.0.0.1:54xxx
    Socket AcceptClient()
    {
        return m_listener.Accept();   // replace with the socket of the accepted client
    }

    // --------------------------------------------------------------------------------- TODO 3 ---
    //  Goal: send 'data' through 'socket' and return how many bytes were sent.
    //  It is already framed, so send the whole array. No address needed here, and slide 10 says why.
    //  Look it up: Socket.Send in the docs. It returns the number of bytes sent.
    int SendRaw(Socket socket, byte[] data)
    {
        return socket.Send(data);   // replace with the number of bytes sent
    }

    // --------------------------------------------------------------------------------- TODO 4 ---
    //  Goal: read from 'socket' into 'buffer' at position 'offset', at most 'count' bytes, and
    //  return how many bytes you actually read.
    //  Look it up: Socket.Receive, the overload that takes an offset and a size.
    //  Return the real number: the given code already knows that 0 means "connection closed".
    int ReceiveRaw(Socket socket, byte[] buffer, int offset, int count)
    {
        return socket.Receive(buffer, offset, count, SocketFlags.None);   // replace with the number of bytes read
    }

    // =============================================================================================
    //  LOBBY - everything below runs in Update(), on the main thread
    // =============================================================================================

    // Lab 3: the first byte says what the packet is (slide 18). The lobby messages are Text, and
    // OnTextReceived() handles them exactly as in Lab 2.
    void OnPacketReceived(byte[] data, Socket from)
    {
        MsgType msgType;
        BinaryReader reader;
        if (!Messages.Open(data, out msgType, out reader))
        {
            Log("[SERVER] Dropped a packet of an unknown type (" + data.Length + " bytes)");
            return;
        }

        switch (msgType)
        {
            case MsgType.Text:
                string text = Messages.ReadText(reader);
                if (text != null) OnTextReceived(text, from);
                else Log("[SERVER] Dropped a broken Text message");
                break;
            default:
                Log("[SERVER] No code for " + msgType + " messages yet, dropped");
                break;
        }
    }

    void OnTextReceived(string text, Socket from)
    {
        // Only the first ':' separates type and content, a chat line can have more of them.
        int separator = text.IndexOf(':');
        string type = separator < 0 ? text : text.Substring(0, separator);
        string content = separator < 0 ? "" : text.Substring(separator + 1);

        switch (type)
        {
            case "JOIN": OnJoin(content, from); break;
            case "CHAT": OnChat(content, from); break;
            case "LEAVE": RemovePlayer(from); break;
            case "PING": break;   // only needed in UDP, here Receive already tells us who is gone
            default: Log("[SERVER] Unknown message: " + text); break;
        }
    }

    void OnJoin(string name, Socket from)
    {
        name = name.Trim().Replace(",", "");   // a comma would break the PLAYERS list
        if (name.Length == 0) name = "Player";

        // Indexer and not Add(): a second JOIN from the same socket renames the player instead of
        // adding it twice.
        lock (m_playersLock) m_players[from] = name;

        Log("[SERVER] " + name + " joined");
        BroadcastPlayers();
    }

    void OnChat(string message, Socket from)
    {
        string name;
        bool isPlayer;
        lock (m_playersLock) isPlayer = m_players.TryGetValue(from, out name);

        if (!isPlayer || message.Trim().Length == 0) return;   // not in the room, or nothing to say

        Log("[SERVER] " + name + ": " + message);
        Broadcast("CHAT:" + name + ": " + message);
    }

    // Used for LEAVE: and for a closed connection. A client that sends LEAVE: and then closes gets
    // here twice, but the second time it is not in the dictionary and nothing is sent.
    void RemovePlayer(Socket socket)
    {
        string name;
        bool wasPlayer;
        lock (m_playersLock)
        {
            wasPlayer = m_players.TryGetValue(socket, out name);
            if (wasPlayer) m_players.Remove(socket);
        }
        if (!wasPlayer) return;

        Log("[SERVER] " + name + " left");
        BroadcastPlayers();

        // Also as a chat line with no sender, so the players that stay can read who left.
        Broadcast("CHAT:" + name + " left");
    }

    // Names in the order the clients connected. The dictionary alone does not keep any order.
    public string[] GetPlayerNames()
    {
        Socket[] clients;
        lock (m_clients) clients = m_clients.ToArray();

        List<string> names = new List<string>();
        lock (m_playersLock)
        {
            foreach (Socket c in clients)
            {
                string name;
                if (m_players.TryGetValue(c, out name)) names.Add(name);
            }
        }
        return names.ToArray();
    }

    void BroadcastPlayers()
    {
        Broadcast("PLAYERS:" + string.Join(",", GetPlayerNames()));
    }

    // The accept thread and the client threads change m_clients while we are here, so we copy the
    // list inside the lock and send outside it.
    void Broadcast(string text)
    {
        Socket[] targets;
        lock (m_clients) targets = m_clients.ToArray();
        byte[] packet = Messages.Text(text);   // built once, the same bytes go to everybody
        foreach (Socket c in targets) SendPacket(packet, c);
    }
}
