using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

public class LeafLeapGame : MonoBehaviour
{
    public const string SceneName = "LeafLeap";
    const string MenuScene = "MainMenu";

    const int Levels = 100;
    const float ColumnSpacing = 4f;
    const float LevelHeight = 1.2f;
    const float LeafOffset = 0.9f;
    const float LeafThickness = 0.15f;
    const float ClimberHalfHeight = 0.6f;

    static readonly Color[] ClimberColors =
    {
        new Color(0.9f, 0.25f, 0.25f), new Color(0.25f, 0.45f, 0.95f), new Color(0.95f, 0.8f, 0.2f),
        new Color(0.65f, 0.3f, 0.85f), new Color(1f, 0.55f, 0.15f), new Color(0.2f, 0.8f, 0.8f),
    };
    static readonly string[] ColorNames = { "red", "blue", "yellow", "purple", "orange", "cyan" };
    static readonly Color StalkColor = new Color(0.25f, 0.5f, 0.2f);
    static readonly Color LeafColor = new Color(0.35f, 0.75f, 0.25f);
    static readonly Color GroundColor = new Color(0.45f, 0.3f, 0.2f);

    public TMP_Text infoText;
    public Camera gameCamera;
    public Material baseMaterial;   // a URP Lit asset, so builds include the shader

    SocketsUDPClient m_client;
    SocketsUDPServer m_server;   // only on the host
    GameStartDTO m_game;
    int[] m_sides;               // side of the leaf at each level: -1 left, 1 right
    readonly Dictionary<int, Transform> m_climbers = new Dictionary<int, Transform>();
    readonly Dictionary<Color, Material> m_materials = new Dictionary<Color, Material>();
    bool m_leaving;

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

        gameCamera.transform.position = new Vector3(0f, 4f, -12f);
        infoText.text = "Leaf Leap   -   you are the " + ColorNames[ColumnOf(m_game.yourId) % ColorNames.Length] + " capsule";
    }

    void Update()
    {
        if (!m_leaving && (m_client == null || !m_client.IsRunning)) BackToMenu("The connection with the server was lost");
    }

    public void Leave()
    {
        BackToMenu("");
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

        GameObject climber = Block(PrimitiveType.Capsule, StandPosition(column, 0), Vector3.one * 0.6f,
            ClimberColors[column % ClimberColors.Length]);
        climber.name = "Climber " + id;
        m_climbers[id] = climber.transform;
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
