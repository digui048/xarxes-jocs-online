using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

public class LeafLeapGame : MonoBehaviour
{
    public const string SceneName = "LeafLeap";
    const string MenuScene = "MainMenu";

    const int Levels = 100;
    const int MetresPerLeaf = 10;
    const float ColumnSpacing = 4f;
    const float LevelHeight = 1.2f;
    const float LeafOffset = 0.9f;
    const float LeafThickness = 0.15f;
    const float ClimberHalfHeight = 0.6f;

    const float JumpTime = 0.25f;            // seconds
    const float JumpArc = 0.8f;
    const float FallTime = 0.35f;            // seconds, plus FallTimePerLevel for each leaf lost
    const float FallTimePerLevel = 0.04f;
    const float MissPenalty = 0.3f;          // seconds without control after a fall
    const int ChargeStreak = 10;             // right jumps in a row that charge the super jump
    const int SuperJumpLeaves = 3;
    const float SuperJumpTime = 0.45f;
    const float SuperJumpArc = 1.6f;

    const float SendInterval = 0.05f;        // 20 states per second
    const float RemoteSmoothing = 20f;
    const float RemoteTimeout = 3f;          // seconds without states before a capsule is hidden
    const float ResultsGrace = 0.5f;         // seconds the host waits after the end for the last states
    const float ResultsRepeat = 1f;          // seconds between copies of the results
    const float CameraAbove = 3f;
    const float FacingCamera = 180f;

    static readonly Color[] ClimberColors =
    {
        new Color(0.9f, 0.25f, 0.25f), new Color(0.25f, 0.45f, 0.95f), new Color(0.95f, 0.8f, 0.2f),
        new Color(0.65f, 0.3f, 0.85f), new Color(1f, 0.55f, 0.15f), new Color(0.2f, 0.8f, 0.8f),
    };
    static readonly string[] ColorNames = { "red", "blue", "yellow", "purple", "orange", "cyan" };
    static readonly Color StalkColor = new Color(0.25f, 0.5f, 0.2f);
    static readonly Color LeafColor = new Color(0.35f, 0.75f, 0.25f);
    static readonly Color GroundColor = new Color(0.45f, 0.3f, 0.2f);
    static readonly Color VisorColor = new Color(0.12f, 0.12f, 0.18f);
    static readonly Color ChargingColor = new Color(0.3f, 0.8f, 1f);
    static readonly Color ReadyColor = new Color(1f, 0.85f, 0.1f);
    static readonly Color ReadyFlash = new Color(1f, 0.45f, 0.1f);

    public TMP_Text infoText;
    public TMP_Text timerText;
    public GameObject resultsPanel;
    public TMP_Text resultsText;
    public GameObject playAgainButton;
    public RectTransform chargePanel;
    public UnityEngine.UI.Image chargeFill;
    public TMP_Text chargeText;
    public UnityEngine.UI.Outline chargeGlow;
    public Camera gameCamera;
    public Material baseMaterial;   // a URP Lit asset, so builds include the shader

    class Climber
    {
        public Transform body;
        public Vector3 target;               // the others move towards their last state
        public Quaternion targetRotation;
        public float lastStateTime;
    }

    enum Phase { Playing, TimeUp, Results }

    SocketsUDPClient m_client;
    SocketsUDPServer m_server;   // only on the host
    GameStartDTO m_game;
    int[] m_sides;               // side of the leaf at each level: -1 left, 1 right
    readonly Dictionary<int, Climber> m_climbers = new Dictionary<int, Climber>();
    readonly Dictionary<Color, Material> m_materials = new Dictionary<Color, Material>();
    bool m_leaving;

    Phase m_phase = Phase.Playing;
    float m_roundStart;
    GameOverDTO m_results;
    float m_nextResults;

    Climber m_me;
    int m_myColumn;
    int m_level;                 // 0 is the ground
    int m_streak;
    bool m_charged;
    float m_facing = FacingCamera;
    Vector3 m_jumpFrom;
    Vector3 m_jumpTo;
    float m_jumpArc;
    float m_jumpStart;
    float m_jumpDuration;
    float m_busyUntil;
    float m_nextSend;

