namespace VrcVa.Core;

/// <summary>One memory-only, bounded BGRA32 thumbnail. Missing or failed images leave the candidate intact.</summary>
public sealed class VideoThumbnailImage
{
    public const int MaximumEncodedBytes = 512 * 1024;
    public const int MaximumDimension = 1024;
    public const int MaximumPixels = 1024 * 1024;
    public const int MaximumDecodedBytes = 4 * MaximumPixels;
    public static readonly TimeSpan DownloadTimeout = TimeSpan.FromSeconds(5);
    private readonly byte[] _pixels;

    public VideoThumbnailImage(int width, int height, ReadOnlySpan<byte> bgra32)
    {
        if (!AreDimensionsAllowed(width, height) || bgra32.Length != (long)width * height * 4)
        {
            throw new ArgumentException("The thumbnail dimensions or pixel buffer are invalid.");
        }
        Width = width;
        Height = height;
        _pixels = bgra32.ToArray();
    }

    public int Width { get; }
    public int Height { get; }
    public ReadOnlyMemory<byte> Bgra32 => _pixels;

    public static bool AreDimensionsAllowed(int width, int height) =>
        width > 0 && height > 0 && width <= MaximumDimension && height <= MaximumDimension
        && (long)width * height <= MaximumPixels && (long)width * height * 4 <= MaximumDecodedBytes;
}

public enum VideoThumbnailStatus
{
    Available,
    Missing,
    RejectedUrl,
    RedirectRejected,
    TooLarge,
    InvalidImage,
    Unavailable,
    TimedOut,
    Cancelled,
}

public sealed record VideoThumbnailResult(VideoThumbnailStatus Status, VideoThumbnailImage? Image = null)
{
    public bool UsePlaceholder => Status != VideoThumbnailStatus.Available || Image is null;
}

public interface IVideoThumbnailProvider
{
    Task<VideoThumbnailResult> LoadAsync(Uri? thumbnailUrl, CancellationToken cancellationToken);
}

/// <summary>Decoders must inspect encoded dimensions before allocating pixels, and reject animation.</summary>
public interface IVideoThumbnailDecoder
{
    VideoThumbnailImage Decode(ReadOnlyMemory<byte> encodedImage);
}
