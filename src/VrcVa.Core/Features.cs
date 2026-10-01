namespace VrcVa.Core;

/// <summary>
/// A stable identifier used to select a feature independently of its display name.
/// </summary>
public readonly record struct FeatureId
{
    public FeatureId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (value.Length > 64 || value.Any(character =>
                character is not (>= 'a' and <= 'z')
                    and not (>= '0' and <= '9')
                    and not '.'
                    and not '_'
                    and not '-'))
        {
            throw new ArgumentException(
                "A feature ID must be at most 64 lowercase ASCII characters using a-z, 0-9, '.', '_', or '-'.",
                nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value ?? string.Empty;
}

public enum FeatureInputKind
{
    CapturedFrame,
    Text,
}

/// <summary>
/// Transmission categories a feature can use. Metadata describes a boundary;
/// it does not enable a provider or grant consent to transmit data.
/// </summary>
[Flags]
public enum FeatureDataBoundary
{
    LocalOnly = 0,
    ExtractedTextMayLeaveDevice = 1,
    CapturedImageMayLeaveDevice = 2,
    VoiceAudioToOpenAi = 4,
    InputTextToOpenAi = 8,
    SearchTextToYouTube = 16,
}

public sealed record FeatureDescriptor
{
    public FeatureDescriptor(
        FeatureId id,
        string displayName,
        FeatureInputKind inputKind,
        FeatureDataBoundary dataBoundary)
    {
        if (id.IsEmpty)
        {
            throw new ArgumentException("A feature ID is required.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (!Enum.IsDefined(inputKind))
        {
            throw new ArgumentOutOfRangeException(nameof(inputKind));
        }

        const FeatureDataBoundary supportedBoundaries =
            FeatureDataBoundary.ExtractedTextMayLeaveDevice
            | FeatureDataBoundary.CapturedImageMayLeaveDevice
            | FeatureDataBoundary.VoiceAudioToOpenAi
            | FeatureDataBoundary.InputTextToOpenAi
            | FeatureDataBoundary.SearchTextToYouTube;
        if ((dataBoundary & ~supportedBoundaries) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(dataBoundary));
        }

        Id = id;
        DisplayName = displayName;
        InputKind = inputKind;
        DataBoundary = dataBoundary;
    }

    public FeatureId Id { get; }

    public string DisplayName { get; }

    public FeatureInputKind InputKind { get; }

    public FeatureDataBoundary DataBoundary { get; }
}

public sealed record FeatureEntry
{
    public FeatureEntry(FeatureDescriptor descriptor, IAnalyzer analyzer)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(analyzer);
        ValidateInputKind(descriptor, FeatureInputKind.CapturedFrame);

        Descriptor = descriptor;
        Analyzer = analyzer;
    }

    public FeatureEntry(FeatureDescriptor descriptor, ITextFeatureHandler textHandler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(textHandler);
        ValidateInputKind(descriptor, FeatureInputKind.Text);

        Descriptor = descriptor;
        TextHandler = textHandler;
    }

    public FeatureDescriptor Descriptor { get; }

    public IAnalyzer? Analyzer { get; }

    public ITextFeatureHandler? TextHandler { get; }

    private static void ValidateInputKind(FeatureDescriptor descriptor, FeatureInputKind expected)
    {
        if (descriptor.InputKind != expected)
        {
            throw new ArgumentException(
                "The feature descriptor input kind must match its handler.",
                nameof(descriptor));
        }
    }
}

public static class FeatureIds
{
    public static FeatureId Translation { get; } = new("translation");

    public static FeatureId Summarization { get; } = new("summarization");

    public static FeatureId DirectVideoSearch { get; } = new("video-search.direct");
}

public static class BuiltInFeatures
{
    // Contract only: the Windows runtime does not register search until K5.
    public static FeatureDescriptor DirectVideoSearch { get; } = new(
        FeatureIds.DirectVideoSearch,
        "そのまま検索",
        FeatureInputKind.Text,
        FeatureDataBoundary.SearchTextToYouTube);

    public static FeatureDescriptor Translation { get; } = new(
        FeatureIds.Translation,
        "翻訳",
        FeatureInputKind.CapturedFrame,
        FeatureDataBoundary.ExtractedTextMayLeaveDevice);

    public static FeatureDescriptor Summarization { get; } = new(
        FeatureIds.Summarization,
        "要約",
        FeatureInputKind.CapturedFrame,
        FeatureDataBoundary.ExtractedTextMayLeaveDevice);
}

/// <summary>
/// The compile-time registry for available features and their typed handlers.
/// </summary>
public sealed class FeatureCatalog
{
    private readonly IReadOnlyDictionary<FeatureId, FeatureEntry> _entriesById;

    public FeatureCatalog(params FeatureEntry[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        Dictionary<FeatureId, FeatureEntry> entriesById = [];
        foreach (FeatureEntry entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);

            if (!entriesById.TryAdd(entry.Descriptor.Id, entry))
            {
                throw new ArgumentException(
                    $"Feature ID '{entry.Descriptor.Id}' is registered more than once.",
                    nameof(entries));
            }
        }

        _entriesById = entriesById;
        Entries = Array.AsReadOnly(entries.ToArray());
    }

    public IReadOnlyList<FeatureEntry> Entries { get; }

    public FeatureEntry Resolve(FeatureId id)
    {
        if (_entriesById.TryGetValue(id, out FeatureEntry? entry))
        {
            return entry;
        }

        throw new ScanException(
            ScanFailureCode.UnknownFeature,
            ScanStage.Trigger,
            "指定された機能は利用できません。機能一覧を開き直してください。");
    }

    public bool TryResolve(FeatureId id, out FeatureEntry? entry) =>
        _entriesById.TryGetValue(id, out entry);
}
