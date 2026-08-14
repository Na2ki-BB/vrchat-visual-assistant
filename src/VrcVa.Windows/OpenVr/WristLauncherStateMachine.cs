namespace VrcVa.Windows.OpenVr;

internal enum WristLauncherView
{
    Hidden,
    DimChip,
    ArmedChip,
    Menu,
    Calibrating,
    Scanning,
    Result,
}

internal sealed class WristLauncherStateMachine
{
    internal static readonly TimeSpan ArmDelay = TimeSpan.FromMilliseconds(150);

    private TimeSpan? _facingSince;

    public WristLauncherView View { get; private set; } = WristLauncherView.Hidden;

    public bool Start()
    {
        if (View != WristLauncherView.Hidden)
        {
            return false;
        }

        View = WristLauncherView.DimChip;
        return true;
    }

    public bool UpdateFacing(bool entersFacingCone, bool remainsInFacingCone, TimeSpan now)
    {
        if (View is not (WristLauncherView.DimChip or WristLauncherView.ArmedChip))
        {
            return false;
        }

        WristLauncherView previous = View;
        if (View == WristLauncherView.ArmedChip)
        {
            if (!remainsInFacingCone)
            {
                View = WristLauncherView.DimChip;
                _facingSince = null;
            }

            return previous != View;
        }

        if (!entersFacingCone)
        {
            _facingSince = null;
            return false;
        }

        _facingSince ??= now;
        if (now - _facingSince.Value >= ArmDelay)
        {
            View = WristLauncherView.ArmedChip;
        }

        return previous != View;
    }

    public bool ExpandMenu()
    {
        if (View is not (WristLauncherView.DimChip or WristLauncherView.ArmedChip))
        {
            return false;
        }

        View = WristLauncherView.Menu;
        return true;
    }

    public bool CollapseMenu()
    {
        if (View != WristLauncherView.Menu)
        {
            return false;
        }

        View = WristLauncherView.DimChip;
        _facingSince = null;
        return true;
    }

    public bool BeginCalibration()
    {
        if (View != WristLauncherView.Menu)
        {
            return false;
        }

        View = WristLauncherView.Calibrating;
        _facingSince = null;
        return true;
    }

    public bool EndCalibration()
    {
        if (View != WristLauncherView.Calibrating)
        {
            return false;
        }

        View = WristLauncherView.DimChip;
        _facingSince = null;
        return true;
    }

    public bool BeginScan()
    {
        if (View is WristLauncherView.Hidden
            or WristLauncherView.Scanning)
        {
            return false;
        }

        View = WristLauncherView.Scanning;
        return true;
    }

    public void ShowResult() => View = WristLauncherView.Result;

    public void ReturnToChip()
    {
        View = WristLauncherView.DimChip;
        _facingSince = null;
    }

    public bool HandlePoseLoss()
    {
        if (View is WristLauncherView.Hidden
            or WristLauncherView.Calibrating
            or WristLauncherView.Scanning
            or WristLauncherView.Result)
        {
            return false;
        }

        bool changed = View != WristLauncherView.DimChip;
        View = WristLauncherView.DimChip;
        _facingSince = null;
        return changed;
    }

    public void Stop()
    {
        View = WristLauncherView.Hidden;
        _facingSince = null;
    }
}
