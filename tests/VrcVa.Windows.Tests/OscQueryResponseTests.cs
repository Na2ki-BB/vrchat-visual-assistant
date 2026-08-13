using System.Text.Json;
using System.Net;
using VrcVa.Windows.Osc;

namespace VrcVa.Windows.Tests;

public sealed class OscQueryResponseTests
{
    [Fact]
    public void CreateQueryResponse_AdvertisesAvatarTreeAndLoopbackPort()
    {
        OscQueryResponse root = OscTriggerService.CreateQueryResponse(
            "/",
            OscTriggerOptions.DefaultAddress,
            12345);
        OscQueryResponse hostInfo = OscTriggerService.CreateQueryResponse(
            "/?HOST_INFO",
            OscTriggerOptions.DefaultAddress,
            12345);

        Assert.Equal(200, root.StatusCode);
        Assert.Contains("\"avatar\"", root.Body, StringComparison.Ordinal);
        Assert.Contains("\"VRCVA_Scan\"", root.Body, StringComparison.Ordinal);

        using JsonDocument document = JsonDocument.Parse(hostInfo.Body);
        Assert.Equal("127.0.0.1", document.RootElement.GetProperty("OSC_IP").GetString());
        Assert.Equal(12345, document.RootElement.GetProperty("OSC_PORT").GetInt32());
        Assert.Equal("UDP", document.RootElement.GetProperty("OSC_TRANSPORT").GetString());
    }

    [Fact]
    public void CreateQueryResponse_RejectsUnknownPath()
    {
        OscQueryResponse response = OscTriggerService.CreateQueryResponse(
            "/unrelated",
            OscTriggerOptions.DefaultAddress,
            12345);

        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public void CreateQueryResponse_DescribesConfiguredIntegerType()
    {
        OscQueryResponse response = OscTriggerService.CreateQueryResponse(
            OscTriggerOptions.DefaultAddress,
            OscTriggerOptions.DefaultAddress,
            12345,
            OscTriggerValueType.Integer);

        Assert.Contains("\"TYPE\":\"i\"", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void CreateQueryResponse_DoesNotClaimUnsupportedQueryExtensions()
    {
        OscQueryResponse response = OscTriggerService.CreateQueryResponse(
            "/?VALUE",
            OscTriggerOptions.DefaultAddress,
            12345);

        Assert.Equal(204, response.StatusCode);
        Assert.Empty(response.Body);
    }

    [Fact]
    public void IsLocalHostAddress_AcceptsOnlyLoopbackOrThisHostsAddresses()
    {
        HashSet<IPAddress> localAddresses = [IPAddress.Parse("192.168.10.25")];

        Assert.True(OscTriggerService.IsLocalHostAddress("127.0.0.1", localAddresses));
        Assert.True(OscTriggerService.IsLocalHostAddress("192.168.10.25", localAddresses));
        Assert.False(OscTriggerService.IsLocalHostAddress("192.0.2.10", localAddresses));
        Assert.False(OscTriggerService.IsLocalHostAddress(null, localAddresses));
    }
}
