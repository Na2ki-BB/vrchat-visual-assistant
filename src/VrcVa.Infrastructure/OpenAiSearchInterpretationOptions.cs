using VrcVa.Core;

namespace VrcVa.Infrastructure;

/// <summary>Search-only selection. Translation's models and environment remain unchanged.</summary>
public sealed record OpenAiSearchInterpretationOptions
{
    public const string DefaultModel = "gpt-6-luna";
    public const int MaximumOutputTokens = 400;
    public const int MaximumResponseBytes = 65_536;
    public string Model { get; init; } = DefaultModel;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(OpenAiTranslatorOptions.MaxTimeoutSeconds);

    public static bool IsSupportedModel(string? model) => string.Equals(model, DefaultModel, StringComparison.Ordinal);

    public static OpenAiSearchInterpretationOptions FromEnvironment()
    {
        string model = Environment.GetEnvironmentVariable("VRCVA_SEARCH_INTERPRETATION_MODEL")?.Trim() ?? DefaultModel;
        OpenAiSearchInterpretationOptions options = new() { Model = model };
        options.Validate();
        return options;
    }

    internal void Validate()
    {
        if (!IsSupportedModel(Model))
        {
            throw new ArgumentException($"Search interpretation model must be {DefaultModel}.", nameof(Model));
        }
        if (Timeout <= TimeSpan.FromMilliseconds(1)
            || Timeout > TimeSpan.FromSeconds(OpenAiTranslatorOptions.MaxTimeoutSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(Timeout));
        }
    }

    internal OpenAiTranslatorOptions ToTransportOptions(TextRequestQuota quota)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(quota);
        if (quota.Purpose != TextRequestPurpose.SearchInterpretation)
        {
            throw new ArgumentException("Search interpretation requires its own quota.", nameof(quota));
        }
        return new OpenAiTranslatorOptions
        {
            Model = Model,
            Endpoint = new Uri(OpenAiTranslatorOptions.DefaultEndpoint),
            Timeout = Timeout,
            MaxOutputTokens = MaximumOutputTokens,
            MaxInputUtf8Bytes = TextInputSession.MaximumTranscriptUtf8Bytes,
            MaxRequestsPerSession = quota.Maximum,
        };
    }
}
