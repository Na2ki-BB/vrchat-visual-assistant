using System.Security.Cryptography;

namespace VrcVa.Core;

public enum ScanStage
{
    Trigger,
    Capture,
    Ocr,
    Translation,
    Rendering,
    Completed,
}

public enum ScanFailureCode
{
    Busy,
    UnknownFeature,
    CaptureTargetNotFound,
    CaptureUnavailable,
    OcrUnavailable,
    NoTextDetected,
    TranslationNotConfigured,
    TranslationAuthenticationFailed,
    TranslationInputTooLarge,
    TranslationUsageLimitReached,
    TranslationRateLimited,
    TranslationTimedOut,
    TranslationFailed,
    Cancelled,
    Unexpected,
}

public sealed record ScanRequest(
    Guid CorrelationId,
    DateTimeOffset RequestedAt,
    string TriggerName)
{
    public FeatureId FeatureId { get; init; } = FeatureIds.Translation;

    public static ScanRequest Create(string triggerName) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, triggerName);

    public static ScanRequest Create(string triggerName, FeatureId featureId) =>
        new(Guid.NewGuid(), DateTimeOffset.UtcNow, triggerName)
        {
            FeatureId = featureId,
        };
}

public sealed class CapturedFrame : IDisposable
{
    private byte[]? _encodedImage;

    public CapturedFrame(
        byte[] encodedImage,
        int width,
        int height,
        string mediaType,
        string sourceKind,
        int? ocrScaleReferenceWidth = null,
        int? ocrScaleReferenceHeight = null)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);

        if (encodedImage.Length == 0)
        {
            throw new ArgumentException("The captured frame cannot be empty.", nameof(encodedImage));
        }

        if (width <= 0 || height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frame dimensions must be positive.");
        }

        if (ocrScaleReferenceWidth is <= 0 || ocrScaleReferenceHeight is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ocrScaleReferenceWidth),
                "OCR scale-reference dimensions must be positive.");
        }

        if (ocrScaleReferenceWidth.HasValue != ocrScaleReferenceHeight.HasValue)
        {
            throw new ArgumentException(
                "Both OCR scale-reference dimensions must be supplied together.");
        }

        _encodedImage = encodedImage;
        Width = width;
        Height = height;
        MediaType = mediaType;
        SourceKind = sourceKind;
        OcrScaleReferenceWidth = ocrScaleReferenceWidth ?? width;
        OcrScaleReferenceHeight = ocrScaleReferenceHeight ?? height;
    }

    public int Width { get; }

    public int Height { get; }

    public string MediaType { get; }

    public string SourceKind { get; }

    public int OcrScaleReferenceWidth { get; }

    public int OcrScaleReferenceHeight { get; }

    public ReadOnlyMemory<byte> EncodedImage =>
        _encodedImage ?? throw new ObjectDisposedException(nameof(CapturedFrame));

    public void Dispose()
    {
        byte[]? encodedImage = Interlocked.Exchange(ref _encodedImage, null);
        if (encodedImage is null)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(encodedImage);
    }
}

public sealed record OcrOutput(
    string Text,
    string RecognizerLanguage,
    string? Warning = null);

public sealed record TranslationOutput(
    string Text,
    string Provider,
    string Model);

public enum ResultSectionRole
{
    Supporting,
    Primary,
}

public sealed record ResultSection
{
    public ResultSection(
        string id,
        string title,
        string text,
        ResultSectionRole role = ResultSectionRole.Supporting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(text);

        Id = id;
        Title = title;
        Text = text;
        Role = role;
    }

    public string Id { get; }

    public string Title { get; }

    public string Text { get; }

    public ResultSectionRole Role { get; }

    public bool IsPrimary => Role == ResultSectionRole.Primary;
}

public sealed record TextModelMetadata
{
    public TextModelMetadata(string ocrLanguage, string provider, string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ocrLanguage);
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        OcrLanguage = ocrLanguage;
        Provider = provider;
        Model = model;
    }

    public string OcrLanguage { get; }

    public string Provider { get; }

    public string Model { get; }
}

public static class TranslationResultSectionIds
{
    public const string SourceText = "source-text";

