// =================================================================================================
//  Lab 3 - Data serialization: THE DATA YOU SEND
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  Given. Do not change it: the test scene and the slides use these exact fields.
//  A DTO (data transfer object) is a small class or struct made only to be sent (slide 13).
//  Never send a GameObject or a MonoBehaviour.
//
// =================================================================================================

using System;
using System.Collections.Generic;

// Challenge 1 (slide 15)
[Serializable]
public struct PlayerDTO
{
    public string playerName;
    public int level;
    public List<string> ownedPokemons;
}

// Challenge 2 (slide 16)
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

// Challenge 3 (slide 18): the first byte of every message says what follows.
public enum MsgType : byte
{
    Text = 1,     // your lobby messages as they are now: JOIN:Anna, CHAT:hello...
    Player = 2,   // a PlayerDTO2
}