    void Start()
    {
        m_client = FindFirstObjectByType<SocketsUDPClient>();
        m_server = FindFirstObjectByType<SocketsUDPServer>();
        if (m_client == null || !m_client.IsRunning)
        {
            BackToMenu("The connection with the server was lost");
            return;
        }

        m_game = m_client.CurrentGame;
        m_sides = LeafSides(m_game.seed, Levels);

        BuildGround();
        for (int column = 0; column < m_game.climbers.Count; column++) BuildColumn(column, m_game.climbers[column].id);

        m_myColumn = ColumnOf(m_game.yourId);
        m_me = m_climbers[m_game.yourId];
        m_jumpFrom = m_jumpTo = m_me.body.position;
        gameCamera.transform.position = new Vector3(0f, m_jumpTo.y + CameraAbove, -12f);
        m_roundStart = Time.time;
        resultsPanel.SetActive(false);
        ShowInfo();

        m_client.OnTransformReceived += OnRemoteState;
        m_client.OnGameOver += OnGameOver;
        m_client.OnGameStarted += OnNewRound;
    }

    void OnDestroy()
    {
        if (m_client == null) return;
        m_client.OnTransformReceived -= OnRemoteState;
        m_client.OnGameOver -= OnGameOver;
        m_client.OnGameStarted -= OnNewRound;
    }

    void Update()
    {
        if (m_leaving) return;
        if (m_client == null || !m_client.IsRunning)
        {
            BackToMenu("The connection with the server was lost");
            return;
        }

        float timeLeft = m_game.duration - (Time.time - m_roundStart);
        if (m_phase == Phase.Playing)
        {
            if (timeLeft > 0f) ReadInput();
            else
            {
                m_phase = Phase.TimeUp;
                ShowInfo();
            }
        }
        if (m_server != null) DecideRound(timeLeft);

        MoveMe();
        MoveOthers();
        SendState();
        FollowWithCamera();
        ShowCharge();
        timerText.text = m_phase == Phase.Playing ? "0:" + Mathf.CeilToInt(timeLeft).ToString("00") : "";
    }

    public void Leave()
    {
        BackToMenu("");
    }

    // Host only: the server starts another round with a new beanstalk
    public void PlayAgain()
    {
        if (m_server != null && m_phase == Phase.Results) m_server.StartGame(m_game.duration);
    }

    // The network objects were kept alive for this scene, so they go away here.
    // Their OnDestroy() sends LEAVE: and stops the server
    void BackToMenu(string status)
    {
        if (m_leaving) return;
        m_leaving = true;

        LobbyUI.PendingStatus = status;
        if (m_client != null) Destroy(m_client.gameObject);
        if (m_server != null) Destroy(m_server.gameObject);
        SceneManager.LoadScene(MenuScene);
    }

    void OnNewRound(GameStartDTO game)
    {
        SceneManager.LoadScene(SceneName);
    }

    // ---------------------------------------------------------------------------------- round

    // Only the host decides who won, from the states its server received. It waits a moment after
    // the end for the last ones, and repeats the results in case a datagram is lost
    void DecideRound(float timeLeft)
    {
        if (timeLeft > -ResultsGrace || Time.time < m_nextResults) return;

        if (m_results.results == null) m_results = ComputeResults();
        m_server.BroadcastGameOver(m_results);
        m_nextResults = Time.time + ResultsRepeat;
    }

    GameOverDTO ComputeResults()
    {
        List<ResultDTO> results = new List<ResultDTO>();
        foreach (TransformDTO state in m_server.GetStates())
            if (m_climbers.ContainsKey(state.id))
                results.Add(new ResultDTO { id = state.id, metres = LevelAt(state.y) * MetresPerLeaf });
        results.Sort((a, b) => b.metres.CompareTo(a.metres));
        return new GameOverDTO { results = results };
    }

    void OnGameOver(GameOverDTO results)
    {
        m_phase = Phase.Results;
        ShowInfo();
        resultsPanel.SetActive(true);
        playAgainButton.SetActive(m_server != null);

        List<string> lines = new List<string> { "Results", "" };
        List<string> winners = new List<string>();
        for (int i = 0; i < results.results.Count; i++)
        {
            ResultDTO r = results.results[i];
            lines.Add((i + 1) + ".   " + NameOf(r.id) + "   " + r.metres + " m");
            if (r.metres == results.results[0].metres) winners.Add(NameOf(r.id));
        }
        lines.Add("");
        if (winners.Count == 1) lines.Add(winners[0] + " wins!");
        else if (winners.Count > 1) lines.Add(string.Join(" and ", winners) + " win!");
        if (m_server == null) lines.Add("Waiting for the host to play again");
        resultsText.text = string.Join("\n", lines);
    }

    // ------------------------------------------------------------------------------- climbing

    void ReadInput()
    {
        Keyboard keyboard = Keyboard.current;
        if (keyboard == null || Time.time < m_busyUntil) return;

        if (keyboard.spaceKey.wasPressedThisFrame && m_charged) SuperJump();
        else if (keyboard.leftArrowKey.wasPressedThisFrame || keyboard.aKey.wasPressedThisFrame) Jump(-1);
        else if (keyboard.rightArrowKey.wasPressedThisFrame || keyboard.dKey.wasPressedThisFrame) Jump(1);
    }

