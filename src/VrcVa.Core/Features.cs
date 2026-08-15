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
}

/// <summary>
/// The widest data boundary a feature can use. A concrete provider may remain more restrictive.
/// </summary>
public enum FeatureDataBoundary
{
    LocalOnly,
    ExtractedTextMayLeaveDevice,
    CapturedImageMayLeaveDevice,
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

        Descriptor = descriptor;
        Analyzer = analyzer;
    }

    public FeatureDescriptor Descriptor { get; }

    public IAnalyzer Analyzer { get; }
}

public static class FeatureIds
{
    public static FeatureId Translation { get; } = new("translation");

    public static FeatureId Summarization { get; } = new("summarization");
}

public static class BuiltInFeatures
{
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
/// The compile-time registry for available features and their analyzers.
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
