// =================================================================================================
//  Lab 2 - Sockets: UDP SERVER
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  Session 2.2: same lobby as the TCP server (player list and chat), over UDP.
//  Scene: S_UDP_Server. The client is in S_UDP_Client.
//
//  MESSAGES (plain text, TYPE:content)
//    JOIN:Anna          client -> server   register the player
//    PLAYERS:Anna,Marc  server -> all      full player list, sent on every join and every leave
//    CHAT:hello         client -> server   a chat line
//    CHAT:Anna: hello   server -> all      the same line with the name of who wrote it
//    LEAVE:             client -> server   the player leaves the room
//    PING:              client -> server   "I am still here", once per second
//
//  What changes from TCP:
//      - ONE socket. There is no Accept(), so a client is just an endpoint we have heard from.
//      - The address travels with every message: ReceiveFrom() tells you who wrote, SendTo() says
//        where the answer goes.
//      - No framing needed: one datagram is one message (compare with the TCP file).
//      - Nothing tells you that a client is gone. There is no "Receive returns 0" here, so a
//        client that stays silent for 'clientTimeout' seconds is removed.
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

public class SocketsUDPServer : MonoBehaviour
{
    public int port = 9050;
    public string serverName = "MyServer";
    public bool autoStart = true;
    public bool showDebugView = true;   // the OnGUI text below. A scene with its own UI turns it off
    public float clientTimeout = 5f;   // seconds of silence before a client is dropped

    // =============================================================================================
    //  NETWORK CODE - thread, receive loop and the Update() pump from session 2.1
    // =============================================================================================

    const int MaxPacketSize = 64 * 1024;

    // 'time' is taken on the network thread, when the datagram really arrived
    struct Packet { public byte[] data; public EndPoint from; public DateTime time; }

    class ClientInfo
    {
        public EndPoint endPoint;
        public string name;
        public DateTime lastSeen;
    }

    Socket m_socket;                                                       // the only socket
    readonly List<ClientInfo> m_clients = new List<ClientInfo>();          // in join order
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<Packet> m_inbox = new ConcurrentQueue<Packet>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;
    volatile bool m_listening;

    public bool IsRunning { get { return m_running; } }
    public bool IsListening { get { return m_running && m_listening; } }   // the socket is bound
    public int ClientCount { get { lock (m_clients) return m_clients.Count; } }

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
        m_listening = false;

        // Closing the socket unblocks the thread waiting in ReceiveFrom() (slide 14).
        // No Shutdown() here: UDP has no connection to shut down.
        if (m_socket != null) { try { m_socket.Close(); } catch { } m_socket = null; }

        lock (m_clients) m_clients.Clear();

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
            OnPacketReceived(packet.data, packet.from, packet.time);

