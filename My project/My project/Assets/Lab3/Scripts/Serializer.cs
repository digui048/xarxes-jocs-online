using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

public static class Serializer
{
    public static byte[] Serialize(PlayerDTO dto)
    {
        MemoryStream stream = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(stream);

        writer.Write(dto.playerName);
        writer.Write(dto.level);
        writer.Write(dto.ownedPokemons.Count);
        foreach (string pokemon in dto.ownedPokemons)
        {
            writer.Write(pokemon);
        }

        return stream.ToArray();
    }

    public static byte[] Serialize(GameStartDTO dto)
    {
        MemoryStream stream = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(stream);

        writer.Write(dto.seed);
        writer.Write(dto.duration);
        writer.Write(dto.yourId);
        writer.Write(dto.climbers.Count);
        foreach (ClimberDTO climber in dto.climbers)
        {
            writer.Write(climber.id);
            writer.Write(climber.name);
        }

        return stream.ToArray();
    }

    public static GameStartDTO DeserializeGameStart(byte[] data)
    {
        BinaryReader reader = new BinaryReader(new MemoryStream(data));
        GameStartDTO dto = new GameStartDTO();
        dto.climbers = new List<ClimberDTO>();

        dto.seed = reader.ReadInt32();
        dto.duration = reader.ReadSingle();
        dto.yourId = reader.ReadInt32();
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            ClimberDTO climber = new ClimberDTO();
            climber.id = reader.ReadInt32();
            climber.name = reader.ReadString();
            dto.climbers.Add(climber);
        }

        return dto;
    }

    public static PlayerDTO DeserializePlayerDTO(byte[] data)
    {
        BinaryReader reader = new BinaryReader(new MemoryStream(data));
        PlayerDTO dto = new PlayerDTO();
        dto.ownedPokemons = new List<string>();

        dto.playerName = reader.ReadString(); 
        dto.level = reader.ReadInt32(); 
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            dto.ownedPokemons.Add(reader.ReadString());
        }

        return dto;
    }

    public static byte[] Serialize(PlayerDTO2 dto)
    {
        MemoryStream stream = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(stream);

        writer.Write(dto.playerName);
        writer.Write(dto.level);
        writer.Write(dto.ownedPokemons.Count);
        foreach (Pokemon pokemon in dto.ownedPokemons)
        {
            writer.Write(pokemon.name);
        }

        return stream.ToArray();
    }

    public static PlayerDTO2 DeserializePlayerDTO2(byte[] data)
    {
        BinaryReader reader = new BinaryReader(new MemoryStream(data));
        PlayerDTO2 dto = new PlayerDTO2();
        dto.ownedPokemons = new List<Pokemon>();

        dto.playerName = reader.ReadString();
        dto.level = reader.ReadInt32();
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            Pokemon pokemon = new Pokemon();
            pokemon.name = reader.ReadString();
            dto.ownedPokemons.Add(pokemon);
        }

        return dto;
    }

    public static byte[] ToJson(PlayerDTO2 dto)
    {
        return Encoding.UTF8.GetBytes(JsonUtility.ToJson(dto)); ;
    }

    public static PlayerDTO2 FromJson(byte[] data)
    {
        return JsonUtility.FromJson<PlayerDTO2>(Encoding.UTF8.GetString(data));
    }

    public static string ToHex(byte[] data)
    {
        if (data == null) return "(null)";
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2"));
        }
        return sb.ToString();
    }
}

public class TodoException : Exception
{
    public readonly int number;
    public TodoException(int number, string detail) : base(detail) { this.number = number; }
}

public static class Todo
{
    public static void NotDone(int number, string detail) { throw new TodoException(number, detail); }
}
