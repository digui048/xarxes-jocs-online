// =================================================================================================
//  Lab 2 - Sockets: LOBBY UI
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  Drives the MainMenu scene. It has two screens:
//
//    - The menu, with the Host and Join panels and the TCP / UDP selector.
//    - The waiting room, with the player list and the chat.
//
//  There is no networking here. This script only starts and stops the four Sockets components and
//  shows what the client that is in use receives. Their events are raised from Update(), so the
//  handlers below can touch the UI directly.
//
//  Host: starts the server and then a local client on 127.0.0.1, so the host is one more player.
//  Join: starts only the client.
//
// =================================================================================================

using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using TMPro;
using UnityEngine;

public class LobbyUI : MonoBehaviour
{
    const int DefaultPort = 9050;
    const float ServerStartTimeout = 2f;   // seconds waiting for the server socket to be bound
    const float ConnectTimeout = 5f;       // seconds waiting for the first player list

    [Header("Network")]
    public SocketsTCPServer tcpServer;
    public SocketsTCPClient tcpClient;
    public SocketsUDPServer udpServer;
    public SocketsUDPClient udpClient;

    [Header("Screens")]
    public GameObject menuPanel;
    public GameObject waitingRoomPanel;

    [Header("Menu")]
    public TMP_Dropdown protocolDropdown;   // option 0 is TCP, option 1 is UDP
    public TMP_InputField hostNameInput;
    public TMP_InputField hostPortInput;
    public TMP_Text localIpText;
    public TMP_InputField playerNameInput;
    public TMP_InputField serverIpInput;
    public TMP_InputField serverPortInput;
    public TMP_Text statusText;

    [Header("Waiting room")]
    public TMP_Text roomTitleText;
    public TMP_Text playerListText;
    public TMP_Text chatHistoryText;
    public TMP_InputField chatInput;

    enum State { Menu, StartingServer, Connecting, InRoom }

    State m_state = State.Menu;
    bool m_useUdp;
    bool m_hosting;
    float m_connectTimer;
    string m_roomTitle = "";
    bool m_focusChat;

    bool ClientRunning { get { return m_useUdp ? udpClient.IsRunning : tcpClient.IsRunning; } }
    bool ServerRunning { get { return m_useUdp ? udpServer.IsRunning : tcpServer.IsRunning; } }
    bool ServerListening { get { return m_useUdp ? udpServer.IsListening : tcpServer.IsListening; } }
    List<string> ChatHistory { get { return m_useUdp ? udpClient.ChatHistory : tcpClient.ChatHistory; } }

    void Awake()
    {
        // Only one client runs at a time, so both can share the same handlers.
        tcpClient.OnPlayersUpdated += OnPlayersUpdated;
        tcpClient.OnChatMessageReceived += OnChatMessageReceived;
        udpClient.OnPlayersUpdated += OnPlayersUpdated;
        udpClient.OnChatMessageReceived += OnChatMessageReceived;

        // Enter in the chat box does the same as the Send button.
        chatInput.onSubmit.AddListener(OnChatSubmitted);

        // Names and chat lines are written by other people. Without this, somebody could send
        // TextMeshPro tags like <size=300> and break the screen of everybody.
        playerListText.richText = false;
        chatHistoryText.richText = false;
    }

    void Start()
    {
        localIpText.text = GetLocalIp();
        ShowMenu("");
    }

    void OnDestroy()
    {
        if (tcpClient != null)
        {
            tcpClient.OnPlayersUpdated -= OnPlayersUpdated;
            tcpClient.OnChatMessageReceived -= OnChatMessageReceived;
        }
        if (udpClient != null)
        {
            udpClient.OnPlayersUpdated -= OnPlayersUpdated;
            udpClient.OnChatMessageReceived -= OnChatMessageReceived;
        }
    }

    void Update()
    {
        if (m_state == State.Connecting)
        {
            // TCP gives up by itself when nobody listens. UDP has no connection, so it would keep
            // sending JOIN forever: the timeout is for that case.
            m_connectTimer += Time.unscaledDeltaTime;

            if (!ClientRunning) BackToMenu("Could not connect to the server");
            else if (m_connectTimer > ConnectTimeout) BackToMenu("The server did not answer");
        }
        else if (m_state == State.InRoom && !ClientRunning)
        {
            // The client stopped and it was not the Leave button, so the server is gone.
            BackToMenu("The connection with the server was lost");
        }

        if (m_focusChat)
        {
            m_focusChat = false;
            chatInput.ActivateInputField();
        }
    }

    // =============================================================================================
    //  BUTTONS - linked from the scene
    // =============================================================================================

    public void HostGame()
    {
        if (m_state != State.Menu) return;

        int port;
        if (!ReadPort(hostPortInput, out port)) return;

        m_useUdp = protocolDropdown.value == 1;
        m_hosting = true;

        if (m_useUdp) { udpServer.port = port; udpServer.StartNetwork(); }
        else { tcpServer.port = port; tcpServer.StartNetwork(); }

        m_state = State.StartingServer;
        SetStatus("Starting the server on port " + port + "...");
        StartCoroutine(JoinOwnServer(ReadName(hostNameInput, "Host"), port));
    }

