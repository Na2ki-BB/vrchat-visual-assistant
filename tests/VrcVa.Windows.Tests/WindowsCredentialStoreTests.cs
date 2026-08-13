using VrcVa.Windows.Security;

namespace VrcVa.Windows.Tests;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public void WriteReadDelete_RoundTripsOnlyForCurrentWindowsUser()
    {
        string target = $"VrcVa.Tests/{Guid.NewGuid():N}";
        WindowsCredentialStore store = new(target);

        try
        {
            store.Write("sk-test-placeholder-not-a-real-key");

            Assert.Equal("sk-test-placeholder-not-a-real-key", store.Read());

            store.Delete();
            Assert.Null(store.Read());
        }
        finally
        {
            store.Delete();
        }
    }
}
