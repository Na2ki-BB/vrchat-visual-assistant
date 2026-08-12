using VrcVa.Core;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace VrcVa.Windows.Ocr;

internal sealed class WindowsOcrRegionSource : IOcrRegionSource
{
    private const int RegionCount = 3;

    public async Task<IReadOnlyList<CapturedFrame>> CreateRegionsAsync(
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        using InMemoryRandomAccessStream sourceStream = await CreateInputStreamAsync(
            frame.EncodedImage,
            cancellationToken);
        BitmapDecoder decoder = await BitmapDecoder
            .CreateAsync(sourceStream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);

        uint sourceWidth = decoder.PixelWidth;
        uint sourceHeight = decoder.PixelHeight;
        uint regionHeight = Math.Max(1u, (sourceHeight + 1) / 2);
        uint maximumStart = sourceHeight - regionHeight;
        List<CapturedFrame> regions = [];

        try
        {
            for (int index = 0; index < RegionCount; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint startY = index == RegionCount - 1
                    ? maximumStart
                    : checked((uint)Math.Round(
                        maximumStart * index / (double)(RegionCount - 1),
                        MidpointRounding.AwayFromZero));
                BitmapTransform transform = WindowsOcrBitmapTransform.Create(
                    sourceWidth,
                    sourceHeight,
                    new BitmapBounds
                    {
                        X = 0,
                        Y = startY,
                        Width = sourceWidth,
                        Height = regionHeight,
                    },
                    OcrBitmapScaleMode.Unscaled);
                using SoftwareBitmap bitmap = await decoder
                    .GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied,
                        transform,
                        ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                byte[] encoded = await EncodePngAsync(bitmap, cancellationToken);
                regions.Add(new CapturedFrame(
                    encoded,
                    checked((int)transform.Bounds.Width),
                    checked((int)transform.Bounds.Height),
                    "image/png",
                    $"{frame.SourceKind}:ocr-band-{index + 1}",
                    frame.OcrScaleReferenceWidth,
                    frame.OcrScaleReferenceHeight));
            }

            return regions;
        }
        catch
        {
            foreach (CapturedFrame region in regions)
            {
                region.Dispose();
            }

            throw;
        }
    }

    private static async Task<InMemoryRandomAccessStream> CreateInputStreamAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        InMemoryRandomAccessStream stream = new();
        try
        {
            using DataWriter writer = new(stream.GetOutputStreamAt(0));
            writer.WriteBytes(bytes.ToArray());
            await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
            writer.DetachStream();
            stream.Seek(0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static async Task<byte[]> EncodePngAsync(
        SoftwareBitmap bitmap,
        CancellationToken cancellationToken)
    {
        using InMemoryRandomAccessStream stream = new();
        BitmapEncoder encoder = await BitmapEncoder
            .CreateAsync(BitmapEncoder.PngEncoderId, stream)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);

        if (stream.Size > uint.MaxValue)
        {
            throw new InvalidOperationException("OCR region image is too large.");
        }

        stream.Seek(0);
        byte[] encoded = new byte[checked((int)stream.Size)];
        using DataReader reader = new(stream.GetInputStreamAt(0));
        await reader.LoadAsync(checked((uint)stream.Size))
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        reader.ReadBytes(encoded);
        return encoded;
    }
}
