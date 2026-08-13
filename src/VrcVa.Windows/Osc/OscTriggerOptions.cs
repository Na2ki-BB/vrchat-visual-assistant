namespace VrcVa.Windows.Osc;

internal enum OscTriggerValueType
{
    Boolean,
    Integer,
}

internal sealed record OscTriggerOptions(
    bool Enabled,
    string Address,
    TimeSpan Debounce,
    OscTriggerValueType ValueType,
    int ExpectedIntegerValue)
{
    internal const string EnabledEnvironmentVariable = "VRCVA_OSC_TRIGGER_ENABLED";
    internal const string AddressEnvironmentVariable = "VRCVA_OSC_TRIGGER_ADDRESS";
    internal const string DebounceEnvironmentVariable = "VRCVA_OSC_TRIGGER_DEBOUNCE_MS";
    internal const string TypeEnvironmentVariable = "VRCVA_OSC_TRIGGER_TYPE";
    internal const string IntegerValueEnvironmentVariable = "VRCVA_OSC_TRIGGER_INT_VALUE";
    internal const string DefaultAddress = "/avatar/parameters/VRCVA_Scan";
    private const int DefaultDebounceMilliseconds = 750;

    internal static OscTriggerOptions FromEnvironment() => Parse(
        Environment.GetEnvironmentVariable(EnabledEnvironmentVariable),
        Environment.GetEnvironmentVariable(AddressEnvironmentVariable),
        Environment.GetEnvironmentVariable(DebounceEnvironmentVariable),
        Environment.GetEnvironmentVariable(TypeEnvironmentVariable),
        Environment.GetEnvironmentVariable(IntegerValueEnvironmentVariable));

    internal static OscTriggerOptions Parse(
        string? enabledValue,
        string? addressValue,
        string? debounceValue,
        string? typeValue = null,
        string? integerValue = null)
    {
        bool enabled = ParseEnabled(enabledValue);
        string address = string.IsNullOrWhiteSpace(addressValue)
            ? DefaultAddress
            : addressValue.Trim();
        int debounceMilliseconds = string.IsNullOrWhiteSpace(debounceValue)
            ? DefaultDebounceMilliseconds
            : int.TryParse(
                debounceValue,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsedMilliseconds)
                && parsedMilliseconds is >= 100 and <= 10_000
                    ? parsedMilliseconds
                    : throw new InvalidOperationException(
                        $"{DebounceEnvironmentVariable} must be an integer from 100 to 10000.");

        OscTriggerValueType valueType = string.IsNullOrWhiteSpace(typeValue)
            ? OscTriggerValueType.Boolean
            : typeValue.Trim().ToLowerInvariant() switch
            {
                "bool" or "boolean" => OscTriggerValueType.Boolean,
                "int" or "integer" => OscTriggerValueType.Integer,
                _ => throw new InvalidOperationException(
                    $"{TypeEnvironmentVariable} must be bool or int."),
            };
        int expectedIntegerValue = string.IsNullOrWhiteSpace(integerValue)
            ? 1
            : int.TryParse(
                integerValue,
                System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture,
                out int parsedIntegerValue)
                ? parsedIntegerValue
                : throw new InvalidOperationException(
                    $"{IntegerValueEnvironmentVariable} must be a 32-bit integer.");

        ValidateAddress(address);
        return new OscTriggerOptions(
            enabled,
            address,
            TimeSpan.FromMilliseconds(debounceMilliseconds),
            valueType,
            expectedIntegerValue);
    }

    private static bool ParseEnabled(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" => true,
            "0" or "false" or "no" or "off" => false,
            _ => throw new InvalidOperationException(
                $"{EnabledEnvironmentVariable} must be true or false."),
        };
    }

    private static void ValidateAddress(string address)
    {
        const string prefix = "/avatar/parameters/";
        if (!address.StartsWith(prefix, StringComparison.Ordinal)
            || address.Length == prefix.Length
            || address[prefix.Length..].Contains('/', StringComparison.Ordinal)
            || address.Any(character => character is <= ' ' or >= '\u007f' or '#' or '*' or ',' or '?' or '[' or ']' or '{' or '}'))
        {
            throw new InvalidOperationException(
                $"{AddressEnvironmentVariable} must be one ASCII avatar parameter address, "
                + "for example /avatar/parameters/VRCVA_Scan.");
        }
    }
}
