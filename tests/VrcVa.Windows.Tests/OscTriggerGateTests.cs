using VrcVa.Windows.Osc;

namespace VrcVa.Windows.Tests;

public sealed class OscTriggerGateTests
{
    private static readonly TimeSpan Start = TimeSpan.FromHours(1);

    [Fact]
    public void Observe_AllowsFirstRisingEdgeAfterStartup()
    {
        OscTriggerGate gate = new(TimeSpan.FromMilliseconds(750));

        Assert.True(gate.Observe(active: true, Start));
        Assert.False(gate.Observe(active: true, Start + TimeSpan.FromMilliseconds(5)));
        Assert.False(gate.Observe(active: false, Start + TimeSpan.FromMilliseconds(10)));
        Assert.False(gate.Observe(active: true, Start + TimeSpan.FromMilliseconds(20)));
        Assert.False(gate.Observe(active: false, Start + TimeSpan.FromMilliseconds(800)));
        Assert.True(gate.Observe(active: true, Start + TimeSpan.FromMilliseconds(801)));
    }

    [Fact]
    public void Observe_DebouncesSeparateRisingEdges()
    {
        OscTriggerGate gate = new(TimeSpan.FromMilliseconds(750));

        Assert.False(gate.Observe(active: false, Start));
        Assert.True(gate.Observe(active: true, Start + TimeSpan.FromMilliseconds(10)));
        Assert.False(gate.Observe(active: false, Start + TimeSpan.FromMilliseconds(20)));
        Assert.False(gate.Observe(active: true, Start + TimeSpan.FromMilliseconds(100)));
        Assert.False(gate.Observe(active: false, Start + TimeSpan.FromMilliseconds(800)));
        Assert.True(gate.Observe(active: true, Start + TimeSpan.FromMilliseconds(801)));
    }

    [Fact]
    public void ResetForAvatarChange_SuppressesActiveStateSeenDuringSettling()
    {
        OscTriggerGate gate = new(TimeSpan.FromMilliseconds(750));

        gate.ResetForAvatarChange(Start);
        Assert.False(gate.Observe(active: true, Start + TimeSpan.FromMilliseconds(100)));
        Assert.False(gate.Observe(active: true, Start + TimeSpan.FromSeconds(1)));
        Assert.False(gate.Observe(active: false, Start + TimeSpan.FromSeconds(2)));
        Assert.True(gate.Observe(active: true, Start + TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void ResetForAvatarChange_AllowsFirstActiveAfterQuietSettlingPeriod()
    {
        OscTriggerGate gate = new(TimeSpan.FromMilliseconds(750));

        gate.ResetForAvatarChange(Start);

        Assert.True(gate.Observe(active: true, Start + TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void ResetForAvatarChange_RearmsWhenButtonResetsDuringSettling()
    {
        OscTriggerGate gate = new(TimeSpan.FromMilliseconds(750));

        gate.ResetForAvatarChange(Start);
        Assert.False(gate.Observe(active: true, Start + TimeSpan.FromMilliseconds(100)));
        Assert.False(gate.Observe(active: false, Start + TimeSpan.FromMilliseconds(200)));

        Assert.True(gate.Observe(active: true, Start + TimeSpan.FromSeconds(1)));
    }
}
