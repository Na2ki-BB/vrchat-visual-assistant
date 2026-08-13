namespace VrcVa.Infrastructure;

public sealed record OpenAiTranslatorOptions
{
    public const int DefaultMaxInputUtf8Bytes = 4_000;
    public const int DefaultMaxRequestsPerSession = 10;
    public const string BudgetModel = "gpt-5.4-nano";
    public const string QualityModel = "gpt-5.6-luna";
    public const string DefaultModel = QualityModel;
    public const string DefaultEndpoint = "https://api.openai.com/v1/responses";

    public string Model { get; init; } = DefaultModel;

    public Uri Endpoint { get; init; } = new(DefaultEndpoint);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(25);

    public int MaxOutputTokens { get; init; } = 1_200;

    public int MaxInputUtf8Bytes { get; init; } = DefaultMaxInputUtf8Bytes;

    public int MaxRequestsPerSession { get; init; } = DefaultMaxRequestsPerSession;

    public static OpenAiTranslatorOptions FromEnvironment()
    {
        string model = Environment.GetEnvironmentVariable("VRCVA_OPENAI_MODEL")?.Trim()
            ?? DefaultModel;
        string endpointValue = Environment.GetEnvironmentVariable("VRCVA_OPENAI_ENDPOINT")?.Trim()
            ?? DefaultEndpoint;

        if (!IsSupportedModel(model))
        {
            throw new InvalidOperationException(
                $"VRCVA_OPENAI_MODEL must be {QualityModel} or {BudgetModel}.");
        }

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out Uri? endpoint))
        {
            throw new InvalidOperationException("VRCVA_OPENAI_ENDPOINT must be an absolute URI.");
        }

        int timeoutSeconds = 25;
        string? timeoutValue = Environment.GetEnvironmentVariable("VRCVA_OPENAI_TIMEOUT_SECONDS");
        if (!string.IsNullOrWhiteSpace(timeoutValue)
            && (!int.TryParse(timeoutValue, out timeoutSeconds) || timeoutSeconds is < 1 or > 120))
        {
            throw new InvalidOperationException(
                "VRCVA_OPENAI_TIMEOUT_SECONDS must be an integer from 1 to 120.");
        }

        return new OpenAiTranslatorOptions
        {
            Model = model,
            Endpoint = endpoint,
            Timeout = TimeSpan.FromSeconds(timeoutSeconds),
        };
    }

    public static bool IsSupportedModel(string? model) =>
        string.Equals(model, QualityModel, StringComparison.Ordinal)
        || string.Equals(model, BudgetModel, StringComparison.Ordinal);
}
