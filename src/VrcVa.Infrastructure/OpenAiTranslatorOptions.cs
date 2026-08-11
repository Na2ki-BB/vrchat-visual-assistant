namespace VrcVa.Infrastructure;

public sealed record OpenAiTranslatorOptions
{
    public const string DefaultModel = "gpt-5.6-luna";
    public const string DefaultEndpoint = "https://api.openai.com/v1/responses";

    public string Model { get; init; } = DefaultModel;

    public Uri Endpoint { get; init; } = new(DefaultEndpoint);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(25);

    public int MaxOutputTokens { get; init; } = 1_200;

    public static OpenAiTranslatorOptions FromEnvironment()
    {
        string model = Environment.GetEnvironmentVariable("VRCVA_OPENAI_MODEL")?.Trim()
            ?? DefaultModel;
        string endpointValue = Environment.GetEnvironmentVariable("VRCVA_OPENAI_ENDPOINT")?.Trim()
            ?? DefaultEndpoint;

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
}

