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
    readonly Dictionary<Socket, PlayerDTO2> m_players = new Dictionary<Socket, PlayerDTO2>();
    readonly object m_playersLock = new object();
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<Packet> m_inbox = new ConcurrentQueue<Packet>();
    readonly List<string> m_log = new List<string>();
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
        Log("[SERVER] Listening on " + port);
        m_listening = true;

        while (m_running)
        {
            Socket client;
            try { client = AcceptClient(); }
            catch (SocketException) { break; }
            catch (ObjectDisposedException) { break; }

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
            SendRaw(to, framed);
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
                if (Messages.ReadPlayer(reader, out dto)) OnJoin(dto, from);
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
            case "CHAT": OnChat(content, from); break;
            case "LEAVE": RemovePlayer(from); break;
            case "PING": break;   // only used by UDP
            default: Log("[SERVER] Unknown message: " + text); break;
        }
    }

    // The PlayerDTO2 is the join request. From a player already in the room it is an update
    void OnJoin(PlayerDTO2 dto, Socket from)
    {
        dto.playerName = dto.playerName.Trim().Replace(",", "");   // a comma would break the PLAYERS list
        if (dto.playerName.Length == 0) dto.playerName = "Player";

        bool isNew;
        lock (m_playersLock)
        {
            isNew = !m_players.ContainsKey(from);
            m_players[from] = dto;
        }

        string pokemons = Messages.PokemonNames(dto);
        if (isNew) Log("[SERVER] " + dto.playerName + " joined");
        Log("[SERVER] Player data from " + dto.playerName + ": level " + dto.level +
            ", Pokemon: " + (pokemons.Length > 0 ? pokemons : "none"));
        BroadcastPlayers();
    }

    void OnChat(string message, Socket from)
    {
        PlayerDTO2 player;
        bool isPlayer;
        lock (m_playersLock) isPlayer = m_players.TryGetValue(from, out player);

        if (!isPlayer || message.Trim().Length == 0) return;

        Log("[SERVER] " + player.playerName + ": " + message);
        Broadcast("CHAT:" + player.playerName + ": " + message);
    }

    // LEAVE: and a closed socket both end here, the second call finds nothing
    void RemovePlayer(Socket socket)
    {
        PlayerDTO2 player;
        bool wasPlayer;
        lock (m_playersLock)
        {
            wasPlayer = m_players.TryGetValue(socket, out player);
            if (wasPlayer) m_players.Remove(socket);
        }
        if (!wasPlayer) return;

        Log("[SERVER] " + player.playerName + " left");
        BroadcastPlayers();
        Broadcast("CHAT:" + player.playerName + " left");
    }

    // In connection order, the dictionary has none
    List<PlayerDTO2> GetPlayers()
    {
        Socket[] clients;
        lock (m_clients) clients = m_clients.ToArray();

        List<PlayerDTO2> players = new List<PlayerDTO2>();
        lock (m_playersLock)
        {
            foreach (Socket c in clients)
            {
                PlayerDTO2 player;
                if (m_players.TryGetValue(c, out player)) players.Add(player);
            }
        }
        return players;
    }

    public string[] GetPlayerNames()
    {
        List<string> names = new List<string>();
        foreach (PlayerDTO2 p in GetPlayers()) names.Add(p.playerName);
        return names.ToArray();
    }

    // Everybody's data goes before the list, so the clients have it when the list arrives
    void BroadcastPlayers()
    {
        List<string> names = new List<string>();
        foreach (PlayerDTO2 p in GetPlayers())
        {
            BroadcastPacket(Messages.Player(p));
            names.Add(p.playerName);
        }
        Broadcast("PLAYERS:" + string.Join(",", names));
    }

    void Broadcast(string text)
    {
        BroadcastPacket(Messages.Text(text));
    }

    // Copy inside the lock, send outside it
    void BroadcastPacket(byte[] packet)
    {
        Socket[] targets;
        lock (m_clients) targets = m_clients.ToArray();
        foreach (Socket c in targets) SendPacket(packet, c);
    }
}
