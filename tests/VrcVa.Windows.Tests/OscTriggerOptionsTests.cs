using VrcVa.Windows.Osc;

namespace VrcVa.Windows.Tests;

public sealed class OscTriggerOptionsTests
{
    [Fact]
    public void Parse_DefaultsToDisabled()
    {
        OscTriggerOptions options = OscTriggerOptions.Parse(null, null, null);

        Assert.False(options.Enabled);
        Assert.Equal(OscTriggerOptions.DefaultAddress, options.Address);
        Assert.Equal(TimeSpan.FromMilliseconds(750), options.Debounce);
        Assert.Equal(OscTriggerValueType.Boolean, options.ValueType);
    }

    [Fact]
    public void Parse_AllowsExpectedIntegerValue()
    {
        OscTriggerOptions options = OscTriggerOptions.Parse(
            "true",
            null,
            null,
            "int",
            "42");

        Assert.Equal(OscTriggerValueType.Integer, options.ValueType);
        Assert.Equal(42, options.ExpectedIntegerValue);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("on")]
    public void Parse_AllowsExplicitEnable(string value)
    {
        OscTriggerOptions options = OscTriggerOptions.Parse(value, null, "500");

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMilliseconds(500), options.Debounce);
    }

    [Theory]
    [InlineData("maybe", null, null)]
    [InlineData("true", "/not/avatar", null)]
    [InlineData("true", "/avatar/parameters/A/B", null)]
    [InlineData("true", null, "99")]
    [InlineData("true", null, "10001")]
    public void Parse_RejectsUnsafeValues(
        string? enabled,
        string? address,
        string? debounce)
    {
        Assert.Throws<InvalidOperationException>(() =>
            OscTriggerOptions.Parse(enabled, address, debounce));
    }

    [Fact]
    public void Parse_RejectsUnknownType()
    {
        Assert.Throws<InvalidOperationException>(() =>
            OscTriggerOptions.Parse("true", null, null, "float"));
    }
}
