// =================================================================================================
//  Lab 2 - Sockets: TCP CLIENT
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  Session 2.2: the client joins the lobby of the server, keeps the player list and the chat.
//  Scene: S_TCP_Client. The server is in S_TCP_Server.
//
//  MESSAGES (plain text, TYPE:content)
//    JOIN:Anna          we send it right after connecting
//    PLAYERS:Anna,Marc  the server sends it on every join and every leave -> CurrentPlayers
//    CHAT:hello         we send it from SendChatMessage()
//    CHAT:Anna: hello   the server sends it to everybody -> ChatHistory
//    LEAVE:             we send it in Disconnect(), before closing the socket
//
//  HOW TO RUN IT
//    Start the server first (build or Editor), then this one. Same PC: Server Ip 127.0.0.1.
//    With a classmate: their IP, given by "ipconfig" on their machine.
//
//  FOR THE UI
//    CurrentPlayers and ChatHistory hold the state, OnPlayersUpdated and OnChatMessageReceived
//    tell when it changes. Both events are raised from Update(), so the handlers can touch the UI.
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

public class SocketsTCPClient : MonoBehaviour
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

    public List<string> CurrentPlayers { get; } = new List<string>();
    public List<string> ChatHistory { get; } = new List<string>();

    public event Action<List<string>> OnPlayersUpdated;
    public event Action<string> OnChatMessageReceived;

    // =============================================================================================
    //  NETWORK CODE - thread, framing and the Update() pump from session 2.1
    // =============================================================================================

    const int MaxPacketSize = 64 * 1024;

    Socket m_connection;                                      // the connection to the server
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<byte[]> m_inbox = new ConcurrentQueue<byte[]>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;
    volatile bool m_sessionOver;                              // the network thread ended by itself
    volatile int m_session;                                   // one number per StartNetwork()
    string m_chatInput = "";                                  // text field of the debug view

    public bool IsRunning { get { return m_running; } }

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
        m_sessionOver = false;

        CurrentPlayers.Clear();
        ChatHistory.Clear();

        // The thread remembers its number. A thread from an older attempt that is still stuck in
        // Connect() can then see that it is not the current one any more.
        int session = ++m_session;
        StartThread(delegate { ClientThread(session); });
    }

    // Closing the socket is what unblocks the thread waiting in Receive() (slide 14).
    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        // Say goodbye before closing. If the server already closed there is nobody to tell.
        if (!m_sessionOver && m_connection != null && m_connection.Connected) SendString("LEAVE:");

        CloseSocket(m_connection); m_connection = null;

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);

        CurrentPlayers.Clear();
        Log("[CLIENT] Disconnected");
    }

    void OnDestroy() { Disconnect(); }

    // Main thread: the only safe place to touch the Unity API.
    void Update()
    {
        byte[] data;
        while (m_inbox.TryDequeue(out data))
            OnPacketReceived(data);

        // The server closed the connection, or we never got one. It was noticed on the network
        // thread, but the player list and the events belong here.
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
        try { socket = StartClient(); }                                              // TODO 1
        catch (SocketException e)
        {
            Log("[CLIENT] Could not connect: " + e.SocketErrorCode +
                (e.SocketErrorCode == SocketError.ConnectionRefused
                    ? " (nobody is listening on " + serverIp + ":" + port + ", slide 34)" : ""));
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

        // Connect() can take a long time. If Disconnect() was called meanwhile nobody wants this
        // socket any more, and sending JOIN would leave a ghost player in the server.
        if (!m_running || session != m_session) { CloseSocket(socket); return; }

        m_connection = socket;
        Log("[CLIENT] Connected to " + serverIp + ":" + port);

        OnConnected();                                                               // TODO 2
        ReceiveLoop(socket);
        Log("[CLIENT] Connection closed");

        EndSession(session);
    }

    // The network thread calls it when it ends and nobody asked for it with Disconnect().
    // It only raises the flag: the cleanup is done by Update(), on the main thread.
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
            int sent = SendRaw(m_connection, framed);                                // TODO 3
            if (sent < 0) WarnTodo(3, "SendRaw() is not implemented yet");
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
            try { read = ReceiveRaw(socket, buffer, total, count - total); }         // TODO 4
            catch (SocketException e)
            {
                // Our own Disconnect() also lands here, because closing the socket is what
                // unblocks Receive(). That one is not an error.
                if (m_running) Log("[CLIENT] Connection lost: " + e.SocketErrorCode);
                return false;
            }
            catch (ObjectDisposedException) { return false; }

            if (read < 0) { WarnTodo(4, "ReceiveRaw() is not implemented yet"); return false; }
            if (read == 0) return false;   // 0 bytes = the server closed the connection (slide 8)
            total += read;
        }
        return true;
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

    // Temporary debug view. In the deliverable you replace it with your own scenes and UI.
    void OnGUI()
    {
        if (!showDebugView) return;

        GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
        GUILayout.Label("TCP CLIENT   port " + port + "   name " + userName);

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

    // =============================================================================================
    //  TODO AREA - this is the only part you have to write
    // =============================================================================================

    // --------------------------------------------------------------------------------- TODO 1 ---
    //  Goal: create the client socket, connect it to the server, and return it.
    //  You have 'serverIp' and 'port'. The client does not bind: the system gives it a free port
    //  (slide 6). It names the server only once, and the socket remembers it (slide 10).
    //  Look it up: slide 8, and Socket / Socket.Connect / IPEndPoint in the docs.
    //  If the server is not running you get ConnectionRefused. That is the expected error.
    //  Console when it works:  [CLIENT] Connected to 127.0.0.1:9050
    Socket StartClient()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Connect(new IPEndPoint(IPAddress.Parse(serverIp), port));   // named once, remembered
        return socket;   // replace with the socket you connected
    }

    // --------------------------------------------------------------------------------- TODO 2 ---
    //  Introduce yourself: the first message after connecting is JOIN: with our name.
    //  Runs on the network thread, so do not touch the Unity API here.
    //  Console when it works: the server prints  [SERVER] Player joined
    void OnConnected()
    {
        SendString("JOIN:" + userName);
    }

    // --------------------------------------------------------------------------------- TODO 3 ---
    //  Goal: send 'data' through 'socket' and return how many bytes were sent.
    //  It is already framed, so send the whole array. Same call the server uses.
    //  Look it up: Socket.Send in the docs.
    int SendRaw(Socket socket, byte[] data)
    {
        return socket.Send(data);   // replace with the number of bytes sent
    }

    // --------------------------------------------------------------------------------- TODO 4 ---
    //  Goal: read from 'socket' into 'buffer' at position 'offset', at most 'count' bytes, and
    //  return how many bytes you actually read.
    //  Look it up: Socket.Receive, the overload that takes an offset and a size.
    //  Console when it works:  [CLIENT] Received: MyServer
    int ReceiveRaw(Socket socket, byte[] buffer, int offset, int count)
    {
        return socket.Receive(buffer, offset, count, SocketFlags.None); ;   // replace with the number of bytes read
    }
}
