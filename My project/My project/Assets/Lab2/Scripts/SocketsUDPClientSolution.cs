// =================================================================================================
//  Lab 2 - Sockets: UDP CLIENT
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  THE EXERCISE
//    Same as the TCP one: you send your user name, the server answers with its name.
//    What changes (slides 7, 9 and 24):
//
//      - There is no Connect(). Your first message IS the handshake.
//      - You do not bind either: the first SendTo() makes the system pick a local port for you.
//      - You must write the server address on every message you send.
//
//  Scene: S_UDP_Client. The server is in S_UDP_Server. Start the server first.
//  The 5 TODOs are at the bottom. Unfinished ones print a [TODO n] warning instead of crashing.
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

public class SocketsUDPClientSolution : MonoBehaviour
{
    public string serverIp = "127.0.0.1";
    public int port = 9050;
    public string userName = "Player";
    public bool autoStart = true;

    // =============================================================================================
    //  GIVEN CODE - nothing to modify until the TODO area
    // =============================================================================================

    const int MaxPacketSize = 64 * 1024;

    Socket m_socket;                                          // the only socket
    EndPoint m_serverEndPoint;                                // where your messages go
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<byte[]> m_inbox = new ConcurrentQueue<byte[]>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;

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
        StartThread(ClientThread);
    }

    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        if (m_socket != null) { try { m_socket.Close(); } catch { } m_socket = null; }

        Thread[] threads;
        lock (m_threads) { threads = m_threads.ToArray(); m_threads.Clear(); }
        foreach (Thread t in threads) if (t != Thread.CurrentThread) t.Join(500);

        Log("[CLIENT] Stopped");
    }

    void OnDestroy() { Disconnect(); }

    void Update()
    {
        byte[] data;
        while (m_inbox.TryDequeue(out data))
            OnPacketReceived(data);
    }

    void ClientThread()
    {
        m_socket = StartClient();                                                    // TODO 1
        if (m_socket == null) { WarnTodo(1, "StartClient() returned null"); return; }

        m_serverEndPoint = ServerEndPoint();                                         // TODO 2
        if (m_serverEndPoint == null) { WarnTodo(2, "ServerEndPoint() returned null"); return; }

        Log("[CLIENT] Ready, server is " + m_serverEndPoint);

        OnStarted();                                                                 // TODO 3
        ReceiveLoop();
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
        GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
        GUILayout.Label("UDP CLIENT   port " + port + "   name " + userName);

        GUILayout.BeginHorizontal();
        if (!m_running)
        {
            GUILayout.Label("Server IP:", GUILayout.Width(70));
            serverIp = GUILayout.TextField(serverIp, GUILayout.Width(120));
            if (GUILayout.Button("Send hello", GUILayout.Width(110))) StartNetwork();
        }
        else if (GUILayout.Button("Stop", GUILayout.Width(110))) Disconnect();
        GUILayout.EndHorizontal();

        lock (m_log)
            for (int i = Mathf.Max(0, m_log.Count - 18); i < m_log.Count; i++)
                GUILayout.Label(m_log[i]);

        GUILayout.EndArea();
    }

    // =============================================================================================
    //  SOLUTION - the TODOs of the lab session, filled in
    // =============================================================================================

    // --------------------------------------------------------------------------------- TODO 1 ---
    //  Goal: create the client socket and return it.
    //  Shorter than you expect: no Connect(), and no Bind() either, because the first SendTo()
    //  makes the system choose a local port for you (slide 6).
    //  Careful with the socket type and the protocol: datagrams, not streams.
    Socket StartClient()
    {
        return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
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
    //  Goal: introduce yourself by sending 'userName' to the server.
    //  In TCP the connection was made by Connect(). Here there is no connection: this first
    //  message is the only thing that tells the server that you exist (slide 9).
    //  Use the helper that is already written: SendString(text).
    //  Console when it works: the server prints  [SERVER] Received: Player
    void OnStarted()
    {
        SendString(userName);   // no Connect: this first message IS the handshake
    }

    // --------------------------------------------------------------------------------- TODO 4 ---
    //  Goal: send 'data' through 'socket' to the address 'to', and return how many bytes went out.
    //  Look it up: Socket.SendTo in the docs.
    int SendRaw(Socket socket, byte[] data, EndPoint to)
    {
        return socket.SendTo(data, to);
    }

    // --------------------------------------------------------------------------------- TODO 5 ---
    //  Goal: receive one datagram into 'buffer' and return its size.
    //  Look it up: Socket.ReceiveFrom in the docs. 'from' is passed with 'ref' because the method
    //  fills it with the address of the sender.
    //  Console when it works:  [CLIENT] Received: MyServer
    int ReceiveRaw(Socket socket, byte[] buffer, ref EndPoint from)
    {
        return socket.ReceiveFrom(buffer, ref from);
    }
}