    public const string JapaneseText = "japanese-text";
}

/// <summary>
/// The feature-neutral result boundary consumed by the pipeline and renderers.
/// </summary>
public sealed record FeatureResult
{
    public FeatureResult(
        FeatureId featureId,
        IEnumerable<ResultSection> sections,
        TextModelMetadata? textModelMetadata = null)
    {
        if (featureId.IsEmpty)
        {
            throw new ArgumentException("A feature ID is required.", nameof(featureId));
        }

        ArgumentNullException.ThrowIfNull(sections);

        ResultSection[] orderedSections = sections.ToArray();
        if (orderedSections.Length == 0)
        {
            throw new ArgumentException("At least one result section is required.", nameof(sections));
        }

        if (orderedSections.Any(section => section is null))
        {
            throw new ArgumentException("Result sections cannot contain null.", nameof(sections));
        }

        string? duplicateId = orderedSections
            .GroupBy(section => section.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1)
            ?.Key;
        if (duplicateId is not null)
        {
            throw new ArgumentException(
                $"Result section ID '{duplicateId}' is duplicated.",
                nameof(sections));
        }

        ResultSection[] primarySections = orderedSections
            .Where(section => section.IsPrimary)
            .ToArray();
        if (primarySections.Length != 1)
        {
            throw new ArgumentException(
                "A feature result must contain exactly one primary section.",
                nameof(sections));
        }

        FeatureId = featureId;
        Sections = Array.AsReadOnly(orderedSections);
        PrimarySection = primarySections[0];
        TextModelMetadata = textModelMetadata;
    }

    public FeatureId FeatureId { get; }

    public IReadOnlyList<ResultSection> Sections { get; }

    public ResultSection PrimarySection { get; }

    public TextModelMetadata? TextModelMetadata { get; }

    public TimeSpan OcrDuration { get; init; }

    public TimeSpan TranslationDuration { get; init; }

    public string? Warning { get; init; }

    public string CaptureSourceKind { get; init; } = "unknown";

    // Compatibility projections for existing translation renderers.
    public string SourceText => FindSectionText(TranslationResultSectionIds.SourceText);

    public string JapaneseText
    {
        get
        {
            string translatedText = FindSectionText(TranslationResultSectionIds.JapaneseText);
            if (translatedText.Length > 0 || FeatureId == FeatureIds.Translation)
            {
                return translatedText;
            }

            return PrimarySection.Text;
        }
    }

    public string OcrLanguage => TextModelMetadata?.OcrLanguage ?? string.Empty;

    public string TranslationProvider => TextModelMetadata?.Provider ?? string.Empty;

    public string TranslationModel => TextModelMetadata?.Model ?? string.Empty;

    private string FindSectionText(string id) =>
        Sections.FirstOrDefault(section => string.Equals(section.Id, id, StringComparison.Ordinal))
            ?.Text
        ?? string.Empty;
}

