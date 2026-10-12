// =================================================================================================
//  Lab 2 - Sockets: UDP CLIENT
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  Session 2.2: same lobby client as the TCP one (player list and chat), over UDP.
//  Scene: S_UDP_Client. The server is in S_UDP_Server. Start the server first.
//
//  MESSAGES (plain text, TYPE:content)
//    JOIN:Anna          our first message, it is the handshake
//    PLAYERS:Anna,Marc  the server sends it on every join and every leave -> CurrentPlayers
//    CHAT:hello         we send it from SendChatMessage()
//    CHAT:Anna: hello   the server sends it to everybody -> ChatHistory
//    LEAVE:             we send it in Disconnect()
//    PING:              we send it every second, or the server drops us after 5 s of silence
//
//  What changes from TCP (slides 7, 9 and 24):
//      - There is no Connect(). Your first message IS the handshake.
//      - You do not bind either: the first SendTo() makes the system pick a local port for you.
//      - You must write the server address on every message you send.
//      - The server cannot see that we closed, so we have to keep telling it we are alive.
//
// =================================================================================================

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class SocketsUDPClient : MonoBehaviour
{
    public string serverIp = "127.0.0.1";
    public int port = 9050;
    public string userName = "Player";
    public bool autoStart = true;
    public bool showDebugView = true;   // the OnGUI text below. A scene with its own UI turns it off

    // =============================================================================================
    //  LOBBY STATE - only touched from the main thread
    // =============================================================================================

    const int MaxChatLines = 100;
    const float PingInterval = 1f;   // seconds. The server timeout is 5, so a few can get lost

    public List<string> CurrentPlayers { get; } = new List<string>();
    public List<string> ChatHistory { get; } = new List<string>();

    public event Action<List<string>> OnPlayersUpdated;
    public event Action<string> OnChatMessageReceived;

    // =============================================================================================
    //  NETWORK CODE - thread, receive loop and the Update() pump from session 2.1
    // =============================================================================================

    const int MaxPacketSize = 64 * 1024;

    Socket m_socket;                                          // the only socket
    EndPoint m_serverEndPoint;                                // where your messages go
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<byte[]> m_inbox = new ConcurrentQueue<byte[]>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;
    volatile bool m_sessionOver;                              // the network thread ended by itself
    bool m_joined;                                            // true once the server answered with PLAYERS
    float m_pingTimer;
    string m_chatInput = "";                                  // text field of the debug view

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

        StartThread(ClientThread);
    }

    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        // There is no connection to close, so this is the only way the server knows right away.
        // If the datagram gets lost its timeout removes us a few seconds later.
        if (!m_sessionOver) SendString("LEAVE:");

        if (m_socket != null) { try { m_socket.Close(); } catch { } m_socket = null; }

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);

        m_joined = false;
        CurrentPlayers.Clear();
        Log("[CLIENT] Stopped");
    }

    void OnDestroy() { Disconnect(); }

    void Update()
    {
        byte[] data;
        while (m_inbox.TryDequeue(out data))
            OnPacketReceived(data);

        // The receive loop ended alone, normally because nobody is listening on the server port.
        // It was noticed on the network thread, but the player list and the events belong here.
        if (m_sessionOver)
        {
            Disconnect();
            m_sessionOver = false;
            if (OnPlayersUpdated != null) OnPlayersUpdated(CurrentPlayers);
        }

        if (m_running) Heartbeat();
    }

    // The server only knows we are still here if it keeps hearing from us.
    void Heartbeat()
    {
        m_pingTimer += Time.unscaledDeltaTime;   // unscaled, or pausing with timeScale 0 would kick us
        if (m_pingTimer < PingInterval) return;
        m_pingTimer = 0f;

        // The JOIN datagram can get lost too. Until the first PLAYERS arrives we repeat it instead
        // of the ping, the server does not add us twice.
        SendString(m_joined ? "PING:" : "JOIN:" + userName);
    }

    void ClientThread()
    {
        m_socket = StartClient();                                                    // TODO 1
        if (m_socket == null) { WarnTodo(1, "StartClient() returned null"); return; }

        try { m_serverEndPoint = ServerEndPoint(); }                                 // TODO 2
        catch (FormatException)
        {
            Log("[CLIENT] '" + serverIp + "' is not a valid IP address");
            if (m_running) m_sessionOver = true;
            return;
        }
        if (m_serverEndPoint == null) { WarnTodo(2, "ServerEndPoint() returned null"); return; }

        Log("[CLIENT] Ready, server is " + m_serverEndPoint);

        Socket socket = m_socket;
        OnStarted();                                                                 // TODO 3
        ReceiveLoop();

        // Still running and still on this socket: the loop did not end because of Disconnect().
        if (m_running && socket == m_socket) m_sessionOver = true;
    }

    public void SendPacket(byte[] payload)
    {
        if (m_socket == null || m_serverEndPoint == null || payload == null) return;

        try
        {
            int sent = SendRaw(m_socket, payload, m_serverEndPoint);                 // TODO 4
            if (sent < 0) WarnTodo(4, "SendRaw() is not implemented yet");
        }
        catch (SocketException e) { Log("[CLIENT] Send failed: " + e.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    public void SendString(string text)
    {
        SendPacket(Encoding.UTF8.GetBytes(text));
    }

    // The server adds our name and sends the line back to everybody, us included, so nothing is
    // added to ChatHistory here.
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
            // Blank endpoint: ReceiveFrom fills it with the sender. Here it is always the server,
            // so you do not really need it, but the API asks for it anyway (slide 10).
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);

            int received;
            try { received = ReceiveRaw(m_socket, buffer, ref from); }                // TODO 5
            catch (SocketException e)
            {
                // Windows reports "nobody is listening there" as an error on receive, which is the
                // closest thing UDP has to "the server is down".
                if (e.SocketErrorCode == SocketError.ConnectionReset)
                {
                    Log("[CLIENT] No server on " + m_serverEndPoint + " (slide 34)");
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

    // Runs in Update(), so the lists and the events are safe here.
    void OnPacketReceived(byte[] data)
    {
        string text = Encoding.UTF8.GetString(data);

        if (text.StartsWith("PLAYERS:", StringComparison.Ordinal))
        {
            string names = text.Substring("PLAYERS:".Length);

            // The server always sends the whole list, so we replace ours instead of merging.
            CurrentPlayers.Clear();
            if (names.Length > 0) CurrentPlayers.AddRange(names.Split(','));   // "" would give one empty name

            m_joined = true;   // the list is only sent to players, so the server has us

            Log("[CLIENT] Players: " + names);
            if (OnPlayersUpdated != null) OnPlayersUpdated(CurrentPlayers);
        }
        else if (text.StartsWith("CHAT:", StringComparison.Ordinal))
        {
            string line = text.Substring("CHAT:".Length);   // already "name: text", the server builds it

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

        // Read once: the buttons change m_running, and the layout must not change in the middle
        // of one OnGUI call.
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

    // =============================================================================================
    //  TODO AREA - this is the only part you have to write
    // =============================================================================================

    // --------------------------------------------------------------------------------- TODO 1 ---
    //  Goal: create the client socket and return it.
    //  Shorter than you expect: no Connect(), and no Bind() either, because the first SendTo()
    //  makes the system choose a local port for you (slide 6).
    //  Careful with the socket type and the protocol: datagrams, not streams.
    Socket StartClient()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        return socket;   // replace with the socket you created
    }

    // --------------------------------------------------------------------------------- TODO 2 ---
    //  Goal: build and return the address of the server, using 'serverIp' and 'port'.
    //  This is the envelope you will write on every message you send (slide 10).
    //  Look it up: IPEndPoint and IPAddress in the docs. IPAddress.Parse turns "127.0.0.1" into an
    //  IPAddress. The return type is EndPoint, and IPEndPoint is an EndPoint.
    //  Console when it works:  [CLIENT] Ready, server is 127.0.0.1:9050
    EndPoint ServerEndPoint()
    {
        return new IPEndPoint(IPAddress.Parse(serverIp), port);
    }

    // --------------------------------------------------------------------------------- TODO 3 ---
    //  Introduce yourself with JOIN: and our name.
    //  In TCP the connection was made by Connect(). Here there is no connection: this first
    //  message is the only thing that tells the server that you exist (slide 9).
    //  Console when it works: the server prints  [SERVER] Player joined from 127.0.0.1:6xxxx
    void OnStarted()
    {
        SendString("JOIN:" + userName);
    }

    // --------------------------------------------------------------------------------- TODO 4 ---
    //  Goal: send 'data' through 'socket' to the address 'to', and return how many bytes went out.
    //  Look it up: Socket.SendTo in the docs.
    int SendRaw(Socket socket, byte[] data, EndPoint to)
    {
        return socket.SendTo(data,to);   // replace with the number of bytes sent
    }

    // --------------------------------------------------------------------------------- TODO 5 ---
    //  Goal: receive one datagram into 'buffer' and return its size.
    //  Look it up: Socket.ReceiveFrom in the docs. 'from' is passed with 'ref' because the method
    //  fills it with the address of the sender.
    //  Console when it works:  [CLIENT] Received: MyServer
    int ReceiveRaw(Socket socket, byte[] buffer, ref EndPoint from)
    {
        return socket.ReceiveFrom(buffer,ref from);   // replace with the number of bytes received
    }
}
