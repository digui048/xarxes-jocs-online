// =================================================================================================
//  Lab 3 - Data serialization: TEST SCENE
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  Given. Nothing to modify. Fills PlayerDTO / PlayerDTO2 with dummy data, runs each round trip
//  (serialize, deserialize, compare), and prints PASS, FAIL or SKIPPED per field plus the bytes.
//  'useSolution' runs SerializerSolution instead of your Serializer.
//
// =================================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

public class SerializationTest : MonoBehaviour
{
    public bool useSolution = false;

    readonly List<string> m_log = new List<string>();
    Vector2 m_scroll;

    // The functions under test. Swappable so the solution, or a broken version, can be checked.
    public class Impl
    {
        public Func<PlayerDTO, byte[]> serialize1;
        public Func<byte[], PlayerDTO> deserialize1;
        public Func<PlayerDTO2, byte[]> serialize2;
        public Func<byte[], PlayerDTO2> deserialize2;
        public Func<PlayerDTO2, byte[]> toJson;
        public Func<byte[], PlayerDTO2> fromJson;
    }

    public static Impl Student()
    {
        return new Impl
        {
            serialize1 = Serializer.Serialize, deserialize1 = Serializer.DeserializePlayerDTO,
            serialize2 = Serializer.Serialize, deserialize2 = Serializer.DeserializePlayerDTO2,
            toJson = Serializer.ToJson, fromJson = Serializer.FromJson,
        };
    }

    public static Impl Solution()
    {
        return new Impl
        {
            serialize1 = SerializerSolution.Serialize, deserialize1 = SerializerSolution.DeserializePlayerDTO,
            serialize2 = SerializerSolution.Serialize, deserialize2 = SerializerSolution.DeserializePlayerDTO2,
            toJson = SerializerSolution.ToJson, fromJson = SerializerSolution.FromJson,
        };
    }

    public struct Result { public int pass, fail, skipped; }

    // Dummy data: a normal player, one whose name would break a text protocol, and an empty list.
    static readonly string[] Names = { "Ash", "Anna,Marc: \"the best\"", "Newbie" };
    static readonly int[] Levels = { 12, 99, 0 };
    static readonly string[][] Teams =
    {
        new[] { "Pikachu", "Charmander" },
        new[] { "Flabébé", "Mr. Mime", "Eevee" },
        new string[0],
    };

    void Start()
    {
        Result r = RunAll(useSolution ? Solution() : Student(), Log);
        Log("");
        Log("TOTAL  " + Tag("PASS") + " " + r.pass + "   " + Tag("FAIL") + " " + r.fail +
            "   " + Tag("SKIPPED") + " " + r.skipped);
    }

    public static Result RunAll(Impl impl, Action<string> log)
    {
        Result r = new Result();
        var skippedTodos = new HashSet<int>();

        // ------------------------------------------------------------------ Challenge 1
        log("[Challenge 1] PlayerDTO, BinaryWriter (TODO 1 and 2)");
        var bytes1 = new List<byte[]>();
        for (int i = 0; i < Names.Length; i++)
        {
            PlayerDTO dto = new PlayerDTO
            {
                playerName = Names[i], level = Levels[i], ownedPokemons = new List<string>(Teams[i]),
            };
            byte[] data = null;
            PlayerDTO back = new PlayerDTO();
            if (!Step(() => data = impl.serialize1(dto), "  ", log, ref r, skippedTodos)) continue;
            bytes1.Add(data);
            log("  " + Describe(dto.playerName, dto.level, Teams[i]) + " -> serialized " + Len(data) + " bytes");
            log("    " + Serializer.ToHex(data));
            if (!Step(() => back = impl.deserialize1(data), "  ", log, ref r, skippedTodos)) continue;
            Compare(dto.playerName, dto.level, Teams[i],
                    back.playerName, back.level, back.ownedPokemons, log, ref r);
        }

        // ------------------------------------------------------------------ Challenge 2
        log("");
        log("[Challenge 2] PlayerDTO2, a list of Pokemon (TODO 3 and 4)");
        for (int i = 0; i < Names.Length; i++)
        {
            PlayerDTO2 dto = MakeDTO2(i);
            byte[] data = null;
            PlayerDTO2 back = new PlayerDTO2();
            if (!Step(() => data = impl.serialize2(dto), "  ", log, ref r, skippedTodos)) continue;
            log("  " + Describe(dto.playerName, dto.level, Teams[i]) + " -> serialized " + Len(data) + " bytes");
            log("    " + Serializer.ToHex(data));
            if (i < bytes1.Count)
                log("    same bytes as Challenge 1: " + (Same(data, bytes1[i]) ? "yes" : "no"));
            if (!Step(() => back = impl.deserialize2(data), "  ", log, ref r, skippedTodos)) continue;
            Compare(dto.playerName, dto.level, Teams[i], back.playerName, back.level,
                    back.ownedPokemons == null ? null : back.ownedPokemons.Select(p => p == null ? null : p.name).ToList(),
                    log, ref r);
        }

        // ------------------------------------------------------------------ JSON
        log("");
        log("[TODO 5] PlayerDTO2, JsonUtility");
        {
            PlayerDTO2 dto = MakeDTO2(0);
            byte[] json = null, binary = null;
            PlayerDTO2 back = new PlayerDTO2();
            if (Step(() => json = impl.toJson(dto), "  ", log, ref r, skippedTodos) &&
                Step(() => back = impl.fromJson(json), "  ", log, ref r, skippedTodos))
            {
                log("  " + (json == null ? "(null)" : System.Text.Encoding.UTF8.GetString(json)));
                try { binary = impl.serialize2(dto); } catch (Exception) { }
                log("  JSON is " + Len(json) + " bytes" +
                    (binary != null ? ", binary is " + binary.Length + ". Why? (slide 8)" : ""));
                Compare(dto.playerName, dto.level, Teams[0], back.playerName, back.level,
                        back.ownedPokemons == null ? null : back.ownedPokemons.Select(p => p == null ? null : p.name).ToList(),
                        log, ref r);
            }
        }
        return r;
    }