/// <summary>
/// Preserves the original analyzer contract while exposing a feature-neutral projection.
/// New result consumers should use <see cref="FeatureResult"/>.
/// </summary>
public sealed record AnalysisResult(
    string SourceText,
    string JapaneseText,
    string OcrLanguage,
    string TranslationProvider,
    string TranslationModel,
    TimeSpan OcrDuration,
    TimeSpan TranslationDuration,
    string? Warning = null,
    string CaptureSourceKind = "unknown")
{
    private readonly FeatureResult? _canonicalResult;

    public AnalysisResult(FeatureResult featureResult)
        : this(
            GetSourceText(GetRequiredResult(featureResult)),
            GetPrimaryText(featureResult),
            featureResult.OcrLanguage,
            featureResult.TranslationProvider,
            featureResult.TranslationModel,
            featureResult.OcrDuration,
            featureResult.TranslationDuration,
            featureResult.Warning,
            featureResult.CaptureSourceKind)
    {
        _canonicalResult = featureResult;
    }

    public FeatureResult FeatureResult => CreateFeatureResultProjection();

    public FeatureId FeatureId => FeatureResult.FeatureId;

    public IReadOnlyList<ResultSection> Sections => FeatureResult.Sections;

    public ResultSection PrimarySection => FeatureResult.PrimarySection;

    public TextModelMetadata? TextModelMetadata => FeatureResult.TextModelMetadata;

    public bool Equals(AnalysisResult? other) =>
        ReferenceEquals(this, other)
        || (other is not null
            && HasEqualLegacyValues(other)
            && HasEqualCanonicalIdentity(other));

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(SourceText, StringComparer.Ordinal);
        hash.Add(JapaneseText, StringComparer.Ordinal);
        hash.Add(OcrLanguage, StringComparer.Ordinal);
        hash.Add(TranslationProvider, StringComparer.Ordinal);
        hash.Add(TranslationModel, StringComparer.Ordinal);
        hash.Add(OcrDuration);
        hash.Add(TranslationDuration);
        hash.Add(Warning, StringComparer.Ordinal);
        hash.Add(CaptureSourceKind, StringComparer.Ordinal);
        if (_canonicalResult is not null
            && _canonicalResult.FeatureId != FeatureIds.Translation)
        {
            hash.Add(_canonicalResult.FeatureId);
            foreach (ResultSection section in _canonicalResult.Sections)
            {
                hash.Add(section.Id, StringComparer.Ordinal);
                hash.Add(section.Title, StringComparer.Ordinal);
                hash.Add(section.Text, StringComparer.Ordinal);
                hash.Add(section.Role);
            }
        }

        return hash.ToHashCode();
    }

    public static implicit operator FeatureResult(AnalysisResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.FeatureResult;
    }

    private static FeatureResult GetRequiredResult(FeatureResult featureResult)
    {
        ArgumentNullException.ThrowIfNull(featureResult);
        return featureResult;
    }

    private FeatureResult CreateFeatureResultProjection()
    {
        if (_canonicalResult is null)
        {
            return CreateTranslationResult(
                SourceText,
                JapaneseText,
                OcrLanguage,
                TranslationProvider,
                TranslationModel,
                OcrDuration,
                TranslationDuration,
                Warning,
                CaptureSourceKind);
        }

        int firstSupportingIndex = -1;
        for (int index = 0; index < _canonicalResult.Sections.Count; index++)
        {
            if (!_canonicalResult.Sections[index].IsPrimary)
            {
                firstSupportingIndex = index;
                break;
            }
        }

        bool translationPrimaryIsSource = _canonicalResult.FeatureId == FeatureIds.Translation
            && !_canonicalResult.Sections.Any(section => string.Equals(
                section.Id,
                TranslationResultSectionIds.JapaneseText,
                StringComparison.Ordinal));
        ResultSection[] projectedSections = _canonicalResult.Sections
            .Select((section, index) => new ResultSection(
                section.Id,
                section.Title,
                section.IsPrimary
                    ? translationPrimaryIsSource
                        ? SourceText
                        : JapaneseText
                    : index == firstSupportingIndex
                        ? SourceText
                        : section.Text,
                section.Role))
            .ToArray();

        return new FeatureResult(
            _canonicalResult.FeatureId,
            projectedSections,
            CreateTextModelMetadata(OcrLanguage, TranslationProvider, TranslationModel))
        {
            OcrDuration = OcrDuration,
            TranslationDuration = TranslationDuration,
            Warning = Warning,
            CaptureSourceKind = CaptureSourceKind,
        };
    }

    private bool HasEqualLegacyValues(AnalysisResult other) =>
        string.Equals(SourceText, other.SourceText, StringComparison.Ordinal)
        && string.Equals(JapaneseText, other.JapaneseText, StringComparison.Ordinal)
        && string.Equals(OcrLanguage, other.OcrLanguage, StringComparison.Ordinal)
        && string.Equals(
            TranslationProvider,
            other.TranslationProvider,
            StringComparison.Ordinal)
        && string.Equals(TranslationModel, other.TranslationModel, StringComparison.Ordinal)
        && OcrDuration == other.OcrDuration
        && TranslationDuration == other.TranslationDuration
        && string.Equals(Warning, other.Warning, StringComparison.Ordinal)
        && string.Equals(CaptureSourceKind, other.CaptureSourceKind, StringComparison.Ordinal);

    private bool HasEqualCanonicalIdentity(AnalysisResult other)
    {
        bool thisIsLegacyCompatible = _canonicalResult is null
            || _canonicalResult.FeatureId == FeatureIds.Translation;
        bool otherIsLegacyCompatible = other._canonicalResult is null
            || other._canonicalResult.FeatureId == FeatureIds.Translation;
        if (thisIsLegacyCompatible || otherIsLegacyCompatible)
        {
            return thisIsLegacyCompatible && otherIsLegacyCompatible;
        }

        // Both values are canonical non-translation results at this point.
        return _canonicalResult!.FeatureId == other._canonicalResult!.FeatureId
            && _canonicalResult.Sections.SequenceEqual(other._canonicalResult.Sections);
    }

    private static string GetSourceText(FeatureResult result) =>
        result.Sections.FirstOrDefault(section => string.Equals(
            section.Id,
            TranslationResultSectionIds.SourceText,
            StringComparison.Ordinal))?.Text
        ?? result.Sections.FirstOrDefault(section => !section.IsPrimary)?.Text
        ?? result.PrimarySection.Text;

    private static string GetPrimaryText(FeatureResult result) =>
        result.FeatureId == FeatureIds.Translation
            ? result.JapaneseText
            : result.PrimarySection.Text;

    private static TextModelMetadata? CreateTextModelMetadata(
        string ocrLanguage,
        string translationProvider,
        string translationModel) =>
        string.IsNullOrWhiteSpace(ocrLanguage)
            || string.IsNullOrWhiteSpace(translationProvider)
            || string.IsNullOrWhiteSpace(translationModel)
                ? null
                : new TextModelMetadata(
                    ocrLanguage,
                    translationProvider,
                    translationModel);

    private static FeatureResult CreateTranslationResult(
        string sourceText,
        string japaneseText,
        string ocrLanguage,
        string translationProvider,
        string translationModel,
        TimeSpan ocrDuration,
        TimeSpan translationDuration,
        string? warning,
        string captureSourceKind)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(japaneseText);

        List<ResultSection> sections =
        [
            new(
                TranslationResultSectionIds.SourceText,
                string.IsNullOrWhiteSpace(japaneseText)
                    ? "OCR結果（翻訳未設定）"
                    : "OCR結果（英語）",
                sourceText,
                string.IsNullOrWhiteSpace(japaneseText)
                    ? ResultSectionRole.Primary
                    : ResultSectionRole.Supporting),
        ];
        if (!string.IsNullOrWhiteSpace(japaneseText))
        {
            sections.Add(new ResultSection(
                TranslationResultSectionIds.JapaneseText,
                "日本語訳",
                japaneseText,
                ResultSectionRole.Primary));
        }

        return new FeatureResult(
            FeatureIds.Translation,
            sections,
            CreateTextModelMetadata(ocrLanguage, translationProvider, translationModel))
        {
            OcrDuration = ocrDuration,
            TranslationDuration = translationDuration,
            Warning = warning,
            CaptureSourceKind = captureSourceKind,
        };
    }
}

public sealed record ScanProgress(
    Guid CorrelationId,
    ScanStage Stage,
    string Message,
    TimeSpan Elapsed);

public sealed record ScanFailure(
    ScanFailureCode Code,
    ScanStage Stage,
    string Message);

public sealed record ScanOutcome(
    Guid CorrelationId,
    bool IsSuccess,
    AnalysisResult? Result,
    ScanFailure? Failure,
    TimeSpan TotalDuration)
{
    public static ScanOutcome Succeeded(
        Guid correlationId,
        AnalysisResult result,
        TimeSpan totalDuration) =>
        new(correlationId, true, result, null, totalDuration);

    public static ScanOutcome Succeeded(
        Guid correlationId,
        FeatureResult result,
        TimeSpan totalDuration)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Succeeded(correlationId, new AnalysisResult(result), totalDuration);
    }

    public static ScanOutcome Failed(
        Guid correlationId,
        ScanFailure failure,
        TimeSpan totalDuration) =>
        new(correlationId, false, null, failure, totalDuration);
}
