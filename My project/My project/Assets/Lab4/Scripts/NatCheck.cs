// Lab 4 - NAT check. A tool, not an exercise: there are no TODOs.
// It asks two public STUN servers which address they see, using ONE UDP socket,
// and compares it with this PC's private address (slides 5, 18 to 20 and 23).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class NatCheck : MonoBehaviour
{
    public static readonly string[] Servers = { "stun.l.google.com:19302", "stun.cloudflare.com:3478" };

    readonly List<string> m_lines = new List<string>();
    bool m_running;

    void Start() { RunInBackground(); }

    // The STUN calls block for up to 2 s each, so they run on their own thread (Lab 1).
    void RunInBackground()
    {
        if (m_running) return;
        m_running = true;
        lock (m_lines) m_lines.Clear();
        new Thread(() =>
        {
            Run(line => { lock (m_lines) m_lines.Add(line); Debug.Log("[NatCheck] " + line); });
            m_running = false;
        }) { IsBackground = true }.Start();
    }

    void OnGUI()
    {
        GUI.skin.label.fontSize = 18;
        GUI.skin.button.fontSize = 18;
        GUILayout.BeginArea(new Rect(20, 20, Screen.width - 40, Screen.height - 40));
        GUILayout.Label("Lab 4 - NAT check");
        string[] lines;
        lock (m_lines) lines = m_lines.ToArray();
        foreach (string line in lines) GUILayout.Label(line);
        if (m_running) GUILayout.Label("Asking the STUN servers...");
        else if (GUILayout.Button("Check again", GUILayout.Width(200))) RunInBackground();
        GUILayout.EndArea();
    }

    // ---------------------------------------------------------------------- the check

    public static void Run(Action<string> log)
    {
        using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
        {
            // One socket for every query, as your game would use its own socket
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            socket.ReceiveTimeout = 2000;
            int localPort = ((IPEndPoint)socket.LocalEndPoint).Port;
            IPAddress localIp = LocalIp();
            log("Private (ipconfig):  " + (localIp != null ? localIp.ToString() : "?") + ":" + localPort);

            var seen = new List<IPEndPoint>();
            foreach (string server in Servers)
            {
                try
                {
                    IPEndPoint mapped = AskStun(socket, server);
                    seen.Add(mapped);
                    log(server + "  sees you as  " + mapped);
                }
                catch (Exception e)
                {
                    log(server + "  no answer (" + e.GetType().Name + ")");
                }
            }
            log("-> " + Verdict(localIp, localPort, seen));
        }
    }

    public static string Verdict(IPAddress localIp, int localPort, List<IPEndPoint> seen)
    {
        if (seen.Count == 0)
            return "No answer: outgoing UDP to STUN is blocked on this network, or there is no Internet. " +
                   "Compare ipconfig with a \"what is my IP\" web page instead.";
        if (localIp != null && seen.All(s => s.Address.Equals(localIp)))
            return "No NAT: this PC has a public address. Anyone on the Internet can send to it " +
                   "(if the firewall lets them).";
        string nat = "Behind NAT: your private address is translated to " + seen[0].Address + ".";
        if (seen.Count == 1)
            return nat + " Only one server answered, so the NAT type is unknown.";
        if (seen.All(s => s.Port == seen[0].Port))
            return nat + " Same public port from both servers: endpoint-independent (\"cone\") NAT. " +
                   "Hole punching would work." +
                   (seen[0].Port == localPort ? " It even kept your local port." : "");
        return nat + " A different public port for each server: symmetric NAT. " +
               "Hole punching would fail, a relay is needed.";
    }

    // Sends a 20-byte STUN Binding Request (RFC 5389) and reads XOR-MAPPED-ADDRESS from the answer.
    public static IPEndPoint AskStun(Socket socket, string server)
    {
        string[] parts = server.Split(':');
        IPAddress ip = Dns.GetHostAddresses(parts[0]).First(a => a.AddressFamily == AddressFamily.InterNetwork);

        byte[] req = new byte[20];                                    // big-endian: network byte order
        req[0] = 0x00; req[1] = 0x01;                                 // type: Binding Request
        req[2] = 0x00; req[3] = 0x00;                                 // length of the attributes: none
        req[4] = 0x21; req[5] = 0x12; req[6] = 0xA4; req[7] = 0x42;   // magic cookie
        byte[] id = new byte[12];
        new System.Random().NextBytes(id);                            // transaction id
        Array.Copy(id, 0, req, 8, 12);
        socket.SendTo(req, new IPEndPoint(ip, int.Parse(parts[1])));

        byte[] buf = new byte[512];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        int n = socket.ReceiveFrom(buf, ref from);

        // After the 20-byte header come the attributes: type (2), length (2), value (padded to 4)
        for (int i = 20; i + 4 <= n; )
        {
            int type = (buf[i] << 8) | buf[i + 1];
            int len = (buf[i + 2] << 8) | buf[i + 3];
            if (type == 0x0020)                                       // XOR-MAPPED-ADDRESS
            {
                int port = ((buf[i + 6] << 8) | buf[i + 7]) ^ 0x2112;
                byte[] addr = { (byte)(buf[i + 8] ^ 0x21), (byte)(buf[i + 9] ^ 0x12),
                                (byte)(buf[i + 10] ^ 0xA4), (byte)(buf[i + 11] ^ 0x42) };
                return new IPEndPoint(new IPAddress(addr), port);
            }
            i += 4 + ((len + 3) & ~3);
        }
        throw new Exception("no XOR-MAPPED-ADDRESS in the answer");
    }

    // The address of the network card that reaches the Internet: what ipconfig shows.
    // Connect on a UDP socket sends nothing, it only picks the route.
    static IPAddress LocalIp()
    {
        try
        {
            using (Socket s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                s.Connect("8.8.8.8", 80);
                return ((IPEndPoint)s.LocalEndPoint).Address;
            }
        }
        catch { return null; }
    }
}