    static PlayerDTO2 MakeDTO2(int i)
    {
        return new PlayerDTO2
        {
            playerName = Names[i], level = Levels[i],
            ownedPokemons = Teams[i].Select(n => new Pokemon { name = n }).ToList(),
        };
    }

    // Runs one call. Unfinished TODO: SKIPPED, warned once. Any other exception: FAIL with a hint.
    static bool Step(Action call, string indent, Action<string> log, ref Result r, HashSet<int> skipped)
    {
        try { call(); return true; }
        catch (TodoException e)
        {
            if (skipped.Add(e.number)) log(indent + Tag("SKIPPED") + " [TODO " + e.number + "] not done yet: " + e.Message);
            r.skipped++;
        }
        catch (EndOfStreamException)
        {
            log(indent + Tag("FAIL") + " EndOfStreamException: you read more than you wrote, " +
                "or in a different order (slide 21)");
            r.fail++;
        }
        catch (Exception e)
        {
            log(indent + Tag("FAIL") + " " + e.GetType().Name + ": " + e.Message);
            r.fail++;
        }
        return false;
    }

    static void Compare(string name, int level, string[] team,
                        string gotName, int gotLevel, List<string> gotTeam,
                        Action<string> log, ref Result r)
    {
        Check("playerName", name == gotName, Quote(gotName), log, ref r);
        Check("level", level == gotLevel, gotLevel.ToString(), log, ref r);
        bool teamOk = gotTeam != null && gotTeam.SequenceEqual(team);
        Check("ownedPokemons", teamOk,
              gotTeam == null ? "null: did you create the list?" : "[" + string.Join(", ", gotTeam) + "]",
              log, ref r);
    }

    static void Check(string field, bool ok, string got, Action<string> log, ref Result r)
    {
        if (ok) r.pass++; else r.fail++;
        log("    " + Tag(ok ? "PASS" : "FAIL") + " " + field + (ok ? "" : "   got " + got));
    }

    static string Describe(string name, int level, string[] team)
    {
        return "{" + Quote(name) + ", " + level + ", [" + string.Join(", ", team) + "]}";
    }

    static string Quote(string s) { return s == null ? "null" : "\"" + s + "\""; }
    static string Len(byte[] b) { return b == null ? "0 (null)" : b.Length.ToString(); }
    static bool Same(byte[] a, byte[] b) { return a != null && b != null && a.SequenceEqual(b); }

    static string Tag(string t)
    {
        string color = t == "PASS" ? "#5fd35f" : t == "FAIL" ? "#ff6060" : "#e0c050";
        return "<color=" + color + ">" + t + "</color>";
    }

    void Log(string line)
    {
        // Console without the color tags, screen with them.
        Debug.Log(System.Text.RegularExpressions.Regex.Replace(line, "<.*?>", ""));
        m_log.Add(line);
    }

    void OnGUI()
    {
        var style = new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true, fontSize = 14 };
        GUILayout.BeginArea(new Rect(10, 10, Screen.width - 20, Screen.height - 20));
        GUILayout.Label("<b>LAB 3 SERIALIZATION TEST</b>   " +
                        (useSolution ? "running SerializerSolution" : "running your Serializer"), style);
        m_scroll = GUILayout.BeginScrollView(m_scroll);
        foreach (string line in m_log) GUILayout.Label(line, style);
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }
}
