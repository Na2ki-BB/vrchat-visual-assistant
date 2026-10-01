using System.Buffers.Binary;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

public enum VideoThumbnailFormat
{
    Jpeg,
    Png,
    WebP,
}

/// <summary>Bounded encoded dimensions, inspected without invoking an image codec or allocating pixels.</summary>
public readonly record struct VideoThumbnailHeader(int Width, int Height, VideoThumbnailFormat Format)
{
    public static VideoThumbnailHeader Read(ReadOnlySpan<byte> encodedImage)
    {
        if (encodedImage.IsEmpty || encodedImage.Length > VideoThumbnailImage.MaximumEncodedBytes)
        {
            throw InvalidImage();
        }

        if (encodedImage.Length >= 8 && encodedImage[0] == 0x89
            && encodedImage.Slice(1, 7).SequenceEqual("PNG\r\n\u001a\n"u8))
        {
            return ReadPng(encodedImage);
        }
        if (encodedImage.Length >= 2 && encodedImage[0] == 0xff && encodedImage[1] == 0xd8)
        {
            return ReadJpeg(encodedImage);
        }
        if (encodedImage.Length >= 12 && encodedImage[..4].SequenceEqual("RIFF"u8)
            && encodedImage.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return ReadWebP(encodedImage);
        }

        throw InvalidImage();
    }

    private static VideoThumbnailHeader ReadPng(ReadOnlySpan<byte> bytes)
    {
        int offset = 8;
        VideoThumbnailHeader? header = null;
        bool hasImageData = false;
        while (offset <= bytes.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(offset, 4));
            if (length > bytes.Length - offset - 12)
            {
                throw InvalidImage();
            }
            ReadOnlySpan<byte> kind = bytes.Slice(offset + 4, 4);
            ReadOnlySpan<byte> payload = bytes.Slice(offset + 8, (int)length);
            // WIC eagerly reads metadata and embedded thumbnails. Pass only bounded raster/display chunks.
            if (!IsAllowedPngChunk(kind, length)) { throw InvalidImage(); }
            if (kind.SequenceEqual("IHDR"u8))
            {
                if (offset != 8 || header is not null || length != 13 || !IsSupportedPngHeader(payload))
                {
                    throw InvalidImage();
                }
                header = Create(BinaryPrimitives.ReadUInt32BigEndian(payload[..4]),
                    BinaryPrimitives.ReadUInt32BigEndian(payload.Slice(4, 4)), VideoThumbnailFormat.Png);
            }
            else if (header is null)
            {
                throw InvalidImage();
            }
            else if (kind.SequenceEqual("IDAT"u8))
            {
                hasImageData = true;
            }
            else if (kind.SequenceEqual("IEND"u8))
            {
                if (length != 0 || !hasImageData || offset + 12 != bytes.Length)
                {
                    throw InvalidImage();
                }
                return header.Value;
            }
            offset += (int)length + 12;
        }
        throw InvalidImage();
    }

    private static VideoThumbnailHeader ReadJpeg(ReadOnlySpan<byte> bytes)
    {
        int offset = 2;
        VideoThumbnailHeader? header = null;
        bool inScan = false;
        bool hasScan = false;
        while (offset < bytes.Length)
        {
            if (inScan)
            {
                while (offset < bytes.Length && bytes[offset] != 0xff) { offset++; }
            }
            if (offset >= bytes.Length || bytes[offset++] != 0xff) { throw InvalidImage(); }
            while (offset < bytes.Length && bytes[offset] == 0xff) { offset++; }
            if (offset >= bytes.Length) { throw InvalidImage(); }
            byte marker = bytes[offset++];
            if (inScan && (marker == 0 || marker is >= 0xd0 and <= 0xd7)) { continue; }
            inScan = false;
            if (marker == 0xd9)
            {
                if (header is null || !hasScan || offset != bytes.Length) { throw InvalidImage(); }
                return header.Value;
            }
            // Nested images, dynamic height, and hierarchical frames cannot satisfy pre-decode bounds.
            if (marker is 0 or 0xd8 or 0xdc or 0xde or 0xdf or >= 0xd0 and <= 0xd7)
            {
                throw InvalidImage();
            }
            if (marker == 1) { continue; }
            if (offset > bytes.Length - 2) { throw InvalidImage(); }
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset, 2));
            if (length < 2 || length > bytes.Length - offset) { throw InvalidImage(); }
            ReadOnlySpan<byte> payload = bytes.Slice(offset + 2, length - 2);
            if (marker is >= 0xc0 and <= 0xcf and not (0xc4 or 0xc8 or 0xcc))
            {
                if (header is not null || marker is not (0xc0 or 0xc1 or 0xc2)
                    || payload.Length < 6 || payload[0] != 8 || payload[5] is not (1 or 3 or 4)
                    || payload.Length != 6 + 3 * payload[5])
                {
                    throw InvalidImage();
                }
                header = Create(BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(3, 2)),
                    BinaryPrimitives.ReadUInt16BigEndian(payload.Slice(1, 2)), VideoThumbnailFormat.Jpeg);
            }
            // EXIF/JFXX/Photoshop can hide another raster, and ICC metadata can expand independently.
            if (marker is >= 0xe0 and <= 0xef && !IsAllowedJpegApplication(marker, payload))
            {
                throw InvalidImage();
            }
            if (marker == 0xda)
            {
                if (header is null || payload.Length < 4 || payload[0] is < 1 or > 4
                    || payload.Length != 4 + 2 * payload[0]) { throw InvalidImage(); }
                hasScan = true;
                inScan = true;
            }
            offset += length;
        }
        throw InvalidImage();
    }

    private static VideoThumbnailHeader ReadWebP(ReadOnlySpan<byte> bytes)
    {
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4)) != bytes.Length - 8)
        {
            throw InvalidImage();
        }
        int offset = 12;
        VideoThumbnailHeader? canvas = null;
        VideoThumbnailHeader? image = null;
        while (offset <= bytes.Length - 8)
        {
            ReadOnlySpan<byte> kind = bytes.Slice(offset, 4);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            long paddedLength = (long)length + (length & 1);
            if (paddedLength > bytes.Length - offset - 8) { throw InvalidImage(); }
            ReadOnlySpan<byte> payload = bytes.Slice(offset + 8, (int)length);
            if ((length & 1) != 0 && bytes[offset + 8 + (int)length] != 0) { throw InvalidImage(); }
            if (kind.SequenceEqual("ANIM"u8) || kind.SequenceEqual("ANMF"u8)) { throw InvalidImage(); }
            if (kind.SequenceEqual("VP8X"u8))
            {
                if (offset != 12 || canvas is not null || length != 10 || (payload[0] & 0x02) != 0)
                {
                    throw InvalidImage();
                }
                canvas = Create(ReadUInt24(payload.Slice(4, 3)) + 1,
                    ReadUInt24(payload.Slice(7, 3)) + 1, VideoThumbnailFormat.WebP);
            }
            else if (kind.SequenceEqual("VP8 "u8))
            {
                if (image is not null || length < 10 || (payload[0] & 1) != 0
                    || payload[3] != 0x9d || payload[4] != 0x01 || payload[5] != 0x2a)
                {
                    throw InvalidImage();
                }
                image = Create((uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & 0x3fff),
                    (uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & 0x3fff), VideoThumbnailFormat.WebP);
            }
            else if (kind.SequenceEqual("VP8L"u8))
            {
                if (image is not null || length < 5 || payload[0] != 0x2f || (payload[4] & 0xe0) != 0)
                {
                    throw InvalidImage();
                }
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
                image = Create((bits & 0x3fff) + 1, ((bits >> 14) & 0x3fff) + 1, VideoThumbnailFormat.WebP);
            }
            offset += 8 + (int)paddedLength;
        }
        if (offset != bytes.Length || image is null || (canvas is not null && canvas != image))
        {
            throw InvalidImage();
        }
        return image.Value;
    }

    private static uint ReadUInt24(ReadOnlySpan<byte> bytes) =>
        bytes[0] | ((uint)bytes[1] << 8) | ((uint)bytes[2] << 16);

    private static bool IsAllowedPngChunk(ReadOnlySpan<byte> kind, uint length) =>
        kind.SequenceEqual("IHDR"u8) || kind.SequenceEqual("IDAT"u8) || kind.SequenceEqual("IEND"u8)
        || (kind.SequenceEqual("PLTE"u8) && length is >= 3 and <= 768 && length % 3 == 0)
        || (kind.SequenceEqual("tRNS"u8) && length is > 0 and <= 256)
        || (kind.SequenceEqual("pHYs"u8) && length == 9)
        || (kind.SequenceEqual("sRGB"u8) && length == 1)
        || (kind.SequenceEqual("gAMA"u8) && length == 4)
        || (kind.SequenceEqual("cHRM"u8) && length == 32)
        || (kind.SequenceEqual("bKGD"u8) && length is 1 or 2 or 6)
        || (kind.SequenceEqual("sBIT"u8) && length is >= 1 and <= 4);

    private static bool IsAllowedJpegApplication(byte marker, ReadOnlySpan<byte> payload) =>
        (marker == 0xe0 && payload.Length == 14 && payload.StartsWith("JFIF\0"u8)
            && payload[12] == 0 && payload[13] == 0)
        || (marker == 0xee && payload.Length == 12 && payload.StartsWith("Adobe"u8));

    private static bool IsSupportedPngHeader(ReadOnlySpan<byte> payload)
    {
        // WPF caches native pixels before conversion. Reject 16-bit formats that could use >4 MiB.
        bool supportedDepth = payload[9] switch
        {
            0 or 3 => payload[8] is 1 or 2 or 4 or 8,
            2 or 4 or 6 => payload[8] == 8,
            _ => false,
        };
        return supportedDepth && payload[10] == 0 && payload[11] == 0 && payload[12] <= 1;
    }

    private static VideoThumbnailHeader Create(uint width, uint height, VideoThumbnailFormat format)
    {
        if (width > VideoThumbnailImage.MaximumDimension || height > VideoThumbnailImage.MaximumDimension
            || !VideoThumbnailImage.AreDimensionsAllowed((int)width, (int)height))
        {
            throw InvalidImage();
        }
        return new((int)width, (int)height, format);
    }

    private static InvalidDataException InvalidImage() => new("The thumbnail image is invalid or exceeds its limits.");
}
