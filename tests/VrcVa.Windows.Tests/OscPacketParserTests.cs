using System.Buffers.Binary;
using System.Text;
using VrcVa.Windows.Osc;

namespace VrcVa.Windows.Tests;

public sealed class OscPacketParserTests
{
    [Fact]
    public void TryParse_ReadsBooleanAndIntegerMessages()
    {
        byte[] falsePacket = BuildMessage("/avatar/parameters/VRCVA_Scan", ",F", []);
        byte[] integerPacket = BuildMessage(
            "/avatar/parameters/VRCVA_Scan",
            ",i",
            WriteInt32(1));

        Assert.True(OscPacketParser.TryParse(falsePacket, out IReadOnlyList<OscMessage> falseMessages));
        Assert.False(Assert.Single(falseMessages).IsActive);
        Assert.True(OscPacketParser.TryParse(integerPacket, out IReadOnlyList<OscMessage> integerMessages));
        Assert.True(Assert.Single(integerMessages).IsActive);
    }

    [Fact]
    public void TryParse_ReadsAvatarChangeWithoutExposingItsValueToTriggerLogic()
    {
        byte[] packet = BuildMessage(
            "/avatar/change",
            ",s",
            WritePaddedString("avtr_private_value"));

        Assert.True(OscPacketParser.TryParse(packet, out IReadOnlyList<OscMessage> messages));
        OscMessage message = Assert.Single(messages);
        Assert.Equal("/avatar/change", message.Address);
        Assert.Equal(OscValueKind.String, message.Kind);
        Assert.NotNull(message.StringValue);
        Assert.False(message.IsActive);
    }

    [Fact]
    public void TryParse_RejectsBundle()
    {
        byte[] low = BuildMessage("/avatar/parameters/VRCVA_Scan", ",F", []);
        byte[] high = BuildMessage("/avatar/parameters/VRCVA_Scan", ",T", []);
        byte[] packet = BuildBundle(low, high);

        Assert.False(OscPacketParser.TryParse(packet, out IReadOnlyList<OscMessage> messages));
        Assert.Empty(messages);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 47, 97, 0, 1, 44, 84, 0, 0 })]
    public void TryParse_RejectsMalformedPackets(byte[] packet)
    {
        Assert.False(OscPacketParser.TryParse(packet, out IReadOnlyList<OscMessage> messages));
        Assert.Empty(messages);
    }

    private static byte[] BuildMessage(string address, string tags, byte[] argument)
    {
        byte[] addressBytes = WritePaddedString(address);
        byte[] tagBytes = WritePaddedString(tags);
        return [.. addressBytes, .. tagBytes, .. argument];
    }

    private static byte[] BuildBundle(params byte[][] messages)
    {
        List<byte> bytes = [.. Encoding.ASCII.GetBytes("#bundle\0"), .. new byte[8]];
        foreach (byte[] message in messages)
        {
            bytes.AddRange(WriteInt32(message.Length));
            bytes.AddRange(message);
        }

        return [.. bytes];
    }

    private static byte[] WriteInt32(int value)
    {
        byte[] bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] WritePaddedString(string value)
    {
        byte[] content = Encoding.UTF8.GetBytes(value);
        int length = (content.Length + 1 + 3) & ~3;
        byte[] bytes = new byte[length];
        content.CopyTo(bytes, 0);
        return bytes;
    }
}