    // The right side climbs one leaf. The wrong one falls to the first leaf below on that side,
    // or to the ground, and breaks the streak
    void Jump(int side)
    {
        int target = m_level + 1;
        bool hit = target <= Levels && m_sides[target - 1] == side;
        if (!hit)
        {
            target = m_level;
            while (target > 0 && m_sides[target - 1] != side) target--;
        }

        m_facing = side * 90f;
        if (hit)
        {
            m_streak++;
            if (m_streak >= ChargeStreak) m_charged = true;
            StartJump(target, JumpTime, JumpArc, 0f);
        }
        else
        {
            m_streak = 0;
            StartJump(target, FallTime + (m_level - target) * FallTimePerLevel, JumpArc, MissPenalty);
        }
    }

    void SuperJump()
    {
        int target = Mathf.Min(m_level + SuperJumpLeaves, Levels);
        m_charged = false;
        m_streak = 0;
        m_facing = m_sides[target - 1] * 90f;
        StartJump(target, SuperJumpTime, SuperJumpArc, 0f);
    }

    void StartJump(int target, float duration, float arc, float stun)
    {
        m_jumpFrom = m_me.body.position;
        m_jumpTo = StandPosition(m_myColumn, target);
        m_jumpArc = arc;
        m_jumpStart = Time.time;
        m_jumpDuration = duration;
        m_busyUntil = m_jumpStart + duration + stun;
        m_level = target;
        ShowInfo();
    }

    void MoveMe()
    {
        float t = m_jumpDuration > 0f ? Mathf.Clamp01((Time.time - m_jumpStart) / m_jumpDuration) : 1f;
        m_me.body.position = Vector3.Lerp(m_jumpFrom, m_jumpTo, t) + Vector3.up * (4f * t * (1f - t) * m_jumpArc);
        m_me.body.rotation = Quaternion.Euler(0f, m_facing, 0f);
    }

    // ---------------------------------------------------------------------------------- states

    void SendState()
    {
        if (Time.time < m_nextSend) return;
        m_nextSend = Time.time + SendInterval;

        Vector3 p = m_me.body.position;
        m_client.SendTransform(new TransformDTO { id = m_game.yourId, x = p.x, y = p.y, z = p.z, rotY = m_facing });
    }

    void OnRemoteState(TransformDTO state)
    {
        Climber climber;
        if (state.id == m_game.yourId || !m_climbers.TryGetValue(state.id, out climber)) return;

        climber.target = new Vector3(state.x, state.y, state.z);
        climber.targetRotation = Quaternion.Euler(0f, state.rotY, 0f);
        climber.lastStateTime = Time.time;
    }

    // States arrive 20 times per second, so the others glide towards the last one.
    // A capsule that stops getting states belongs to a player who left
    void MoveOthers()
    {
        float blend = 1f - Mathf.Exp(-RemoteSmoothing * Time.deltaTime);
        foreach (Climber climber in m_climbers.Values)
        {
            if (climber == m_me) continue;
            climber.body.gameObject.SetActive(Time.time - climber.lastStateTime < RemoteTimeout);
            climber.body.position = Vector3.Lerp(climber.body.position, climber.target, blend);
            climber.body.rotation = Quaternion.Slerp(climber.body.rotation, climber.targetRotation, blend);
        }
    }

    // -------------------------------------------------------------------------------------- view

    void FollowWithCamera()
    {
        Vector3 position = gameCamera.transform.position;
        position.y = Mathf.Lerp(position.y, m_me.body.position.y + CameraAbove, 1f - Mathf.Exp(-5f * Time.deltaTime));
        gameCamera.transform.position = position;
    }

    void ShowInfo()
    {
        string text = "Leaf Leap   -   you are the " + ColorNames[m_myColumn % ColorNames.Length] +
            " capsule   -   " + m_level * MetresPerLeaf + " m";
        if (m_phase == Phase.TimeUp) text += "   -   time is up!";
        infoText.text = text;
    }

    // The bar fills with every right jump and pulses once the super jump is ready
    void ShowCharge()
    {
        chargePanel.gameObject.SetActive(m_phase == Phase.Playing);
        if (m_phase != Phase.Playing) return;

        float fill = m_charged ? 1f : (float)m_streak / ChargeStreak;
        chargeFill.rectTransform.anchorMax = new Vector2(fill, 1f);
        chargeGlow.enabled = m_charged;
        if (m_charged)
        {
            float pulse = Mathf.PingPong(Time.time * 4f, 1f);
            chargeText.text = "SPACE: SUPER JUMP!";
            chargeText.color = Color.Lerp(ReadyColor, Color.white, pulse);
            chargeFill.color = Color.Lerp(ReadyColor, ReadyFlash, pulse);
            chargeGlow.effectColor = Color.Lerp(ReadyColor, ReadyFlash, pulse);
            chargePanel.localScale = Vector3.one * (1f + 0.08f * pulse);
        }
        else
        {
            chargeText.text = "Super jump   " + m_streak + " / " + ChargeStreak;
            chargeText.color = Color.white;
            chargeFill.color = ChargingColor;
            chargePanel.localScale = Vector3.one;
        }
    }

