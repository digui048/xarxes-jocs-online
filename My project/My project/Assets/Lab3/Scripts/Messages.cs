using System;
using System.Collections.Generic;
using System.IO;

// Every packet: one MsgType byte, then the content
public static class Messages
{
    public static byte[] Text(string text)
    {
        MemoryStream stream = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(stream);

        writer.Write((byte)MsgType.Text);
        writer.Write(text);

        return stream.ToArray();
    }

    public static byte[] Player(PlayerDTO2 dto)
    {
        return Wrap(MsgType.Player, Serializer.Serialize(dto));
    }

    public static byte[] GameStart(GameStartDTO dto)
    {
        return Wrap(MsgType.GameStart, Serializer.Serialize(dto));
    }

    // Leaves 'reader' after the type byte. False for an empty packet or an unknown type
    public static bool Open(byte[] data, out MsgType type, out BinaryReader reader)
    {
        reader = new BinaryReader(new MemoryStream(data));
        type = 0;
        if (data.Length == 0) return false;

        type = (MsgType)reader.ReadByte();
        return Enum.IsDefined(typeof(MsgType), type);
    }

    // null if the bytes are not exactly one string
    public static string ReadText(BinaryReader reader)
    {
        try
        {
            string text = reader.ReadString();
            return reader.BaseStream.Position == reader.BaseStream.Length ? text : null;
        }
        catch (IOException) { return null; }
        catch (FormatException) { return null; }
    }

    public static bool ReadPlayer(BinaryReader reader, out PlayerDTO2 dto)
    {
        return Read(reader, Serializer.DeserializePlayerDTO2, out dto);
    }

    public static bool ReadGameStart(BinaryReader reader, out GameStartDTO dto)
    {
        return Read(reader, Serializer.DeserializeGameStart, out dto);
    }

    public static string PokemonNames(PlayerDTO2 dto)
    {
        List<string> names = new List<string>();
        foreach (Pokemon p in dto.ownedPokemons) names.Add(p.name);
        return string.Join(", ", names);
    }

    static byte[] Wrap(MsgType type, byte[] content)
    {
        byte[] packet = new byte[1 + content.Length];
        packet[0] = (byte)type;
        content.CopyTo(packet, 1);
        return packet;
    }

    // False if the rest of the packet cannot be read by 'deserialize'
    static bool Read<T>(BinaryReader reader, Func<byte[], T> deserialize, out T dto)
    {
        Stream stream = reader.BaseStream;
        byte[] content = reader.ReadBytes((int)(stream.Length - stream.Position));

        try
        {
            dto = deserialize(content);
            return true;
        }
        catch (IOException) { }
        catch (FormatException) { }

        dto = default(T);
        return false;
    }
}
