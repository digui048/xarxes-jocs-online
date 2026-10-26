using System;
using System.Collections.Generic;

[Serializable]
public struct ClimberDTO
{
    public int id;
    public string name;
}

[Serializable]
public struct GameStartDTO
{
    public int seed;
    public float duration;
    public int yourId;
    public List<ClimberDTO> climbers;
}
