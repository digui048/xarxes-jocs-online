using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using TMPro;
using UnityEngine;

public class LobbyUI : MonoBehaviour
{
    const int DefaultPort = 9050;
    const float ServerStartTimeout = 2f;   // seconds
    const float ConnectTimeout = 5f;       // seconds

    [Header("Network")]
    public SocketsTCPServer tcpServer;
    public SocketsTCPClient tcpClient;
    public SocketsUDPServer udpServer;
    public SocketsUDPClient udpClient;

    [Header("Screens")]
    public GameObject menuPanel;
    public GameObject waitingRoomPanel;

    [Header("Menu")]
    public TMP_Dropdown protocolDropdown;   // 0 = TCP, 1 = UDP
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
        tcpClient.OnPlayersUpdated += OnPlayersUpdated;
        tcpClient.OnChatMessageReceived += OnChatMessageReceived;
        udpClient.OnPlayersUpdated += OnPlayersUpdated;
        udpClient.OnChatMessageReceived += OnChatMessageReceived;

        chatInput.onSubmit.AddListener(OnChatSubmitted);

        // Other players write these texts, so TMP tags must not be interpreted
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
            // Without this timeout UDP would keep sending JOIN forever
            m_connectTimer += Time.unscaledDeltaTime;

            if (!ClientRunning) BackToMenu("Could not connect to the server");
            else if (m_connectTimer > ConnectTimeout) BackToMenu("The server did not answer");
        }
        else if (m_state == State.InRoom && !ClientRunning)
        {
            // Stopped without the Leave button: the server is gone
            BackToMenu("The connection with the server was lost");
        }

        if (m_focusChat)
        {
            m_focusChat = false;
            chatInput.ActivateInputField();
        }
    }

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
        m_focusChat = true;   // the input field loses focus on submit

        if (m_useUdp) udpClient.SendChatMessage(text);
        else tcpClient.SendChatMessage(text);
    }

    public void LeaveGame()
    {
        BackToMenu("");
    }

    // The server binds on its own thread, wait for it before sending JOIN
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

        // Client first, so its LEAVE: still reaches the server
        tcpClient.Disconnect();
        udpClient.Disconnect();

        if (m_hosting)
        {
            tcpServer.Disconnect();
            udpServer.Disconnect();
        }
        m_hosting = false;
    }

    void OnPlayersUpdated(List<string> players)
    {
        // The first list means the server accepted the JOIN
        if (m_state == State.Connecting && players.Count > 0) ShowWaitingRoom();

        if (m_state == State.InRoom)
            playerListText.text = "Players (" + players.Count + ")\n\n" + string.Join("\n", players);
    }

    void OnChatMessageReceived(string line)
    {
        if (m_state != State.InRoom) return;

        chatHistoryText.text = string.Join("\n", ChatHistory);
    }

    void OnChatSubmitted(string text)
    {
        SendChat();
    }

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

    // Connect() on a UDP socket sends nothing, it only picks the interface the system would use
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
            return "127.0.0.1";   // no network
        }
    }
}
