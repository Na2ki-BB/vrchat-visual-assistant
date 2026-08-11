using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Interop;

namespace VrcVa.Windows.Win32;

internal sealed class GlobalHotKey : IDisposable
{
    private readonly IntPtr _windowHandle;
    private readonly int _identifier;
    private readonly HwndSource _source;
    private bool _registered;

    public GlobalHotKey(
        IntPtr windowHandle,
        int identifier,
        HotKeyDefinition definition)
    {
        if (identifier <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(identifier));
        }

        _windowHandle = windowHandle;
        _identifier = identifier;
        Definition = definition;
        _source = HwndSource.FromHwnd(windowHandle)
            ?? throw new InvalidOperationException("WPF window source is not available.");
        _source.AddHook(WindowProcedure);

        _registered = NativeMethods.RegisterHotKey(
            windowHandle,
            identifier,
            definition.Modifiers | NativeMethods.ModNoRepeat,
            definition.VirtualKey);
        if (!_registered)
        {
            _source.RemoveHook(WindowProcedure);
            throw new Win32Exception(
                "The global hotkey is already in use or could not be registered.");
        }
    }

    public event EventHandler? Pressed;

    public HotKeyDefinition Definition { get; }

    public void Dispose()
    {
        if (_registered)
        {
            NativeMethods.UnregisterHotKey(_windowHandle, _identifier);
            _registered = false;
        }

        _source.RemoveHook(WindowProcedure);
    }

    private IntPtr WindowProcedure(
        IntPtr windowHandle,
        int message,
        IntPtr wordParameter,
        IntPtr longParameter,
        ref bool handled)
    {
        if (message == NativeMethods.WmHotKey && wordParameter.ToInt32() == _identifier)
        {
            handled = true;
            Pressed?.Invoke(this, EventArgs.Empty);
        }

        return IntPtr.Zero;
    }
}

internal sealed record HotKeyDefinition(uint Modifiers, uint VirtualKey, string DisplayText)
{
    internal static HotKeyDefinition FromEnvironment(
        string variableName = "VRCVA_HOTKEY",
        string defaultValue = "Ctrl+Shift+T")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(variableName);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultValue);

        string configured = Environment.GetEnvironmentVariable(variableName)?.Trim()
            ?? defaultValue;
        return Parse(configured);
    }

    internal static HotKeyDefinition Parse(string value)
    {
        string[] parts = value
            .Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            throw new InvalidOperationException(
                "VRCVA_HOTKEY must include a modifier and key, for example Ctrl+Shift+T.");
        }

        uint modifiers = 0;
        Key? key = null;
        foreach (string part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= NativeMethods.ModControl;
                    break;
                case "SHIFT":
                    modifiers |= NativeMethods.ModShift;
                    break;
                case "ALT":
                    modifiers |= NativeMethods.ModAlt;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= NativeMethods.ModWin;
                    break;
                default:
                    if (key is not null || !Enum.TryParse(part, ignoreCase: true, out Key parsedKey))
                    {
                        throw new InvalidOperationException(
                            $"VRCVA_HOTKEY contains an unsupported key token: {part}");
                    }

                    key = parsedKey;
                    break;
            }
        }

        if (modifiers == 0 || key is null)
        {
            throw new InvalidOperationException(
                "VRCVA_HOTKEY must include at least one modifier and one key.");
        }

        return new HotKeyDefinition(
            modifiers,
            (uint)KeyInterop.VirtualKeyFromKey(key.Value),
            value);
    }
}
