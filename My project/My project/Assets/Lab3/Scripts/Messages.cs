// =================================================================================================
//  Lab 3 - Data serialization: MESSAGES
//  XJO - Xarxes per a Jocs Online
// =================================================================================================
//
//  Every packet of the lobby starts with one byte that says what follows (slide 18):
//
//    Text    01, then the string as BinaryWriter writes it (its length, then the UTF-8 bytes).
//            These are the Lab 2 lobby messages, unchanged: JOIN:Anna, PLAYERS:Anna,Marc,
//            CHAT:hello, LEAVE:, PING:
//
//  This is the only file that knows the layout. The socket scripts call Text() to send, and
//  Open() and ReadText() when a packet arrives. TCP still puts its 4-byte length in front of all
//  this (Lab 2.1). In UDP one datagram is one message, so nothing else is needed.
//
//  Byte order: BinaryWriter and BinaryReader are always little-endian, and both ends use them,
//  so there is nothing to convert (slide 9).
//
// =================================================================================================

using System;
using System.IO;

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

    // Reads the type byte and leaves 'reader' at the first byte of the content.
    // False for an empty packet or a type we do not know. A Lab 2 build sending plain text ends
    // up here too: the 'J' of JOIN is byte 74, which is not a MsgType.
    public static bool Open(byte[] data, out MsgType type, out BinaryReader reader)
    {
        reader = new BinaryReader(new MemoryStream(data));
        type = 0;
        if (data.Length == 0) return false;

        type = (MsgType)reader.ReadByte();
        return Enum.IsDefined(typeof(MsgType), type);
    }

    // The content of a Text message, or null if the bytes are not exactly one string.
    // Anything can arrive from the network, and a broken packet must not throw in Update().
    public static string ReadText(BinaryReader reader)
    {
        try
        {
            string text = reader.ReadString();
            return reader.BaseStream.Position == reader.BaseStream.Length ? text : null;
        }
        catch (EndOfStreamException) { return null; }   // shorter than its length says
        catch (FormatException) { return null; }        // the length itself is broken
    }
}
