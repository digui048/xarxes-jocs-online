// =================================================================================================
//  Lab 2 - Sockets: TCP CLIENT
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  THE EXERCISE
//    The client sends its user name. The server receives it and answers with the server name.
//    Scene: S_TCP_Client. The server is in S_TCP_Server.
//
//  HOW TO RUN IT
//    Start the server first (build or Editor), then this one. Same PC: Server Ip 127.0.0.1.
//    With a classmate: their IP, given by "ipconfig" on their machine.
//
//  WHAT IS ALREADY WRITTEN FOR YOU
//    Threads, shutdown, the Update() pump, the 4-byte length framing (slide 12) and the seam
//    SendPacket() / OnPacketReceived(), which work with byte[].
//
//  SOLUTION VERSION - this is the finished exercise
//    The 4 TODOs at the bottom. Each one is 1 to 3 lines. Unfinished ones print a [TODO n] warning.
//    Stuck? Test against Lab2_Reference/Lab2_TCP_Server.exe, and compare with the disabled
//    GameObject "TCPClientSolution" in the scene.
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

public class SocketsTCPClientSolution : MonoBehaviour
{
    public string serverIp = "127.0.0.1";
    public int port = 9050;
    public string userName = "Player";
    public bool autoStart = true;

    // =============================================================================================
    //  GIVEN CODE - nothing to modify until the TODO area
    // =============================================================================================

    const int MaxPacketSize = 64 * 1024;

    Socket m_connection;                                      // the connection to the server
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<byte[]> m_inbox = new ConcurrentQueue<byte[]>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;

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
        StartThread(ClientThread);
    }

    // Closing the socket is what unblocks the thread waiting in Receive() (slide 14).
    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        CloseSocket(m_connection); m_connection = null;

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);

        Log("[CLIENT] Disconnected");
    }

    void OnDestroy() { Disconnect(); }

    // Main thread: the only safe place to touch the Unity API.
    void Update()
    {
        byte[] data;
        while (m_inbox.TryDequeue(out data))
            OnPacketReceived(data);
    }

    void ClientThread()
    {
        try { m_connection = StartClient(); }                                        // TODO 1
        catch (SocketException e)
        {
            Log("[CLIENT] Could not connect: " + e.SocketErrorCode +
                (e.SocketErrorCode == SocketError.ConnectionRefused
                    ? " (nobody is listening on " + serverIp + ":" + port + ", slide 34)" : ""));
            return;
        }

        if (m_connection == null) { WarnTodo(1, "StartClient() returned null"); return; }
        Log("[CLIENT] Connected to " + serverIp + ":" + port);

        OnConnected();                                                               // TODO 2
        ReceiveLoop(m_connection);
        Log("[CLIENT] Connection closed");
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
            catch (SocketException e) { Log("[CLIENT] Connection lost: " + e.SocketErrorCode); return false; }
            catch (ObjectDisposedException) { return false; }

            if (read < 0) { WarnTodo(4, "ReceiveRaw() is not implemented yet"); return false; }
            if (read == 0) return false;   // 0 bytes = the server closed the connection (slide 8)
            total += read;
        }
        return true;
    }

    void OnPacketReceived(byte[] data)
    {
        Log("[CLIENT] Received: " + Encoding.UTF8.GetString(data));
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

    //// Temporary debug view. In the deliverable you replace it with your own scenes and UI.
    //void OnGUI()
    //{
    //    GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
    //    GUILayout.Label("TCP CLIENT   port " + port + "   name " + userName);

    //    GUILayout.BeginHorizontal();
    //    if (!m_running)
    //    {
    //        GUILayout.Label("Server IP:", GUILayout.Width(70));
    //        serverIp = GUILayout.TextField(serverIp, GUILayout.Width(120));
    //        if (GUILayout.Button("Connect", GUILayout.Width(110))) StartNetwork();
    //    }
    //    else if (GUILayout.Button("Disconnect", GUILayout.Width(110))) Disconnect();
    //    GUILayout.EndHorizontal();

    //    lock (m_log)
    //        for (int i = Mathf.Max(0, m_log.Count - 18); i < m_log.Count; i++)
    //            GUILayout.Label(m_log[i]);

    //    GUILayout.EndArea();
    //}

    // =============================================================================================
    //  SOLUTION - the TODOs of the lab session, filled in
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
        return socket;
    }

    // --------------------------------------------------------------------------------- TODO 2 ---
    //  Goal: introduce yourself. Send 'userName' to the server, right after connecting.
    //  Use the helper that is already written: SendString(text).
    //  Runs on the network thread, so do not touch the Unity API here.
    //  Console when it works: the server prints  [SERVER] Received: Player
    void OnConnected()
    {
        SendString(userName);
    }

    // --------------------------------------------------------------------------------- TODO 3 ---
    //  Goal: send 'data' through 'socket' and return how many bytes were sent.
    //  It is already framed, so send the whole array. Same call the server uses.
    //  Look it up: Socket.Send in the docs.
    int SendRaw(Socket socket, byte[] data)
    {
        return socket.Send(data);
    }

    // --------------------------------------------------------------------------------- TODO 4 ---
    //  Goal: read from 'socket' into 'buffer' at position 'offset', at most 'count' bytes, and
    //  return how many bytes you actually read.
    //  Look it up: Socket.Receive, the overload that takes an offset and a size.
    //  Console when it works:  [CLIENT] Received: MyServer
    int ReceiveRaw(Socket socket, byte[] buffer, int offset, int count)
    {
        return socket.Receive(buffer, offset, count, SocketFlags.None);
    }
}
