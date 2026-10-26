using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class SocketsTCPClient : MonoBehaviour
{
    public string serverIp = "127.0.0.1";
    public int port = 9050;
    public string userName = "Player";
    public bool autoStart = true;
    public bool showDebugView = true;
    public int level = 1;
    public List<Pokemon> pokemons = new List<Pokemon> { new Pokemon { name = "Pikachu" }, new Pokemon { name = "Charmander" } };

    const int MaxChatLines = 100;

    public List<string> CurrentPlayers { get; } = new List<string>();
    public List<string> ChatHistory { get; } = new List<string>();

    public event Action<List<string>> OnPlayersUpdated;
    public event Action<string> OnChatMessageReceived;

    const int MaxPacketSize = 64 * 1024;

    Socket m_connection;
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<byte[]> m_inbox = new ConcurrentQueue<byte[]>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;
    volatile bool m_sessionOver;
    volatile int m_session;
    string m_chatInput = "";

    public bool IsRunning { get { return m_running; } }

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
        m_sessionOver = false;

        CurrentPlayers.Clear();
        ChatHistory.Clear();

        // An older thread still stuck in Connect() can tell it is not the current one
        int session = ++m_session;
        StartThread(delegate { ClientThread(session); });
    }

    // Closing the socket unblocks the thread waiting in Receive()
    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        if (!m_sessionOver && m_connection != null && m_connection.Connected) SendString("LEAVE:");

        CloseSocket(m_connection); m_connection = null;

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);

        CurrentPlayers.Clear();
        Log("[CLIENT] Disconnected");
    }

    void OnDestroy() { Disconnect(); }

    void Update()
    {
        byte[] data;
        while (m_inbox.TryDequeue(out data))
            OnPacketReceived(data);

        // The network thread ended by itself: the server is gone or the connection failed
        if (m_sessionOver)
        {
            Disconnect();
            m_sessionOver = false;
            if (OnPlayersUpdated != null) OnPlayersUpdated(CurrentPlayers);
        }
    }

    void ClientThread(int session)
    {
        Socket socket;
        try { socket = StartClient(); }
        catch (SocketException e)
        {
            Log("[CLIENT] Could not connect: " + e.SocketErrorCode +
                (e.SocketErrorCode == SocketError.ConnectionRefused
                    ? " (nobody is listening on " + serverIp + ":" + port + ")" : ""));
            EndSession(session);
            return;
        }
        catch (FormatException)
        {
            Log("[CLIENT] '" + serverIp + "' is not a valid IP address");
            EndSession(session);
            return;
        }

        if (socket == null) { WarnTodo(1, "StartClient() returned null"); return; }

        // Disconnect() was called while connecting: drop the socket before it sends JOIN
        if (!m_running || session != m_session) { CloseSocket(socket); return; }

        m_connection = socket;
        Log("[CLIENT] Connected to " + serverIp + ":" + port);

        OnConnected();
        ReceiveLoop(socket);
        Log("[CLIENT] Connection closed");

        EndSession(session);
    }

    // Only raises the flag, Update() does the cleanup on the main thread
    void EndSession(int session)
    {
        if (m_running && session == m_session) m_sessionOver = true;
    }

    public void SendPacket(byte[] payload)
    {
        if (m_connection == null || payload == null) return;

        byte[] framed = new byte[4 + payload.Length];
        BitConverter.GetBytes(payload.Length).CopyTo(framed, 0);
        payload.CopyTo(framed, 4);

        try
        {
            int sent = SendRaw(m_connection, framed);
            if (sent < 0) WarnTodo(3, "SendRaw() is not implemented yet");
        }
        catch (SocketException e) { Log("[CLIENT] Send failed: " + e.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    public void SendString(string text)
    {
        SendPacket(Messages.Text(text));
    }

    // The server sends the line back to everybody, so it is not added to ChatHistory here
    public void SendChatMessage(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return;
        SendString("CHAT:" + msg);
    }

    void ReceiveLoop(Socket socket)
    {
        byte[] header = new byte[4];
        while (m_running)
        {
            if (!ReadExactly(socket, header, 4)) return;

            int size = BitConverter.ToInt32(header, 0);
            if (size <= 0 || size > MaxPacketSize) { Log("[CLIENT] Invalid packet size: " + size); return; }

            byte[] payload = new byte[size];
            if (!ReadExactly(socket, payload, size)) return;

            m_inbox.Enqueue(payload);
        }
    }

    bool ReadExactly(Socket socket, byte[] buffer, int count)
    {
        int total = 0;
        while (total < count)
        {
            int read;
            try { read = ReceiveRaw(socket, buffer, total, count - total); }
            catch (SocketException e)
            {
                // Our own Disconnect() also ends up here, that one is not an error
                if (m_running) Log("[CLIENT] Connection lost: " + e.SocketErrorCode);
                return false;
            }
            catch (ObjectDisposedException) { return false; }

            if (read < 0) { WarnTodo(4, "ReceiveRaw() is not implemented yet"); return false; }
            if (read == 0) return false;   // connection closed
            total += read;
        }
        return true;
    }

    void OnPacketReceived(byte[] data)
    {
        MsgType msgType;
        BinaryReader reader;
        if (!Messages.Open(data, out msgType, out reader))
        {
            Log("[CLIENT] Dropped a packet of an unknown type (" + data.Length + " bytes)");
            return;
        }

        switch (msgType)
        {
            case MsgType.Text:
                string text = Messages.ReadText(reader);
                if (text != null) OnTextReceived(text);
                else Log("[CLIENT] Dropped a broken Text message");
                break;
            default:
                Log("[CLIENT] Unexpected " + msgType + " message, dropped");
                break;
        }
    }

    void OnTextReceived(string text)
    {
        if (text.StartsWith("PLAYERS:", StringComparison.Ordinal))
        {
            string names = text.Substring("PLAYERS:".Length);

            CurrentPlayers.Clear();
            if (names.Length > 0) CurrentPlayers.AddRange(names.Split(','));   // "" would give one empty name

            Log("[CLIENT] Players: " + names);
            if (OnPlayersUpdated != null) OnPlayersUpdated(CurrentPlayers);
        }
        else if (text.StartsWith("CHAT:", StringComparison.Ordinal))
        {
            string line = text.Substring("CHAT:".Length);

            ChatHistory.Add(line);
            if (ChatHistory.Count > MaxChatLines) ChatHistory.RemoveAt(0);

            Log("[CLIENT] " + line);
            if (OnChatMessageReceived != null) OnChatMessageReceived(line);
        }
        else
        {
            Log("[CLIENT] Received: " + text);
        }
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
            if (args[i] == "-ip" && i + 1 < args.Length) serverIp = args[++i];
            else if (args[i] == "-port" && i + 1 < args.Length) int.TryParse(args[++i], out port);
            else if (args[i] == "-name" && i + 1 < args.Length) userName = args[++i];
            else if (args[i] == "-autostart") autoStart = true;
            else if (args[i] == "-noautostart") autoStart = false;
        }
    }

    void OnGUI()
    {
        if (!showDebugView) return;

        GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
        GUILayout.Label("TCP CLIENT   port " + port + "   name " + userName);

        // Read once so the layout does not change in the middle of OnGUI
        bool running = m_running;

        GUILayout.BeginHorizontal();
        if (!running)
        {
            GUILayout.Label("Name:", GUILayout.Width(45));
            userName = GUILayout.TextField(userName, GUILayout.Width(100));
            GUILayout.Label("Server IP:", GUILayout.Width(70));
            serverIp = GUILayout.TextField(serverIp, GUILayout.Width(120));
            if (GUILayout.Button("Connect", GUILayout.Width(110))) StartNetwork();
        }
        else if (GUILayout.Button("Disconnect", GUILayout.Width(110))) Disconnect();
        GUILayout.EndHorizontal();

        if (running)
        {
            GUILayout.Label("Players: " + string.Join(", ", CurrentPlayers));

            GUILayout.BeginHorizontal();
            m_chatInput = GUILayout.TextField(m_chatInput, GUILayout.Width(300));
            if (GUILayout.Button("Send", GUILayout.Width(60)))
            {
                SendChatMessage(m_chatInput);
                m_chatInput = "";
            }
            GUILayout.EndHorizontal();
        }

        lock (m_log)
            for (int i = Mathf.Max(0, m_log.Count - 18); i < m_log.Count; i++)
                GUILayout.Label(m_log[i]);

        GUILayout.EndArea();
    }

    Socket StartClient()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(new IPEndPoint(IPAddress.Parse(serverIp), port));
        return socket;
    }

    void OnConnected()
    {
        SendString("JOIN:" + userName);
        SendPacket(Messages.Player(MyData()));
    }

    PlayerDTO2 MyData()
    {
        PlayerDTO2 dto = new PlayerDTO2();
        dto.playerName = userName;
        dto.level = level;
        dto.ownedPokemons = new List<Pokemon>();
        foreach (Pokemon p in pokemons)
            if (p != null && !string.IsNullOrEmpty(p.name)) dto.ownedPokemons.Add(p);
        return dto;
    }

    int SendRaw(Socket socket, byte[] data)
    {
        return socket.Send(data);
    }

    int ReceiveRaw(Socket socket, byte[] buffer, int offset, int count)
    {
        return socket.Receive(buffer, offset, count, SocketFlags.None); ;
    }
}
