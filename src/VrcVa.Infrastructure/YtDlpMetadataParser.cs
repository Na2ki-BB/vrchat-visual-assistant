using System.Text;
using System.Text.Json;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

internal static class YtDlpMetadataParser
{
    internal static VideoSearchBatch Parse(ReadOnlyMemory<byte> json, bool hasProviderWarning = false)
    {
        if (json.Length > YtDlpVideoSearchProvider.MaximumStdoutBytes) { throw Invalid(); }
        try
        {
            // Validate bytes in every field, including ignored provider metadata, before DOM access.
            _ = new UTF8Encoding(false, true).GetCharCount(json.Span);
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || HasDuplicateFields(root)
                || !root.TryGetProperty("entries", out JsonElement entries)
                || entries.ValueKind != JsonValueKind.Array
                || entries.GetArrayLength() > VideoSearchRequest.MaximumCandidates)
            {
                throw Invalid();
            }

            List<VideoMetadata> videos = [];
            HashSet<string> ids = new(StringComparer.Ordinal);
            bool partial = hasProviderWarning;
            foreach (JsonElement entry in entries.EnumerateArray())
            {
                if (!TryParseEntry(entry, out VideoMetadata? video, out bool thumbnailDiscarded)
                    || !ids.Add(video!.VideoId))
                {
                    partial = true;
                    continue;
                }
                partial |= thumbnailDiscarded;
                videos.Add(video!);
            }
            if (entries.GetArrayLength() > 0 && videos.Count == 0) { throw Invalid(); }
            return new VideoSearchBatch(videos, partial);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or DecoderFallbackException) { throw Invalid(); }
    }

    private static bool TryParseEntry(JsonElement entry, out VideoMetadata? video, out bool thumbnailDiscarded)
    {
        video = null;
        thumbnailDiscarded = false;
        try
        {
            if (entry.ValueKind != JsonValueKind.Object || HasDuplicateFields(entry)
                || !ReadString(entry, "id", required: true, out string? id)
                || !ReadString(entry, "title", required: true, out string? title)
                || !ReadString(entry, "url", required: false, out string? url)
                || !ReadString(entry, "webpage_url", required: false, out string? webpageUrl))
            {
                return false;
            }
            // Validate every supplied URL, even if another field is valid. Never use a URL to repair a bad ID.
            video = new VideoMetadata(id!, title!, url);
            if (webpageUrl is not null) { _ = new VideoMetadata(id!, title!, webpageUrl); }
            Uri? thumbnail;
            try { thumbnail = ReadThumbnail(entry, id!, title!, out thumbnailDiscarded); }
            catch (InvalidOperationException) { thumbnail = null; thumbnailDiscarded = true; }
            if (thumbnail is not null) { video = new VideoMetadata(id!, title!, url, thumbnail); }
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return false; }
    }

    private static Uri? ReadThumbnail(JsonElement entry, string id, string title, out bool discarded)
    {
        discarded = false;
        if (entry.TryGetProperty("thumbnail", out JsonElement single) && single.ValueKind != JsonValueKind.Null)
        {
            Uri? value = ValidateThumbnail(single, id, title);
            if (value is not null) { return value; }
            discarded = true;
        }
        if (!entry.TryGetProperty("thumbnails", out JsonElement many) || many.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        if (many.ValueKind != JsonValueKind.Array || many.GetArrayLength() > 32)
        {
            discarded = true;
            return null;
        }
        // Flat extraction may omit thumbnails. Pick the first permitted image URL, with no network access here.
        foreach (JsonElement thumbnail in many.EnumerateArray())
        {
            if (thumbnail.ValueKind == JsonValueKind.Object && !HasDuplicateFields(thumbnail)
                && thumbnail.TryGetProperty("url", out JsonElement url))
            {
                Uri? value = ValidateThumbnail(url, id, title);
                if (value is not null) { return value; }
            }
            discarded = true;
        }
        return null;
    }

    private static Uri? ValidateThumbnail(JsonElement value, string id, string title)
    {
        try
        {
            if (value.ValueKind != JsonValueKind.String
                || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out Uri? uri)) { return null; }
            return new VideoMetadata(id, title, thumbnailUrl: uri).ThumbnailUrl;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { return null; }
    }

    private static bool ReadString(JsonElement entry, string field, bool required, out string? value)
    {
        value = null;
        if (!entry.TryGetProperty(field, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return !required;
        }
        if (element.ValueKind != JsonValueKind.String) { return false; }
        value = element.GetString();
        return true;
    }

    private static bool HasDuplicateFields(JsonElement element)
    {
        HashSet<string> fields = new(StringComparer.Ordinal);
        return element.EnumerateObject().Any(property => !fields.Add(property.Name));
    }

    private static ScanException Invalid() => YtDlpVideoSearchProvider.Failure(
        ScanFailureCode.VideoSearchInvalidMetadata, "動画検索の応答を検証できませんでした。やり直してください。");
}
