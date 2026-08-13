using System.Buffers.Binary;
using System.Text;

namespace VrcVa.Windows.Osc;

internal enum OscValueKind
{
    Boolean,
    Integer,
    String,
}

internal readonly record struct OscMessage(
    string Address,
    OscValueKind Kind,
    bool BooleanValue,
    int IntegerValue,
    string? StringValue)
{
    internal bool IsActive => Kind switch
    {
        OscValueKind.Boolean => BooleanValue,
        OscValueKind.Integer => IntegerValue != 0,
        _ => false,
    };
}

internal static class OscPacketParser
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static bool TryParse(ReadOnlySpan<byte> packet, out IReadOnlyList<OscMessage> messages)
    {
        if (packet.StartsWith("#bundle\0"u8)
            || !TryParseMessage(packet, out OscMessage message))
        {
            messages = [];
            return false;
        }

        messages = [message];
        return true;
    }

    private static bool TryParseMessage(ReadOnlySpan<byte> packet, out OscMessage message)
    {
        message = default;
        int offset = 0;
        if (!TryReadPaddedString(packet, ref offset, out string address)
            || string.IsNullOrWhiteSpace(address)
            || address[0] != '/'
            || !TryReadPaddedString(packet, ref offset, out string typeTags)
            || typeTags.Length != 2
            || typeTags[0] != ',')
        {
            return false;
        }

        switch (typeTags[1])
        {
            case 'T':
            case 'F':
                if (offset != packet.Length)
                {
                    return false;
                }

                message = new OscMessage(
                    address,
                    OscValueKind.Boolean,
                    typeTags[1] == 'T',
                    0,
                    null);
                return true;
            case 'i':
                if (packet.Length - offset != sizeof(int))
                {
                    return false;
                }

                message = new OscMessage(
                    address,
                    OscValueKind.Integer,
                    false,
                    BinaryPrimitives.ReadInt32BigEndian(packet[offset..]),
                    null);
                return true;
            case 's':
                if (!TryReadPaddedString(packet, ref offset, out string stringValue)
                    || offset != packet.Length)
                {
                    return false;
                }

                message = new OscMessage(
                    address,
                    OscValueKind.String,
                    false,
                    0,
                    stringValue);
                return true;
            default:
                return false;
        }
    }

    private static bool TryReadPaddedString(
        ReadOnlySpan<byte> packet,
        ref int offset,
        out string value)
    {
        value = string.Empty;
        if (offset >= packet.Length)
        {
            return false;
        }

        int terminator = packet[offset..].IndexOf((byte)0);
        if (terminator < 0)
        {
            return false;
        }

        try
        {
            value = StrictUtf8.GetString(packet.Slice(offset, terminator));
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        int consumed = terminator + 1;
        int paddedLength = (consumed + 3) & ~3;
        if (paddedLength > packet.Length - offset
            || packet.Slice(offset + consumed, paddedLength - consumed).IndexOfAnyExcept((byte)0) >= 0)
        {
            return false;
        }

        offset += paddedLength;
        return true;
    }
}
