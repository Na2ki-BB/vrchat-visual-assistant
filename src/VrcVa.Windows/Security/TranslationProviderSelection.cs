namespace VrcVa.Windows.Security;

internal static class TranslationProviderSelection
{
    public static string Resolve(string? configuredProvider, bool hasStoredApiKey)
    {
        string? normalizedProvider = configuredProvider?.Trim().ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalizedProvider)
            ? hasStoredApiKey ? "openai" : "none"
            : normalizedProvider;
    }
}
