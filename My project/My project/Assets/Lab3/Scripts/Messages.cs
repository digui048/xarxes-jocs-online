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
        MemoryStream stream = new MemoryStream();
        BinaryWriter writer = new BinaryWriter(stream);

        writer.Write((byte)MsgType.Player);
        writer.Write(Serializer.Serialize(dto));

        return stream.ToArray();
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

    // False if the rest of the packet is not a valid PlayerDTO2
    public static bool ReadPlayer(BinaryReader reader, out PlayerDTO2 dto)
    {
        Stream stream = reader.BaseStream;
        byte[] content = reader.ReadBytes((int)(stream.Length - stream.Position));

        try
        {
            dto = Serializer.DeserializePlayerDTO2(content);
            return true;
        }
        catch (IOException) { }
        catch (FormatException) { }

        dto = new PlayerDTO2();
        return false;
    }

    public static string PokemonNames(PlayerDTO2 dto)
    {
        List<string> names = new List<string>();
        foreach (Pokemon p in dto.ownedPokemons) names.Add(p.name);
        return string.Join(", ", names);
    }
}