    public void JoinGame()
    {
        if (m_state != State.Menu) return;

        int port;
        if (!ReadPort(serverPortInput, out port)) return;

        string ip = serverIpInput.text.Trim();
        if (ip.Length == 0 || ip == "localhost") ip = "127.0.0.1";

        IPAddress parsed;
        if (!IPAddress.TryParse(ip, out parsed))
        {
            SetStatus("'" + ip + "' is not a valid IP address");
            return;
        }

        m_useUdp = protocolDropdown.value == 1;
        m_hosting = false;
        StartClient(ip, port, ReadName(playerNameInput, "Player"));
    }

    public void SendChat()
    {
        if (m_state != State.InRoom) return;

        string text = chatInput.text;
        chatInput.text = "";
        m_focusChat = true;   // the box loses the focus when a line is sent, give it back

        // Nothing is added to the chat here. The server sends the line back to everybody, us too.
        if (m_useUdp) udpClient.SendChatMessage(text);
        else tcpClient.SendChatMessage(text);
    }

    public void LeaveGame()
    {
        BackToMenu("");
    }

    // =============================================================================================
    //  CONNECTING
    // =============================================================================================

    // The server binds its socket on a network thread. The local client has to wait for it, or
    // its JOIN would arrive before anybody is listening.
    IEnumerator JoinOwnServer(string playerName, int port)
    {
        float waited = 0f;
        while (ServerRunning && !ServerListening && waited < ServerStartTimeout)
        {
            waited += Time.unscaledDeltaTime;
            yield return null;
        }

        if (!ServerListening)
        {
            BackToMenu("Could not start the server. Is port " + port + " already in use?");
            yield break;
        }

        StartClient("127.0.0.1", port, playerName);
    }

    void StartClient(string ip, int port, string playerName)
    {
        if (m_useUdp)
        {
            udpClient.serverIp = ip;
            udpClient.port = port;
            udpClient.userName = playerName;
            udpClient.StartNetwork();
        }
        else
        {
            tcpClient.serverIp = ip;
            tcpClient.port = port;
            tcpClient.userName = playerName;
            tcpClient.StartNetwork();
        }

        m_roomTitle = (m_useUdp ? "UDP" : "TCP") + " room   -   " +
            (m_hosting ? "hosting on " + GetLocalIp() : "connected to " + ip) + ":" + port;

        m_connectTimer = 0f;
        m_state = State.Connecting;
        SetStatus("Connecting to " + ip + ":" + port + "...");
    }

    void StopNetwork()
    {
        StopAllCoroutines();

        // Client first, so its LEAVE: still finds the server alive.
        tcpClient.Disconnect();
        udpClient.Disconnect();

        if (m_hosting)
        {
            tcpServer.Disconnect();
            udpServer.Disconnect();
        }
        m_hosting = false;
    }

    // =============================================================================================
    //  CLIENT EVENTS
    // =============================================================================================

    void OnPlayersUpdated(List<string> players)
    {
        // The first list is the answer to our JOIN: the server has us, so we are in the room.
        if (m_state == State.Connecting && players.Count > 0) ShowWaitingRoom();

        if (m_state == State.InRoom)
            playerListText.text = "Players (" + players.Count + ")\n\n" + string.Join("\n", players);
    }

    void OnChatMessageReceived(string line)
    {
        if (m_state != State.InRoom) return;

        // The client already keeps the last 100 lines, so we just show its list again.
        chatHistoryText.text = string.Join("\n", ChatHistory);
    }

    void OnChatSubmitted(string text)
    {
        SendChat();
    }

    // =============================================================================================
    //  SCREENS
    // =============================================================================================

    void ShowMenu(string status)
    {
        m_state = State.Menu;
        menuPanel.SetActive(true);
        waitingRoomPanel.SetActive(false);
        SetStatus(status);
    }

    void ShowWaitingRoom()
    {
        m_state = State.InRoom;
        menuPanel.SetActive(false);
        waitingRoomPanel.SetActive(true);

        roomTitleText.text = m_roomTitle;
        playerListText.text = "";
        chatHistoryText.text = "";
        chatInput.text = "";
        m_focusChat = true;
    }

    void BackToMenu(string status)
    {
        StopNetwork();
        ShowMenu(status);
    }

    void SetStatus(string text)
    {
        statusText.text = text;
    }

    // =============================================================================================
    //  HELPERS
    // =============================================================================================

    bool ReadPort(TMP_InputField input, out int port)
    {
        string text = input.text.Trim();
        if (text.Length == 0) { port = DefaultPort; return true; }

        if (int.TryParse(text, out port) && port > 0 && port <= 65535) return true;

        SetStatus("The port has to be a number between 1 and 65535");
        return false;
    }

    static string ReadName(TMP_InputField input, string fallback)
    {
        string name = input.text.Trim();
        return name.Length > 0 ? name : fallback;
    }

    // The address the other players have to type to reach this PC.
    // Connect() on a UDP socket sends nothing: it only makes the system choose the network
    // interface it would use, and that interface has the address we want to show.
    static string GetLocalIp()
    {
        try
        {
            using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp))
            {
                socket.Connect("8.8.8.8", 65530);
                return ((IPEndPoint)socket.LocalEndPoint).Address.ToString();
            }
        }
        catch (SocketException)
        {
            return "127.0.0.1";   // no network at all
        }
    }
}
