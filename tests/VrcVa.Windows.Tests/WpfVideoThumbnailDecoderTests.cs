using System.Buffers.Binary;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VrcVa.Core;
using VrcVa.Windows.Video;

namespace VrcVa.Windows.Tests;

public sealed class WpfVideoThumbnailDecoderTests
{
    [Fact]
    public Task Decode_PngRoundTripReturnsStraightBgra32InMemory() => RunOnSta(() =>
    {
        byte[] pixels = [1, 2, 3, 128, 25, 50, 75, 255];
        byte[] encoded = Encode(new PngBitmapEncoder(), 2, 1, pixels);

        VideoThumbnailImage image = new WpfVideoThumbnailDecoder().Decode(encoded);

        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(pixels, image.Bgra32.ToArray());
        Array.Clear(encoded);
        Assert.Equal(pixels, image.Bgra32.ToArray());
    });

    [Fact]
    public async Task Decode_WorkerThreadReturnsOnlyOwnedPixelsWithoutDispatcher()
    {
        byte[] pixels = [1, 2, 3, 128, 25, 50, 75, 255];
        byte[]? encoded = null;
        await RunOnSta(() => encoded = Encode(new PngBitmapEncoder(), 2, 1, pixels));

        VideoThumbnailImage image = await Task.Run(() =>
        {
            Assert.Equal(ApartmentState.MTA, Thread.CurrentThread.GetApartmentState());
            return new WpfVideoThumbnailDecoder().Decode(encoded!);
        });

        Assert.Equal(pixels, image.Bgra32.ToArray());
        Assert.Equal(2, image.Width);
        Assert.Equal(1, image.Height);
    }

    [Fact]
    public Task Decode_JpegRoundTripConvertsOpaquePixelsToBgra32() => RunOnSta(() =>
    {
        byte[] pixels = Enumerable.Range(0, 3 * 2).SelectMany(_ => new byte[] { 25, 50, 75, 255 }).ToArray();
        byte[] encoded = Encode(new JpegBitmapEncoder { QualityLevel = 90 }, 3, 2, pixels);

        VideoThumbnailImage image = new WpfVideoThumbnailDecoder().Decode(encoded);

        Assert.Equal(3, image.Width);
        Assert.Equal(2, image.Height);
        Assert.Equal(24, image.Bgra32.Length);
        for (int index = 3; index < image.Bgra32.Length; index += 4)
        {
            Assert.Equal(255, image.Bgra32.Span[index]);
        }
    });

    [Fact]
    public Task Decode_AcceptsExactDimensionAndDecodedByteLimit() => RunOnSta(() =>
    {
        byte[] pixels = new byte[VideoThumbnailImage.MaximumDecodedBytes];
        byte[] encoded = Encode(new PngBitmapEncoder(), 1024, 1024, pixels);

        VideoThumbnailImage image = new WpfVideoThumbnailDecoder().Decode(encoded);

        Assert.Equal(1024, image.Width);
        Assert.Equal(1024, image.Height);
        Assert.Equal(VideoThumbnailImage.MaximumDecodedBytes, image.Bgra32.Length);
    });

    [Fact]
    public Task Decode_RejectsRealOversizedPngBeforeCodecDecode() => RunOnSta(() =>
    {
        byte[] encoded = Encode(new PngBitmapEncoder(), 1025, 1, new byte[1025 * 4]);
        Assert.Throws<InvalidDataException>(() => new WpfVideoThumbnailDecoder().Decode(encoded));
    });

    [Fact]
    public Task Decode_RejectsPngAnimationMarkerEvenWhenDefaultFrameIsValid() => RunOnSta(() =>
    {
        byte[] encoded = Encode(new PngBitmapEncoder(), 1, 1, new byte[4]);
        // Put acTL after IHDR. A built-in PNG codec may ignore APNG; preflight must reject it.
        byte[] animation = [0, 0, 0, 8, (byte)'a', (byte)'c', (byte)'T', (byte)'L',
            0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0];
        encoded = [.. encoded[..33], .. animation, .. encoded[33..]];

        Assert.Throws<InvalidDataException>(() => new WpfVideoThumbnailDecoder().Decode(encoded));
    });