    string NameOf(int id)
    {
        foreach (ClimberDTO climber in m_game.climbers)
            if (climber.id == id) return climber.name;
        return "?";
    }

    // ------------------------------------------------------------------------------ beanstalks

    // The same seed gives the same beanstalk on every screen. Never more than 3 leaves in a row on one side
    public static int[] LeafSides(int seed, int count)
    {
        System.Random random = new System.Random(seed);
        int[] sides = new int[count];
        int run = 0;
        for (int i = 0; i < count; i++)
        {
            int side = random.Next(2) == 0 ? -1 : 1;
            run = i > 0 && side == sides[i - 1] ? run + 1 : 1;
            if (run > 3)
            {
                side = -side;
                run = 1;
            }
            sides[i] = side;
        }
        return sides;
    }

    // The leaf a capsule at this height stands on, from its position in a Transform message
    static int LevelAt(float y)
    {
        return Mathf.Max(0, Mathf.RoundToInt((y - ClimberHalfHeight - LeafThickness / 2f) / LevelHeight));
    }

    int ColumnOf(int id)
    {
        for (int i = 0; i < m_game.climbers.Count; i++)
            if (m_game.climbers[i].id == id) return i;
        return 0;
    }

    float ColumnX(int column)
    {
        return (column - (m_game.climbers.Count - 1) / 2f) * ColumnSpacing;
    }

    // Level 0 is the ground, in front of the stalk
    Vector3 StandPosition(int column, int level)
    {
        float x = ColumnX(column);
        if (level == 0) return new Vector3(x, ClimberHalfHeight, -0.6f);
        return new Vector3(x + m_sides[level - 1] * LeafOffset, level * LevelHeight + LeafThickness / 2f + ClimberHalfHeight, 0f);
    }

    void BuildGround()
    {
        float width = m_game.climbers.Count * ColumnSpacing + 4f;
        Block(PrimitiveType.Cube, new Vector3(0f, -0.1f, 0f), new Vector3(width, 0.2f, 4f), GroundColor);
    }

    void BuildColumn(int column, int id)
    {
        float x = ColumnX(column);
        float top = Levels * LevelHeight;

        Block(PrimitiveType.Cylinder, new Vector3(x, top / 2f, 0f), new Vector3(0.25f, top / 2f, 0.25f), StalkColor);
        for (int level = 1; level <= Levels; level++)
        {
            Vector3 leaf = new Vector3(x + m_sides[level - 1] * LeafOffset, level * LevelHeight, 0f);
            Block(PrimitiveType.Cube, leaf, new Vector3(1.2f, LeafThickness, 0.8f), LeafColor);
        }

        Vector3 start = StandPosition(column, 0);
        GameObject body = Block(PrimitiveType.Capsule, start, Vector3.one * 0.6f, ClimberColors[column % ClimberColors.Length]);
        body.name = "Climber " + id;
        body.transform.rotation = Quaternion.Euler(0f, FacingCamera, 0f);

        // A dark visor on the front, so it shows which way the capsule jumped
        GameObject visor = Block(PrimitiveType.Cube, Vector3.zero, new Vector3(0.6f, 0.2f, 0.25f), VisorColor);
        visor.transform.SetParent(body.transform, false);
        visor.transform.localPosition = new Vector3(0f, 0.45f, 0.4f);

        m_climbers[id] = new Climber
        {
            body = body.transform,
            target = start,
            targetRotation = body.transform.rotation,
            lastStateTime = Time.time,
        };
    }

    // Nothing here uses physics, so the colliders go away. One material per colour, shared
    GameObject Block(PrimitiveType type, Vector3 position, Vector3 scale, Color color)
    {
        GameObject go = GameObject.CreatePrimitive(type);
        Destroy(go.GetComponent<Collider>());
        go.transform.position = position;
        go.transform.localScale = scale;

        Renderer renderer = go.GetComponent<Renderer>();
        Material material;
        if (!m_materials.TryGetValue(color, out material))
        {
            material = new Material(baseMaterial);
            material.color = color;
            m_materials[color] = material;
        }
        renderer.sharedMaterial = material;
        return go;
    }
}
