// =================================================================================================
//  Lab 2 - Sockets: TCP SERVER
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  THE EXERCISE
//    The client sends its user name. The server receives it and answers with the server name.
//    Scene: S_TCP_Server. The client is in S_TCP_Client.
//
//  HOW TO RUN IT
//    Build the project with S_TCP_Server, or press Play with this scene open.
//    Then run the client on the other side. Both on the same PC: IP 127.0.0.1.
//
//  WHAT IS ALREADY WRITTEN FOR YOU
//    - Threads, shutdown and the Update() pump. Only Unity's main thread may touch the Unity API,
//      so the network thread fills a queue and Update() empties it (slide 14).
//    - Message framing. TCP is a stream of bytes with no message boundaries (slide 12), so every
//      packet travels as [4-byte length][payload].
//    - The seam: SendPacket() and OnPacketReceived() work with byte[]. In Lab 3 you will send
//      serialized objects through these very same functions.
//
//  WHAT YOU HAVE TO DO
//    - The 5 TODOs at the bottom. Each one is 1 to 3 lines.
//    - It compiles and runs before you start: unfinished TODOs print a [TODO n] warning.
//    - Stuck? Test against Lab2_Reference/Lab2_TCP_Client.exe, and compare with the solution
//      object in the scene (disabled GameObject "TCPServerSolution").
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

public class SocketsTCPServer : MonoBehaviour
{
    public int port = 9050;
    public string serverName = "MyServer";
    public bool autoStart = true;

    // =============================================================================================
    //  GIVEN CODE - nothing to modify until the TODO area
    // =============================================================================================

    const int MaxPacketSize = 64 * 1024;

    struct Packet { public byte[] data; public Socket from; }

    Socket m_listener;                                        // only accepts connections
    readonly List<Socket> m_clients = new List<Socket>();     // one socket per connected client
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<Packet> m_inbox = new ConcurrentQueue<Packet>();
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
        StartThread(ServerThread);
    }

    // Closing the sockets is what unblocks the threads waiting in Accept() or Receive() (slide 14).
    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        Socket[] clients;
        lock (m_clients) { clients = m_clients.ToArray(); m_clients.Clear(); }
        foreach (Socket c in clients) CloseSocket(c);

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
            OnPacketReceived(packet.data, packet.from);
    }

    void ServerThread()
    {
        try { m_listener = StartServer(); }                                          // TODO 1
        catch (SocketException e)
        {
            Log("[SERVER] Could not start: " + e.SocketErrorCode +
                (e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? " (port " + port + " is still used by another instance, slide 34)" : ""));
            return;
        }

        if (m_listener == null) { WarnTodo(1, "StartServer() returned null"); return; }
        Log("[SERVER] Listening on " + port);

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

    public void SendString(string text, Socket to)
    {
        SendPacket(Encoding.UTF8.GetBytes(text), to);
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
        GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
        GUILayout.Label("TCP SERVER   port " + port + "   name " + serverName);

        if (!m_running) { if (GUILayout.Button("Start server", GUILayout.Width(110))) StartNetwork(); }
        else if (GUILayout.Button("Stop", GUILayout.Width(110))) Disconnect();

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

    // --------------------------------------------------------------------------------- TODO 5 ---
    //  Goal: answer with 'serverName' to whoever sent the message.
    //  'from' is the socket the packet arrived through, so you already know where to reply.
    //  Use the helper that is already written: SendString(text, socket).
    //  Console when it works: the client prints  [CLIENT] Received: MyServer
    void OnPacketReceived(byte[] data, Socket from)
    {
        string text = Encoding.UTF8.GetString(data);
        Log("[SERVER] Received: " + text);

        // your code here
        SendString(serverName, from);
    }
}