        // After emptying the queue, so the pings that were waiting there already count.
        if (m_running) CheckTimeouts();
    }

    void ServerThread()
    {
        try { m_socket = StartServer(); }                                            // TODO 1
        catch (SocketException e)
        {
            Log("[SERVER] Could not start: " + e.SocketErrorCode +
                (e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? " (port " + port + " is still used by another instance, slide 34)" : ""));
            m_running = false;   // nothing was started, so whoever asked can see it and try again
            return;
        }

        if (m_socket == null) { WarnTodo(1, "StartServer() returned null"); return; }
        Log("[SERVER] Listening on " + port);
        m_listening = true;

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

            m_inbox.Enqueue(new Packet { data = payload, from = from, time = DateTime.UtcNow });
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
        if (!showDebugView) return;

        GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
        GUILayout.Label("UDP SERVER   port " + port + "   clients " + ClientCount);

        if (!m_running) { if (GUILayout.Button("Start server", GUILayout.Width(110))) StartNetwork(); }
        else if (GUILayout.Button("Stop", GUILayout.Width(110))) Disconnect();

        GUILayout.Label("Players: " + string.Join(", ", GetPlayerNames()));

        lock (m_log)
            for (int i = Mathf.Max(0, m_log.Count - 18); i < m_log.Count; i++)
                GUILayout.Label(m_log[i]);

        GUILayout.EndArea();
    }

    // =============================================================================================
    //  TODO AREA - this is the only part you have to write
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

    // =============================================================================================
    //  LOBBY - everything below runs in Update(), on the main thread
    // =============================================================================================

    void OnPacketReceived(byte[] data, EndPoint from, DateTime time)
    {
        string text = Encoding.UTF8.GetString(data);

        // Only the first ':' separates type and content, a chat line can have more of them.
        int separator = text.IndexOf(':');
        string type = separator < 0 ? text : text.Substring(0, separator);
        string content = separator < 0 ? "" : text.Substring(separator + 1);

        // Any message proves that the client is alive, not only PING.
        ClientInfo client;
        lock (m_clients)
        {
            client = FindClient(from);
            if (client != null) client.lastSeen = time;
        }

        switch (type)
        {
            case "JOIN": OnJoin(content, from, client, time); break;
            case "CHAT": if (client != null) OnChat(content, client); break;
            case "LEAVE": if (client != null) RemoveClient(client, "left"); break;
            case "PING": break;   // lastSeen is already updated above
            default: Log("[SERVER] Unknown message: " + text + " from " + from); break;
        }
    }

    // There is no Accept() in UDP: the first JOIN from an endpoint is what creates the client.
    // Only JOIN does it. Adding on any message would bring back a player that already timed out.
    void OnJoin(string name, EndPoint from, ClientInfo client, DateTime time)
    {
        name = name.Trim().Replace(",", "");   // a comma would break the PLAYERS list
        if (name.Length == 0) name = "Player";

        lock (m_clients)
        {
            if (client == null)
            {
                client = new ClientInfo { endPoint = from, lastSeen = time };
                m_clients.Add(client);
            }
            client.name = name;   // a repeated JOIN only renames, it never adds a second entry
        }

        Log("[SERVER] " + name + " joined from " + from);
        BroadcastPlayers();
    }

    void OnChat(string message, ClientInfo client)
    {
        if (message.Trim().Length == 0) return;

        Log("[SERVER] " + client.name + ": " + message);
        Broadcast("CHAT:" + client.name + ": " + message);
    }

    void RemoveClient(ClientInfo client, string reason)
    {
        lock (m_clients) m_clients.Remove(client);

        Log("[SERVER] " + client.name + " " + reason);
        BroadcastPlayers();

        // Also as a chat line with no sender, so the players that stay can read who left.
        Broadcast("CHAT:" + client.name + " " + reason);
    }

    // A closed window or a crash sends no LEAVE:
    void CheckTimeouts()
    {
        DateTime now = DateTime.UtcNow;
        List<string> timedOut = null;

        lock (m_clients)
        {
            // backwards because we remove while going through the list
            for (int i = m_clients.Count - 1; i >= 0; i--)
            {
                if ((now - m_clients[i].lastSeen).TotalSeconds < clientTimeout) continue;

                if (timedOut == null) timedOut = new List<string>();
                timedOut.Add(m_clients[i].name);
                m_clients.RemoveAt(i);
            }
        }

        if (timedOut == null) return;

        BroadcastPlayers();
        foreach (string name in timedOut)
        {
            Log("[SERVER] " + name + " timed out");
            Broadcast("CHAT:" + name + " timed out");
        }
    }

    // Call it with m_clients locked.
    ClientInfo FindClient(EndPoint endPoint)
    {
        foreach (ClientInfo c in m_clients)
            if (c.endPoint.Equals(endPoint)) return c;
        return null;
    }

    public string[] GetPlayerNames()
    {
        lock (m_clients)
        {
            string[] names = new string[m_clients.Count];
            for (int i = 0; i < names.Length; i++) names[i] = m_clients[i].name;
            return names;
        }
    }

    void BroadcastPlayers()
    {
        Broadcast("PLAYERS:" + string.Join(",", GetPlayerNames()));
    }

    // Copy the endpoints inside the lock and send outside it, same idea as in the TCP server.
    void Broadcast(string text)
    {
        EndPoint[] targets;
        lock (m_clients)
        {
            targets = new EndPoint[m_clients.Count];
            for (int i = 0; i < targets.Length; i++) targets[i] = m_clients[i].endPoint;
        }
        foreach (EndPoint target in targets) SendString(text, target);
    }
}
