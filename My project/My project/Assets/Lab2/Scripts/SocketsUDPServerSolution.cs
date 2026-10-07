// =================================================================================================
//  Lab 2 - Sockets: UDP SERVER
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  THE EXERCISE
//    Same as the TCP one: the client sends its user name, the server answers with the server name.
//    What changes is the protocol, and that is what you are here to see (slides 7, 9 and 24):
//
//      - ONE socket. There is no Accept(), so the server never gets a second socket.
//      - The address travels with every message: ReceiveFrom() tells you who wrote, SendTo() says
//        where the answer goes.
//      - No framing needed: one datagram is one message (compare with the TCP file).
//      - Nothing tells you that a client is gone. There is no "Receive returns 0" here.
//
//  Scene: S_UDP_Server. The client is in S_UDP_Client.
//  The 4 TODOs are at the bottom. Unfinished ones print a [TODO n] warning instead of crashing.
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

public class SocketsUDPServerSolution : MonoBehaviour
{
    public int port = 9050;
    public string serverName = "MyServer";
    public bool autoStart = true;

    // =============================================================================================
    //  GIVEN CODE - nothing to modify until the TODO area
    // =============================================================================================

    const int MaxPacketSize = 64 * 1024;

    struct Packet { public byte[] data; public EndPoint from; }

    Socket m_socket;                                                       // the only socket
    readonly HashSet<EndPoint> m_knownClients = new HashSet<EndPoint>();   // your client list
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<Packet> m_inbox = new ConcurrentQueue<Packet>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;

