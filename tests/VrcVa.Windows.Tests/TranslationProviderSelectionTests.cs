using VrcVa.Windows.Security;

namespace VrcVa.Windows.Tests;

public sealed class TranslationProviderSelectionTests
{
    [Theory]
    [InlineData(null, false, "none")]
    [InlineData("", false, "none")]
    [InlineData(null, true, "openai")]
    [InlineData("none", true, "none")]
    [InlineData(" OPENAI ", false, "openai")]
    public void Resolve_RequiresStoredKeyUnlessProviderIsExplicit(
        string? configuredProvider,
        bool hasStoredApiKey,
        string expected)
    {
        Assert.Equal(
            expected,
            TranslationProviderSelection.Resolve(configuredProvider, hasStoredApiKey));
    }
}
