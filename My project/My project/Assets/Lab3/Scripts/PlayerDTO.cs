using System;
using System.Collections.Generic;

[Serializable]
public struct PlayerDTO
{
    public string playerName;
    public int level;
    public List<string> ownedPokemons;
}

[Serializable]
public class Pokemon
{
    public string name;
}

[Serializable]
public struct PlayerDTO2
{
    public string playerName;
    public int level;
    public List<Pokemon> ownedPokemons;
}

public enum MsgType : byte
{
    Text = 1,
    Player = 2,
    GameStart = 4,
}
