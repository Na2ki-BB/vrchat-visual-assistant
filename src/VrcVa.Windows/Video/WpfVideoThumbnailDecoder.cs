using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VrcVa.Core;
using VrcVa.Infrastructure;

namespace VrcVa.Windows.Video;

/// <summary>Static, memory-only thumbnail decode using the Windows JPEG/PNG codec path.</summary>
public sealed class WpfVideoThumbnailDecoder : IVideoThumbnailDecoder
{
    public VideoThumbnailImage Decode(ReadOnlyMemory<byte> encodedImage)
    {
        VideoThumbnailHeader header = VideoThumbnailHeader.Read(encodedImage.Span);
        // Snapshot bounded encoded bytes so callers cannot change a header after validation.
        byte[] bytes = encodedImage.ToArray();
        header = VideoThumbnailHeader.Read(bytes);
        if (header.Format == VideoThumbnailFormat.WebP)
        {
            // A codec is not bundled or installed. The provider preserves the candidate with a placeholder.
            throw new NotSupportedException("WebP thumbnail decoding is unavailable.");
        }

        using MemoryStream stream = new(bytes, writable: false);
        const BitmapCreateOptions options = BitmapCreateOptions.IgnoreColorProfile;
        // WPF requests the matching container with Microsoft as the preferred WIC vendor.
        // It does not enforce an exclusive decoder CLSID; no WebP codec lookup is performed.
        BitmapDecoder decoder = header.Format == VideoThumbnailFormat.Png
            ? new PngBitmapDecoder(stream, options, BitmapCacheOption.OnLoad)
            : new JpegBitmapDecoder(stream, options, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count != 1)
        {
            throw new InvalidDataException("The thumbnail must have one frame.");
        }
        BitmapFrame frame = decoder.Frames[0];
        if (frame.PixelWidth != header.Width || frame.PixelHeight != header.Height
            || !VideoThumbnailImage.AreDimensionsAllowed(frame.PixelWidth, frame.PixelHeight))
        {
            throw new InvalidDataException("The thumbnail dimensions are invalid.");
        }

        BitmapSource source = frame.Format == PixelFormats.Bgra32
            ? frame
            : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        int stride = checked(header.Width * 4);
        byte[] pixels = new byte[checked(stride * header.Height)];
        source.CopyPixels(pixels, stride, 0);
        return new VideoThumbnailImage(header.Width, header.Height, pixels);
    }
}
