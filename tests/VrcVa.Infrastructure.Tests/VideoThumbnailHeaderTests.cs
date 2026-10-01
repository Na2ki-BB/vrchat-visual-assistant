using System.Buffers.Binary;
using System.Text;
using VrcVa.Core;

namespace VrcVa.Infrastructure.Tests;

public sealed class VideoThumbnailHeaderTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(320, 180)]
    [InlineData(1024, 1024)]
    public void Read_AcceptsStaticPngAndJpegAtBoundedDimensions(int width, int height)
    {
        Assert.Equal(new(width, height, VideoThumbnailFormat.Png), VideoThumbnailHeader.Read(Png((uint)width, (uint)height)));
        Assert.Equal(new(width, height, VideoThumbnailFormat.Jpeg), VideoThumbnailHeader.Read(Jpeg((ushort)width, (ushort)height)));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(320, 180)]
    [InlineData(1024, 1024)]
    public void Read_InspectsBothWebPBitstreamsAndExtendedCanvas(int width, int height)
    {
        VideoThumbnailHeader expected = new(width, height, VideoThumbnailFormat.WebP);
        Assert.Equal(expected, VideoThumbnailHeader.Read(WebP(("VP8 ", Lossy(width, height)))));
        Assert.Equal(expected, VideoThumbnailHeader.Read(WebP(("VP8L", Lossless(width, height)))));
        Assert.Equal(expected, VideoThumbnailHeader.Read(WebP(
            ("VP8X", Canvas(width, height)), ("VP8L", Lossless(width, height)))));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1025, 1)]
    [InlineData(1, 1025)]
    [InlineData(65535, 65535)]
    public void Read_RejectsPngAndJpegBombDimensionsBeforeCodecUse(int width, int height)
    {
        Invalid(Png((uint)width, (uint)height));
        Invalid(Jpeg((ushort)width, (ushort)height));
    }

    [Theory]
    [InlineData(1025, 1)]
    [InlineData(1, 1025)]
    [InlineData(16384, 16384)]
    public void Read_RejectsWebPBombDimensionsInAllHeaders(int width, int height)
    {
        Invalid(WebP(("VP8 ", Lossy(width, height))));
        Invalid(WebP(("VP8L", Lossless(width, height))));
        Invalid(WebP(("VP8X", Canvas(width, height)), ("VP8L", Lossless(1, 1))));
    }

    [Fact]
    public void Read_RejectsUnsignedPngDimensionOverflow()
    {
        Invalid(Png(uint.MaxValue, 1));
        Invalid(Png(1, uint.MaxValue));
    }

    [Theory]
    [InlineData(16, 6, 0, 0, 0)]
    [InlineData(16, 2, 0, 0, 0)]
    [InlineData(8, 7, 0, 0, 0)]
    [InlineData(4, 6, 0, 0, 0)]
    [InlineData(8, 6, 1, 0, 0)]
    [InlineData(8, 6, 0, 1, 0)]
    [InlineData(8, 6, 0, 0, 2)]
    public void Read_RejectsHighDepthOrUnsupportedPngHeaderBeforeNativePixelCache(
        byte depth, byte color, byte compression, byte filter, byte interlace)
    {
        byte[] png = Png(1024, 1024);
        png[24] = depth;
        png[25] = color;
        png[26] = compression;
        png[27] = filter;
        png[28] = interlace;
        Invalid(png);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(4, 0)]
    [InlineData(8, 0)]
    [InlineData(8, 2)]
    [InlineData(1, 3)]
    [InlineData(2, 3)]
    [InlineData(4, 3)]
    [InlineData(8, 3)]
    [InlineData(8, 4)]
    [InlineData(8, 6)]
    public void Read_AllowsBoundedPngPixelFormatsAndInterlacing(byte depth, byte color)
    {
        byte[] png = Png(1, 1);
        png[24] = depth;
        png[25] = color;
        png[28] = 1;
        Assert.Equal(new(1, 1, VideoThumbnailFormat.Png), VideoThumbnailHeader.Read(png));
    }

    [Theory]
    [InlineData("acTL")]
    [InlineData("fcTL")]
    [InlineData("fdAT")]
    public void Read_RejectsAnyPngAnimationChunkEvenAfterDefaultImage(string kind)
    {
        Invalid(Png(1, 1, (kind, new byte[8])));
    }

    [Theory]
    [InlineData("ANIM")]
    [InlineData("ANMF")]
    public void Read_RejectsWebPAnimationChunksWithoutAnimationFlag(string kind)
    {
        Invalid(WebP(("VP8L", Lossless(1, 1)), (kind, new byte[8])));
    }

    [Fact]
    public void Read_RejectsWebPAnimationFlagEvenForOneImage()
    {
        byte[] canvas = Canvas(1, 1);
        canvas[0] = 0x02;
        Invalid(WebP(("VP8X", canvas), ("VP8L", Lossless(1, 1))));
    }

    [Fact]
    public void Read_RejectsWebPDuplicateBitstreamOrInconsistentCanvas()
    {
        Invalid(WebP(("VP8L", Lossless(1, 1)), ("VP8 ", Lossy(1, 1))));
        Invalid(WebP(("VP8X", Canvas(1, 1)), ("VP8L", Lossless(2, 1))));
        Invalid(WebP(("VP8L", Lossless(1, 1)), ("VP8X", Canvas(1, 1))));
        Invalid(WebP(("VP8X", Canvas(1, 1)), ("VP8X", Canvas(1, 1)), ("VP8L", Lossless(1, 1))));
    }

    [Fact]
    public void Read_RejectsWebPNonKeyFrameAndWrongSignaturesOrVersion()
    {
        byte[] lossy = Lossy(1, 1);
        lossy[0] = 1;
        Invalid(WebP(("VP8 ", lossy)));
        lossy = Lossy(1, 1);
        lossy[3] = 0;
        Invalid(WebP(("VP8 ", lossy)));
        byte[] lossless = Lossless(1, 1);
        lossless[0] = 0;
        Invalid(WebP(("VP8L", lossless)));
        lossless = Lossless(1, 1);
        lossless[4] |= 0x20;
        Invalid(WebP(("VP8L", lossless)));
    }

    [Fact]
    public void Read_RejectsUnsupportedMagicEmptyOversizeAndPrivateTextWithoutEcho()
    {
        Invalid([]);
        Invalid("GIF89a"u8.ToArray());
        Invalid("BM"u8.ToArray());
        Invalid("private title or URL"u8.ToArray());
        Invalid(new byte[VideoThumbnailImage.MaximumEncodedBytes + 1]);
    }

    [Fact]
    public void Read_RejectsEveryTruncatedPrefixAndTrailingData()
    {
        foreach (byte[] image in new[] { Png(1, 1), Jpeg(1, 1), WebP(("VP8L", Lossless(1, 1))) })
        {
            for (int count = 0; count < image.Length; count++) { Invalid(image[..count]); }
            Invalid([.. image, 0]);
        }
    }

    [Fact]
    public void Read_RejectsOverflowingChunkLengthsAndIncorrectRiffSizeOrPadding()
    {
        byte[] png = Png(1, 1);
        BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(8, 4), uint.MaxValue);
        Invalid(png);
        byte[] webP = WebP(("VP8L", Lossless(1, 1)));
        BinaryPrimitives.WriteUInt32LittleEndian(webP.AsSpan(16, 4), uint.MaxValue);
        Invalid(webP);
        webP = WebP(("VP8L", Lossless(1, 1)));
        webP[4]++;
        Invalid(webP);
        webP = WebP(("VP8L", Lossless(1, 1)));
        webP[^1] = 1;
        Invalid(webP);
    }

    [Fact]
    public void Read_RequiresUniqueInitialPngHeaderImageDataAndTerminator()
    {
        Invalid(Png(1, 1, ("IHDR", new byte[13])));
        byte[] png = Png(1, 1);
        png[12] = (byte)'X';
        Invalid(png);
        Invalid([.. Png(1, 1)[..33], .. Png(1, 1)[^12..]]);
        Invalid(WebP(("EXIF", new byte[2])));
    }

    [Theory]
    [InlineData("eXIf")]
    [InlineData("iCCP")]
    [InlineData("zTXt")]
    [InlineData("iTXt")]
    [InlineData("tEXt")]
    [InlineData("unKn")]
    public void Read_RejectsPngCompressedNestedAndUnrelatedMetadataBeforeNativeDecode(string kind)
    {
        Invalid(Png(1, 1, (kind, Jpeg(65535, 65535))));
    }

    [Theory]
    [InlineData(0xe1)]
    [InlineData(0xe2)]
    [InlineData(0xe3)]
    [InlineData(0xed)]
    [InlineData(0xef)]
    public void Read_RejectsJpegMetadataContainingAnUncheckedNestedThumbnail(int marker)
    {
        Invalid(Jpeg(1, 1, ((byte)marker, [.. "Exif\0\0"u8, .. Jpeg(65535, 65535)])));
    }

    [Fact]
    public void Read_AllowsOnlyOrdinaryZeroThumbnailJfifAndFixedSizeAdobeApplicationData()
    {
        byte[] jfif = [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0];
        byte[] adobe = [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1];
        Assert.Equal(new(1, 1, VideoThumbnailFormat.Jpeg), VideoThumbnailHeader.Read(Jpeg(1, 1, (0xe0, jfif), (0xee, adobe))));
        Invalid(Jpeg(1, 1, (0xe0, [.. "JFXX\0"u8, 0x10, .. Jpeg(65535, 65535)])));
        jfif[12] = 1;
        Invalid(Jpeg(1, 1, (0xe0, [.. jfif, 0, 0, 0])));
        Invalid(Jpeg(1, 1, (0xee, [.. adobe, 0])));
    }

    [Fact]
    public void Read_ValidatesFixedSizePngDisplayMetadata()
    {
        Assert.Equal(new(1, 1, VideoThumbnailFormat.Png), VideoThumbnailHeader.Read(Png(1, 1, ("pHYs", new byte[9]), ("sRGB", [0]))));
        Invalid(Png(1, 1, ("pHYs", new byte[10])));
        Invalid(Png(1, 1, ("PLTE", new byte[769])));
    }

    [Fact]
    public void Read_RejectsJpegDynamicHeightMultipleFramesAndMultiPictureMetadata()
    {
        Invalid(Jpeg(1, 1, (0xdc, new byte[2])));
        Invalid(Jpeg(1, 1, (0xc0, Frame(1, 1))));
        Invalid(Jpeg(1, 1, (0xe2, "MPF\0"u8.ToArray())));
        Invalid([.. Jpeg(1, 1), .. Jpeg(1, 1)]);
        byte[] jpeg = Jpeg(1, 1);
        jpeg[3] = 0xc3;
        Invalid(jpeg);
    }

    [Fact]
    public void Read_AcceptsProgressiveJpegMultipleScansStuffingAndRestartMarkers()
    {
        byte[] jpeg = Jpeg(32, 16);
        jpeg[3] = 0xc2;
        byte[] scan = Segment(0xda, new byte[] { 1, 1, 0, 0, 63, 0 });
        jpeg = [.. jpeg[..^2], 0xff, 0x00, 0xff, 0xd0, .. scan, 42, 0xff, 0xd9];
        Assert.Equal(new(32, 16, VideoThumbnailFormat.Jpeg), VideoThumbnailHeader.Read(jpeg));
    }

    [Fact]
    public void Read_EnforcesEncodedLimitExactly()
    {
        byte[] small = Png(1, 1);
        byte[] padding = new byte[VideoThumbnailImage.MaximumEncodedBytes - small.Length - 12];
        byte[] image = Png(1, 1, ("IDAT", padding));
        Assert.Equal(VideoThumbnailImage.MaximumEncodedBytes, image.Length);
        Assert.Equal(1, VideoThumbnailHeader.Read(image).Width);
        Invalid(Png(1, 1, ("IDAT", [.. padding, 0])));
    }

    [Fact]
    public void Read_MutatedHeadersEitherStayBoundedOrRejectWithoutParserExceptions()
    {
        byte[][] originals = [Png(1, 1), Jpeg(1, 1), WebP(("VP8L", Lossless(1, 1)))];
        foreach (byte[] original in originals)
        {
            for (int index = 0; index < original.Length; index++)
            {
                for (int value = 0; value <= byte.MaxValue; value += 17)
                {
                    byte[] mutated = (byte[])original.Clone();
                    mutated[index] = (byte)value;
                    try
                    {
                        VideoThumbnailHeader header = VideoThumbnailHeader.Read(mutated);
                        Assert.True(VideoThumbnailImage.AreDimensionsAllowed(header.Width, header.Height));
                    }
                    catch (InvalidDataException) { }
                }
            }
        }
    }

    private static void Invalid(byte[] bytes)
    {
        InvalidDataException failure = Assert.Throws<InvalidDataException>(() => VideoThumbnailHeader.Read(bytes));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("private title", failure.ToString());
    }

    // These self-authored containers test header preflight only. The platform codec validates pixel data/CRC.
    private static byte[] Png(uint width, uint height, params (string Kind, byte[] Bytes)[] extra)
    {
        using MemoryStream stream = new();
        stream.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
        byte[] header = new byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0, 4), width);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4, 4), height);
        header[8] = 8;
        header[9] = 6;
        WritePngChunk(stream, "IHDR", header);
        WritePngChunk(stream, "IDAT", [0]);
        foreach ((string kind, byte[] bytes) in extra) { WritePngChunk(stream, kind, bytes); }
        WritePngChunk(stream, "IEND", []);
        return stream.ToArray();
    }

    private static void WritePngChunk(Stream stream, string kind, byte[] bytes)
    {
        byte[] length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)bytes.Length);
        stream.Write(length);
        stream.Write(Encoding.ASCII.GetBytes(kind));
        stream.Write(bytes);
        stream.Write(new byte[4]);
    }

    private static byte[] Jpeg(ushort width, ushort height, params (byte Marker, byte[] Bytes)[] extra)
    {
        using MemoryStream stream = new();
        stream.Write(new byte[] { 0xff, 0xd8 });
        stream.Write(Segment(0xc0, Frame(width, height)));
        foreach ((byte marker, byte[] bytes) in extra) { stream.Write(Segment(marker, bytes)); }
        stream.Write(Segment(0xda, [1, 1, 0, 0, 63, 0]));
        stream.Write(new byte[] { 42, 0xff, 0xd9 });
        return stream.ToArray();
    }

    private static byte[] Frame(ushort width, ushort height)
    {
        byte[] bytes = [8, 0, 0, 0, 0, 1, 1, 0x11, 0];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(1, 2), height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(3, 2), width);
        return bytes;
    }

    private static byte[] Segment(byte marker, byte[] payload)
    {
        byte[] bytes = [0xff, marker, 0, 0, .. payload];
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(2, 2), checked((ushort)(payload.Length + 2)));
        return bytes;
    }

    private static byte[] WebP(params (string Kind, byte[] Bytes)[] chunks)
    {
        using MemoryStream stream = new();
        stream.Write("RIFF\0\0\0\0WEBP"u8);
        foreach ((string kind, byte[] bytes) in chunks)
        {
            stream.Write(Encoding.ASCII.GetBytes(kind));
            byte[] length = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(length, (uint)bytes.Length);
            stream.Write(length);
            stream.Write(bytes);
            if ((bytes.Length & 1) != 0) { stream.WriteByte(0); }
        }
        byte[] image = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(4, 4), (uint)image.Length - 8);
        return image;
    }

    private static byte[] Canvas(int width, int height) =>
        [0, 0, 0, 0, (byte)(width - 1), (byte)((width - 1) >> 8), (byte)((width - 1) >> 16),
            (byte)(height - 1), (byte)((height - 1) >> 8), (byte)((height - 1) >> 16)];

    private static byte[] Lossy(int width, int height)
    {
        byte[] bytes = [0, 0, 0, 0x9d, 0x01, 0x2a, 0, 0, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6, 2), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8, 2), (ushort)height);
        return bytes;
    }

    private static byte[] Lossless(int width, int height)
    {
        byte[] bytes = new byte[5];
        bytes[0] = 0x2f;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(1, 4), (uint)((width - 1) | ((height - 1) << 14)));
        return bytes;
    }
}
