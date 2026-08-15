namespace VrcVa.Windows.OpenVr;

internal sealed class PointerActivationGate
{
    private int _target;
    private bool _releaseObserved;

    public int Update(
        int target,
        bool selectActive,
        bool selectPressed,
        bool selectChanged)
    {
        if (!selectActive || target == 0)
        {
            _target = 0;
            _releaseObserved = !selectPressed;
            return 0;
        }

        if (target != _target)
        {
            _target = target;
            _releaseObserved = !selectPressed;
        }

        if (!selectPressed)
        {
            _releaseObserved = true;
            return 0;
        }

        if (!selectChanged || !_releaseObserved)
        {
            return 0;
        }

        _releaseObserved = false;
        return target;
    }

    public void Reset()
    {
        _target = 0;
        _releaseObserved = false;
    }
}