    public bool IsRunning { get { return m_running; } }
    public int ClientCount { get { lock (m_knownClients) return m_knownClients.Count; } }

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
        StartThread(ServerThread);
    }

    public void Disconnect()
    {
        if (!m_running) return;
        m_running = false;

        // Closing the socket unblocks the thread waiting in ReceiveFrom() (slide 14).
        // No Shutdown() here: UDP has no connection to shut down.
        if (m_socket != null) { try { m_socket.Close(); } catch { } m_socket = null; }

        lock (m_knownClients) m_knownClients.Clear();

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
            OnPacketReceived(packet.data, packet.from);
    }

    void ServerThread()
    {
        try { m_socket = StartServer(); }                                            // TODO 1
        catch (SocketException e)
        {
            Log("[SERVER] Could not start: " + e.SocketErrorCode +
                (e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? " (port " + port + " is still used by another instance, slide 34)" : ""));
            return;
        }

        if (m_socket == null) { WarnTodo(1, "StartServer() returned null"); return; }
        Log("[SERVER] Listening on " + port);

        ReceiveLoop();
    }

    // Sends one datagram. Note the difference with TCP: you must say WHERE every single time.
    public void SendPacket(byte[] payload, EndPoint to)
    {
        if (m_socket == null || to == null || payload == null) return;

        try
        {
            int sent = SendRaw(m_socket, payload, to);                               // TODO 2
            if (sent < 0) WarnTodo(2, "SendRaw() is not implemented yet");
        }
        catch (SocketException e) { Log("[SERVER] Send failed: " + e.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    public void SendString(string text, EndPoint to)
    {
        SendPacket(Encoding.UTF8.GetBytes(text), to);
    }

    void ReceiveLoop()
    {
        byte[] buffer = new byte[MaxPacketSize];

        while (m_running)
        {
            // A blank endpoint: ReceiveFrom OVERWRITES it with the address of whoever sent the
            // datagram. That is how a UDP server learns where to reply (slides 9 and 10).
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);

            int received;
            try { received = ReceiveRaw(m_socket, buffer, ref from); }                // TODO 3
            catch (SocketException e)
            {
                // Windows reports "a previous SendTo reached a closed port" as an error on receive.
                // Not our problem: keep serving.
                if (e.SocketErrorCode == SocketError.ConnectionReset) continue;
                if (m_running) Log("[SERVER] Receive failed: " + e.SocketErrorCode);
                return;
            }
            catch (ObjectDisposedException) { return; }

            if (received < 0) { WarnTodo(3, "ReceiveRaw() is not implemented yet"); return; }
            if (received == 0) continue;

            byte[] payload = new byte[received];
            Array.Copy(buffer, payload, received);

            m_inbox.Enqueue(new Packet { data = payload, from = from });
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
            if (args[i] == "-port" && i + 1 < args.Length) int.TryParse(args[++i], out port);
            else if (args[i] == "-name" && i + 1 < args.Length) serverName = args[++i];
            else if (args[i] == "-autostart") autoStart = true;
            else if (args[i] == "-noautostart") autoStart = false;
        }
    }

    void OnGUI()
    {
        GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
        GUILayout.Label("UDP SERVER   port " + port + "   clients " + ClientCount);

        if (!m_running) { if (GUILayout.Button("Start server", GUILayout.Width(110))) StartNetwork(); }
        else if (GUILayout.Button("Stop", GUILayout.Width(110))) Disconnect();

        lock (m_log)
            for (int i = Mathf.Max(0, m_log.Count - 18); i < m_log.Count; i++)
                GUILayout.Label(m_log[i]);

        GUILayout.EndArea();
    }

    // =============================================================================================
    //  SOLUTION - the TODOs of the lab session, filled in
    // =============================================================================================

    // --------------------------------------------------------------------------------- TODO 1 ---
    //  Goal: leave the server socket ready to receive datagrams on 'port', and return it.
    //  Two steps this time. Compare with the TCP server and ask yourself which of its three steps
    //  does not exist here, and why (slides 7 and 9).
    //  Look it up: slide 9 (UDP sequence), slide 6 (who binds and why), Socket in the docs.
    //  Careful with the socket type and the protocol: UDP works with datagrams, not with streams.
    //  Console when it works:  [SERVER] Listening on 9050
    Socket StartServer()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));   // no Listen: UDP has no connections
        return socket;
    }

    // --------------------------------------------------------------------------------- TODO 2 ---
    //  Goal: send 'data' through 'socket' to the address 'to', and return how many bytes went out.
    //  Look it up: Socket.SendTo in the docs.
    //  Notice what makes UDP different: the destination is a parameter of every send, because the
    //  socket does not remember anybody (slide 10).
    int SendRaw(Socket socket, byte[] data, EndPoint to)
    {
        return socket.SendTo(data, to);   // the destination travels with every single message
    }

    // --------------------------------------------------------------------------------- TODO 3 ---
    //  Goal: receive one datagram into 'buffer' and return its size.
    //  Look it up: Socket.ReceiveFrom in the docs.
    //  'from' arrives blank and must come out filled with the sender's address: that is why it is
    //  passed with 'ref'. Pass it along as it is, do not create a new one here.
    int ReceiveRaw(Socket socket, byte[] buffer, ref EndPoint from)
    {
        return socket.ReceiveFrom(buffer, ref from);   // 'from' comes back filled with the sender
    }

    // --------------------------------------------------------------------------------- TODO 4 ---
    //  Goal: remember who wrote to you, and answer them.
    //    a) Add 'from' to m_knownClients. There is no Accept() in UDP, so this set of addresses IS
    //       your list of clients. In Lab 6 it becomes the ClientProxy list.
    //       HashSet.Add returns true the first time an address appears: that is how you detect a
    //       new player. Log it as "[SERVER] New client: " + from.
    //    b) Reply to 'from' with 'serverName', using SendString(text, endPoint).
    //  Console when it works:
    //      [SERVER] New client: 127.0.0.1:61xxx
    //      and the client prints  [CLIENT] Received: MyServer
    void OnPacketReceived(byte[] data, EndPoint from)
    {
        string text = Encoding.UTF8.GetString(data);
        Log("[SERVER] Received: " + text + " from " + from);

        bool isNew;
        lock (m_knownClients) isNew = m_knownClients.Add(from);   // no Accept: this IS the list
        if (isNew) Log("[SERVER] New client: " + from);

        SendString(serverName, from);
    }
}
