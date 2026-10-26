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
    public bool showDebugView = true;

    const int MaxPacketSize = 64 * 1024;

    // disconnected: the socket closed and there is no data
    struct Packet { public byte[] data; public Socket from; public bool disconnected; }

    Socket m_listener;
    readonly List<Socket> m_clients = new List<Socket>();
    readonly Dictionary<Socket, string> m_players = new Dictionary<Socket, string>();
    readonly object m_playersLock = new object();
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<Packet> m_inbox = new ConcurrentQueue<Packet>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;
    volatile bool m_listening;

    public bool IsRunning { get { return m_running; } }
    public bool IsListening { get { return m_running && m_listening; } }
    public int PlayerCount { get { lock (m_playersLock) return m_players.Count; } }

    void Start()
    {
        Application.runInBackground = true;   // keep running without focus
        ParseCommandLine();
        if (autoStart) StartNetwork();
    }

    public void StartNetwork()
    {
        if (m_running) return;
        m_running = true;
        StartThread(ServerThread);
    }

    // Closing the sockets unblocks the threads waiting in Accept() and Receive()
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
        try { m_listener = StartServer(); }
        catch (SocketException e)
        {
            Log("[SERVER] Could not start: " + e.SocketErrorCode +
                (e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? " (port " + port + " is still used by another instance)" : ""));
            m_running = false;
            return;
        }

        if (m_listener == null) { WarnTodo(1, "StartServer() returned null"); return; }
        Log("[SERVER] Listening on " + port);
        m_listening = true;

        while (m_running)
        {
            Socket client;
            try { client = AcceptClient(); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

            if (client == null) { WarnTodo(2, "AcceptClient() returned null"); return; }

            lock (m_clients) m_clients.Add(client);
            Log("[SERVER] Client connected: " + client.RemoteEndPoint);

            Socket captured = client;
            StartThread(delegate { ClientThread(captured); });
        }
    }

    void ClientThread(Socket client)
    {
        ReceiveLoop(client);
        lock (m_clients) m_clients.Remove(client);
        Log("[SERVER] Client disconnected");
        CloseSocket(client);

        // Queued so the player is removed on the main thread, after its last packets
        m_inbox.Enqueue(new Packet { from = client, disconnected = true });
    }

    public void SendPacket(byte[] payload, Socket to)
    {
        if (to == null || payload == null) return;

        byte[] framed = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(framed, 0);
        payload.CopyTo(framed, 4);

        try
        {
            int sent = SendRaw(to, framed);
            if (sent < 0) WarnTodo(3, "SendRaw() is not implemented yet");
        }
        catch (SocketException e) { Log("[SERVER] Send failed: " + e.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

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

    // Receive() can return fewer bytes than asked for
    bool ReadExactly(Socket socket, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read;
            try { read = ReceiveRaw(socket, buffer, total, count - total); }
            catch (SocketException e) { Log("[SERVER] Connection lost: " + e.SocketErrorCode); return false; }
            catch (ObjectDisposedException) { return false; }

            if (read < 0) { WarnTodo(4, "ReceiveRaw() is not implemented yet"); return false; }
            if (read == 0) return false;   // connection closed
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

    Socket StartServer()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));   // Any: reachable from other machines
        socket.Listen(10);                                  // pending connections queue
        return socket;
    }

    Socket AcceptClient()
    {
        return m_listener.Accept();
    }

    int SendRaw(Socket socket, byte[] data)
    {
        return socket.Send(data);
    }

    int ReceiveRaw(Socket socket, byte[] buffer, int offset, int count)
    {
        return socket.Receive(buffer, offset, count, SocketFlags.None);
    }

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
            case MsgType.Player:
                PlayerDTO2 dto;
                if (Messages.ReadPlayer(reader, out dto)) OnPlayerData(dto);
                else Log("[SERVER] Dropped a broken Player message");
                break;
            default:
                Log("[SERVER] Unexpected " + msgType + " message, dropped");
                break;
        }
    }

    void OnTextReceived(string text, Socket from)
    {
        // A chat line can contain ':' too, so split on the first one
        int separator = text.IndexOf(':');
        string type = separator < 0 ? text : text.Substring(0, separator);
        string content = separator < 0 ? "" : text.Substring(separator + 1);

        switch (type)
        {
            case "JOIN": OnJoin(content, from); break;
            case "CHAT": OnChat(content, from); break;
            case "LEAVE": RemovePlayer(from); break;
            case "PING": break;   // only used by UDP
            default: Log("[SERVER] Unknown message: " + text); break;
        }
    }

    void OnJoin(string name, Socket from)
    {
        name = name.Trim().Replace(",", "");   // a comma would break the PLAYERS list
        if (name.Length == 0) name = "Player";

        // A second JOIN renames the player instead of adding it twice
        lock (m_playersLock) m_players[from] = name;

        Log("[SERVER] " + name + " joined");
        BroadcastPlayers();
    }

    void OnChat(string message, Socket from)
    {
        string name;
        bool isPlayer;
        lock (m_playersLock) isPlayer = m_players.TryGetValue(from, out name);

        if (!isPlayer || message.Trim().Length == 0) return;

        Log("[SERVER] " + name + ": " + message);
        Broadcast("CHAT:" + name + ": " + message);
    }

    void OnPlayerData(PlayerDTO2 dto)
    {
        List<string> pokemons = new List<string>();
        foreach (Pokemon p in dto.ownedPokemons) pokemons.Add(p.name);

        Log("[SERVER] Player data from " + dto.playerName + ": level " + dto.level +
            ", Pokemon: " + (pokemons.Count > 0 ? string.Join(", ", pokemons) : "none"));
    }

    // LEAVE: and a closed socket both end here, the second call finds nothing
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
        Broadcast("CHAT:" + name + " left");
    }

    // In connection order, the dictionary has none
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

    // Copy inside the lock, send outside it
    void Broadcast(string text)
    {
        Socket[] targets;
        lock (m_clients) targets = m_clients.ToArray();
        byte[] packet = Messages.Text(text);
        foreach (Socket c in targets) SendPacket(packet, c);
    }
}
