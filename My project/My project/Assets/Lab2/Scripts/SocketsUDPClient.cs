using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class SocketsUDPClient : MonoBehaviour
{
    public string serverIp = "127.0.0.1";
    public int port = 9050;
    public string userName = "Player";
    public bool autoStart = true;
    public bool showDebugView = true;
    public int level = 1;
    public List<Pokemon> pokemons = new List<Pokemon> { new Pokemon { name = "Pikachu" }, new Pokemon { name = "Charmander" } };

    const int MaxChatLines = 100;
    const float PingInterval = 1f;   // seconds

    public List<string> CurrentPlayers { get; } = new List<string>();
    public List<string> ChatHistory { get; } = new List<string>();
    public Dictionary<string, PlayerDTO2> PlayerData { get; } = new Dictionary<string, PlayerDTO2>();
    public GameStartDTO CurrentGame { get; private set; }

    public event Action<List<string>> OnPlayersUpdated;
    public event Action<string> OnChatMessageReceived;
    public event Action<GameStartDTO> OnGameStarted;
    public event Action<TransformDTO> OnTransformReceived;

    const int MaxPacketSize = 64 * 1024;

    Socket m_socket;
    EndPoint m_serverEndPoint;
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<byte[]> m_inbox = new ConcurrentQueue<byte[]>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;
    volatile bool m_sessionOver;
    bool m_joined;
    float m_pingTimer;
    string m_chatInput = "";

    public bool IsRunning { get { return m_running; } }

    void Start()
    {
        Application.runInBackground = true;
        ParseCommandLine();
        if (autoStart) StartNetwork();
    }

    public void StartNetwork()
    {
        if (m_running) return;
        m_running = true;
        m_sessionOver = false;
        m_joined = false;
        m_pingTimer = 0f;

        CurrentPlayers.Clear();
        ChatHistory.Clear();
        PlayerData.Clear();
        CurrentGame = new GameStartDTO();

        StartThread(ClientThread);
    }

    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        // No connection to close: without this the server only notices through its timeout
        if (!m_sessionOver) SendString("LEAVE:");

        if (m_socket != null) { try { m_socket.Close(); } catch { } m_socket = null; }

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);

        m_joined = false;
        CurrentPlayers.Clear();
        PlayerData.Clear();
        Log("[CLIENT] Stopped");
    }

    void OnDestroy() { Disconnect(); }

    void Update()
    {
        byte[] data;
        while (m_inbox.TryDequeue(out data))
            OnPacketReceived(data);

        // The receive loop ended by itself, usually because no server is listening
        if (m_sessionOver)
        {
            Disconnect();
            m_sessionOver = false;
            if (OnPlayersUpdated != null) OnPlayersUpdated(CurrentPlayers);
        }

        if (m_running) Heartbeat();
    }

    void Heartbeat()
    {
        m_pingTimer += Time.unscaledDeltaTime;   // unscaled so timeScale 0 does not stop the pings
        if (m_pingTimer < PingInterval) return;
        m_pingTimer = 0f;

        // The PlayerDTO2 can be lost too, so it is repeated until the first PLAYERS arrives
        if (m_joined) SendString("PING:");
        else SendJoin();
    }

    void ClientThread()
    {
        m_socket = StartClient();
        if (m_socket == null) { WarnTodo(1, "StartClient() returned null"); return; }

        try { m_serverEndPoint = ServerEndPoint(); }
        catch (FormatException)
        {
            Log("[CLIENT] '" + serverIp + "' is not a valid IP address");
            if (m_running) m_sessionOver = true;
            return;
        }
        if (m_serverEndPoint == null) { WarnTodo(2, "ServerEndPoint() returned null"); return; }

        Log("[CLIENT] Ready, server is " + m_serverEndPoint);

        Socket socket = m_socket;
        OnStarted();
        ReceiveLoop();

        // The loop ended by itself, not because of Disconnect()
        if (m_running && socket == m_socket) m_sessionOver = true;
    }

    public void SendPacket(byte[] payload)
    {
        if (m_socket == null || m_serverEndPoint == null || payload == null) return;

        try
        {
            int sent = SendRaw(m_socket, payload, m_serverEndPoint);
            if (sent < 0) WarnTodo(4, "SendRaw() is not implemented yet");
        }
        catch (SocketException e) { Log("[CLIENT] Send failed: " + e.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    public void SendString(string text)
    {
        SendPacket(Messages.Text(text));
    }

    // The server sends the line back to everybody, so it is not added to ChatHistory here
    public void SendTransform(TransformDTO state)
    {
        SendPacket(Messages.Transform(state));
    }

    public void SendChatMessage(string msg)
    {
        if (string.IsNullOrWhiteSpace(msg)) return;
        SendString("CHAT:" + msg);
    }

    void ReceiveLoop()
    {
        byte[] buffer = new byte[MaxPacketSize];

        while (m_running)
        {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);

            int received;
            try { received = ReceiveRaw(m_socket, buffer, ref from); }
            catch (SocketException e)
            {
                // Windows reports a closed server port here
                if (e.SocketErrorCode == SocketError.ConnectionReset)
                {
                    Log("[CLIENT] No server on " + m_serverEndPoint);
                    return;
                }
                if (m_running) Log("[CLIENT] Receive failed: " + e.SocketErrorCode);
                return;
            }
            catch (ObjectDisposedException) { return; }

            if (received < 0) { WarnTodo(5, "ReceiveRaw() is not implemented yet"); return; }
            if (received == 0) continue;

            byte[] payload = new byte[received];
            Array.Copy(buffer, payload, received);

            m_inbox.Enqueue(payload);
        }
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
            case MsgType.Player:
                PlayerDTO2 dto;
                if (Messages.ReadPlayer(reader, out dto)) PlayerData[dto.playerName] = dto;
                else Log("[CLIENT] Dropped a broken Player message");
                break;
            case MsgType.Transform:
                TransformDTO state;
                if (!Messages.ReadTransform(reader, out state)) Log("[CLIENT] Dropped a broken Transform message");
                else if (OnTransformReceived != null) OnTransformReceived(state);
                break;
            case MsgType.GameStart:
                GameStartDTO game;
                if (Messages.ReadGameStart(reader, out game)) OnGameStart(game);
                else Log("[CLIENT] Dropped a broken GameStart message");
                break;
            default:
                Log("[CLIENT] Unexpected " + msgType + " message, dropped");
                break;
        }
    }

    void OnGameStart(GameStartDTO game)
    {
        CurrentGame = game;
        Log("[CLIENT] Game started, my id is " + game.yourId);
        if (OnGameStarted != null) OnGameStarted(game);
    }

    void OnTextReceived(string text)
    {
        if (text.StartsWith("PLAYERS:", StringComparison.Ordinal))
        {
            string names = text.Substring("PLAYERS:".Length);

            CurrentPlayers.Clear();
            if (names.Length > 0) CurrentPlayers.AddRange(names.Split(','));   // "" would give one empty name

            // Forget the data of players that are not in the room any more
            List<string> gone = new List<string>();
            foreach (string name in PlayerData.Keys) if (!CurrentPlayers.Contains(name)) gone.Add(name);
            foreach (string name in gone) PlayerData.Remove(name);

            m_joined = true;   // only players receive the list

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
        GUILayout.Label("UDP CLIENT   port " + port + "   name " + userName);

        // Read once so the layout does not change in the middle of OnGUI
        bool running = m_running;

        GUILayout.BeginHorizontal();
        if (!running)
        {
            GUILayout.Label("Name:", GUILayout.Width(45));
            userName = GUILayout.TextField(userName, GUILayout.Width(100));
            GUILayout.Label("Server IP:", GUILayout.Width(70));
            serverIp = GUILayout.TextField(serverIp, GUILayout.Width(120));
            if (GUILayout.Button("Join", GUILayout.Width(110))) StartNetwork();
        }
        else if (GUILayout.Button("Leave", GUILayout.Width(110))) Disconnect();
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
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        return socket;
    }

    EndPoint ServerEndPoint()
    {
        return new IPEndPoint(IPAddress.Parse(serverIp), port);
    }

    void OnStarted()
    {
        SendJoin();
    }

    void SendJoin()
    {
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

    int SendRaw(Socket socket, byte[] data, EndPoint to)
    {
        return socket.SendTo(data,to);
    }

    int ReceiveRaw(Socket socket, byte[] buffer, ref EndPoint from)
    {
        return socket.ReceiveFrom(buffer,ref from);
    }
}
