using VrcVa.Windows.Capture;
using VrcVa.Windows.OpenVr;

namespace VrcVa.Windows.Tests;

public sealed class OpenVrEyeCaptureOptionsTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("left")]
    [InlineData("LEFT")]
    public void Parse_DefaultsToLeft(string? value)
    {
        OpenVrEyeCaptureOptions options = OpenVrEyeCaptureOptions.Parse(
            value,
            out string? warning);

        Assert.Equal(OpenVrEye.Left, options.Eye);
        Assert.Null(warning);
    }

    [Fact]
    public void Parse_AllowsRight()
    {
        OpenVrEyeCaptureOptions options = OpenVrEyeCaptureOptions.Parse(
            "right",
            out string? warning);

        Assert.Equal(OpenVrEye.Right, options.Eye);
        Assert.Null(warning);
    }

    [Fact]
    public void Parse_InvalidValueWarnsAndUsesLeft()
    {
        OpenVrEyeCaptureOptions options = OpenVrEyeCaptureOptions.Parse(
            "center",
            out string? warning);

        Assert.Equal(OpenVrEye.Left, options.Eye);
        Assert.Contains(OpenVrEyeCaptureOptions.EnvironmentVariable, warning);
    }
}
