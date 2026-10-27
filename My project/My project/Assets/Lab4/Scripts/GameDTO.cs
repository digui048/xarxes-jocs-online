using System;
using System.Collections.Generic;

[Serializable]
public struct ClimberDTO
{
    public int id;
    public string name;
}

[Serializable]
public struct TransformDTO
{
    public int id;
    public float x;
    public float y;
    public float z;
    public float rotY;
}

[Serializable]
public struct GameStartDTO
{
    public int seed;
    public float duration;
    public int yourId;
    public List<ClimberDTO> climbers;
}

[Serializable]
public struct ResultDTO
{
    public int id;
    public int metres;
}

[Serializable]
public struct GameOverDTO
{
    public List<ResultDTO> results;   // highest first
}
