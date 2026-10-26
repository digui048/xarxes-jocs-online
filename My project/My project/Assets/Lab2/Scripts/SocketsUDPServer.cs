using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class SocketsUDPServer : MonoBehaviour
{
    public int port = 9050;
    public string serverName = "MyServer";
    public bool autoStart = true;
    public bool showDebugView = true;
    public float clientTimeout = 5f;   // seconds of silence before a client is dropped

    const int MaxPacketSize = 64 * 1024;

    // time: when the datagram arrived, taken on the network thread
    struct Packet { public byte[] data; public EndPoint from; public DateTime time; }

    class ClientInfo
    {
        public EndPoint endPoint;
        public string name;
        public DateTime lastSeen;
    }

    Socket m_socket;
    readonly List<ClientInfo> m_clients = new List<ClientInfo>();
    readonly List<Thread> m_threads = new List<Thread>();
    readonly ConcurrentQueue<Packet> m_inbox = new ConcurrentQueue<Packet>();
    readonly List<string> m_log = new List<string>();
    readonly HashSet<int> m_warned = new HashSet<int>();
    volatile bool m_running;
    volatile bool m_listening;

    public bool IsRunning { get { return m_running; } }
    public bool IsListening { get { return m_running && m_listening; } }
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

        // Closing the socket unblocks ReceiveFrom(). No Shutdown(): UDP has no connection
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

        // After the queue, so the pings waiting in it already count
        if (m_running) CheckTimeouts();
    }

    void ServerThread()
    {
        try { m_socket = StartServer(); }
        catch (SocketException e)
        {
            Log("[SERVER] Could not start: " + e.SocketErrorCode +
                (e.SocketErrorCode == SocketError.AddressAlreadyInUse
                    ? " (port " + port + " is still used by another instance)" : ""));
            m_running = false;
            return;
        }

        if (m_socket == null) { WarnTodo(1, "StartServer() returned null"); return; }
        Log("[SERVER] Listening on " + port);
        m_listening = true;

        ReceiveLoop();
    }

    public void SendPacket(byte[] payload, EndPoint to)
    {
        if (m_socket == null || to == null || payload == null) return;

        try
        {
            int sent = SendRaw(m_socket, payload, to);
            if (sent < 0) WarnTodo(2, "SendRaw() is not implemented yet");
        }
        catch (SocketException e) { Log("[SERVER] Send failed: " + e.SocketErrorCode); }
        catch (ObjectDisposedException) { }
    }

    public void SendString(string text, EndPoint to)
    {
        SendPacket(Messages.Text(text), to);
    }

    void ReceiveLoop()
    {
        byte[] buffer = new byte[MaxPacketSize];

        while (m_running)
        {
            // ReceiveFrom() fills it with the sender's address
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);

            int received;
            try { received = ReceiveRaw(m_socket, buffer, ref from); }
            catch (SocketException e)
            {
                // Windows reports an earlier SendTo to a closed port here, ignore it
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

    Socket StartServer()
    {
        Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, port));   // no Listen: UDP has no connections
        return socket;
    }

    int SendRaw(Socket socket, byte[] data, EndPoint to)
    {
        return socket.SendTo(data, to);
    }

    int ReceiveRaw(Socket socket, byte[] buffer, ref EndPoint from)
    {
        return socket.ReceiveFrom(buffer, ref from);
    }

    void OnPacketReceived(byte[] data, EndPoint from, DateTime time)
    {
        // Any message proves the client is alive, not only PING
        ClientInfo client;
        lock (m_clients)
        {
            client = FindClient(from);
            if (client != null) client.lastSeen = time;
        }

        MsgType msgType;
        BinaryReader reader;
        if (!Messages.Open(data, out msgType, out reader))
        {
            Log("[SERVER] Dropped a packet of an unknown type from " + from + " (" + data.Length + " bytes)");
            return;
        }

        switch (msgType)
        {
            case MsgType.Text:
                string text = Messages.ReadText(reader);
                if (text != null) OnTextReceived(text, from, client, time);
                else Log("[SERVER] Dropped a broken Text message from " + from);
                break;
            case MsgType.Player:
                PlayerDTO2 dto;
                if (Messages.ReadPlayer(reader, out dto)) OnPlayerData(dto);
                else Log("[SERVER] Dropped a broken Player message from " + from);
                break;
            default:
                Log("[SERVER] Unexpected " + msgType + " message, dropped");
                break;
        }
    }

    void OnTextReceived(string text, EndPoint from, ClientInfo client, DateTime time)
    {
        // A chat line can contain ':' too, so split on the first one
        int separator = text.IndexOf(':');
        string type = separator < 0 ? text : text.Substring(0, separator);
        string content = separator < 0 ? "" : text.Substring(separator + 1);

        switch (type)
        {
            case "JOIN": OnJoin(content, from, client, time); break;
            case "CHAT": if (client != null) OnChat(content, client); break;
            case "LEAVE": if (client != null) RemoveClient(client, "left"); break;
            case "PING": break;   // lastSeen already updated
            default: Log("[SERVER] Unknown message: " + text + " from " + from); break;
        }
    }

    // Only JOIN creates a client, so a timed out player is not brought back by its pings
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
            client.name = name;   // a repeated JOIN only renames
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

    void OnPlayerData(PlayerDTO2 dto)
    {
        List<string> pokemons = new List<string>();
        foreach (Pokemon p in dto.ownedPokemons) pokemons.Add(p.name);

        Log("[SERVER] Player data from " + dto.playerName + ": level " + dto.level +
            ", Pokemon: " + (pokemons.Count > 0 ? string.Join(", ", pokemons) : "none"));
    }

    void RemoveClient(ClientInfo client, string reason)
    {
        lock (m_clients) m_clients.Remove(client);

        Log("[SERVER] " + client.name + " " + reason);
        BroadcastPlayers();
        Broadcast("CHAT:" + client.name + " " + reason);
    }

    // A closed window or a crash sends no LEAVE:
    void CheckTimeouts()
    {
        DateTime now = DateTime.UtcNow;
        List<string> timedOut = null;

        lock (m_clients)
        {
            // backwards because we remove while looping
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

    // Call it with m_clients locked
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

    // Copy inside the lock, send outside it
    void Broadcast(string text)
    {
        EndPoint[] targets;
        lock (m_clients)
        {
            targets = new EndPoint[m_clients.Count];
            for (int i = 0; i < targets.Length; i++) targets[i] = m_clients[i].endPoint;
        }
        byte[] packet = Messages.Text(text);
        foreach (EndPoint target in targets) SendPacket(packet, target);
    }
}