    [Fact]
    public Task Decode_RejectsEmbeddedJpegThumbnailAndPngMetadataBeforeNativeCodec() => RunOnSta(() =>
    {
        byte[] jpeg = Encode(new JpegBitmapEncoder(), 1, 1, [0, 0, 0, 255]);
        // EXIF may embed a differently sized thumbnail. Reject its container without decoding either raster.
        byte[] embedded = [0xff, 0xe1, 0, 18, .. "Exif\0\0"u8, 0xff, 0xc0, 0, 8, 8, 0xff, 0xff, 0xff, 0xff, 0];
        byte[] jpegWithMetadata = [.. jpeg[..2], .. embedded, .. jpeg[2..]];
        Assert.Throws<InvalidDataException>(() => new WpfVideoThumbnailDecoder().Decode(jpegWithMetadata));
        byte[] png = Encode(new PngBitmapEncoder(), 1, 1, new byte[4]);
        byte[] metadata = [0, 0, 0, 4, (byte)'i', (byte)'C', (byte)'C', (byte)'P', 0, 0, 0, 0, 0, 0, 0, 0];
        byte[] pngWithMetadata = [.. png[..33], .. metadata, .. png[33..]];
        Assert.Throws<InvalidDataException>(() => new WpfVideoThumbnailDecoder().Decode(pngWithMetadata));
    });

    [Fact]
    public Task Decode_RejectsSingleAndMultiFrameGifRatherThanUsingRegisteredCodec() => RunOnSta(() =>
    {
        byte[] pixels = [0, 0, 0, 255];
        byte[] oneFrame = Encode(new GifBitmapEncoder(), 1, 1, pixels);
        Assert.Throws<InvalidDataException>(() => new WpfVideoThumbnailDecoder().Decode(oneFrame));

        GifBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4)));
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, pixels, 4)));
        using MemoryStream stream = new();
        encoder.Save(stream);
        Assert.Throws<InvalidDataException>(() => new WpfVideoThumbnailDecoder().Decode(stream.ToArray()));
    });

    [Fact]
    public void Decode_RejectsEmptyMalformedAndEncodedOversize()
    {
        WpfVideoThumbnailDecoder decoder = new();
        Assert.Throws<InvalidDataException>(() => decoder.Decode(ReadOnlyMemory<byte>.Empty));
        Assert.Throws<InvalidDataException>(() => decoder.Decode("private title or URL"u8.ToArray()));
        Assert.Throws<InvalidDataException>(() => decoder.Decode(new byte[VideoThumbnailImage.MaximumEncodedBytes + 1]));
    }

    [Fact]
    public void Decode_ValidBoundedWebPHeaderUsesUnsupportedCodecFallback()
    {
        // Synthetic lossless header: 1x1, static, one bitstream, correctly padded RIFF container.
        byte[] encoded = [.. "RIFF"u8, 18, 0, 0, 0, .. "WEBPVP8L"u8, 5, 0, 0, 0, 0x2f, 0, 0, 0, 0, 0];
        Assert.Throws<NotSupportedException>(() => new WpfVideoThumbnailDecoder().Decode(encoded));

        BinaryPrimitives.WriteUInt32LittleEndian(encoded.AsSpan(21, 4), 1024);
        Assert.Throws<InvalidDataException>(() => new WpfVideoThumbnailDecoder().Decode(encoded));
    }

    private static byte[] Encode(BitmapEncoder encoder, int width, int height, byte[] pixels)
    {
        BitmapSource bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using MemoryStream stream = new();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static Task RunOnSta(Action action)
    {
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception exception)
            {
                completion.SetException(exception);
            }
        })
        { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
