using System.Globalization;
using System.Text;
using VrcVa.Core;

namespace VrcVa.Infrastructure;

public sealed class PrivacySafeFileLogger : IPrivacySafeLogger
{
    private readonly string _logDirectory;
    private readonly object _sync = new();

    public PrivacySafeFileLogger(string logDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logDirectory);
        _logDirectory = logDirectory;
        Directory.CreateDirectory(_logDirectory);
    }

    public string LogDirectory => _logDirectory;

    public void Info(
        string eventName,
        Guid correlationId,
        ScanStage stage,
        TimeSpan? duration = null,
        IReadOnlyDictionary<string, long>? numericMetrics = null)
    {
        StringBuilder line = CreatePrefix("INFO", eventName, correlationId, stage);
        if (duration is not null)
        {
            line.Append(" durationMs=")
                .Append(duration.Value.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture));
        }

        if (numericMetrics is not null)
        {
            foreach ((string key, long value) in numericMetrics.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                line.Append(' ')
                    .Append(SanitizeToken(key))
                    .Append('=')
                    .Append(value.ToString(CultureInfo.InvariantCulture));
            }
        }

        WriteLine(line);
    }

    public void Error(
        string eventName,
        Guid correlationId,
        ScanStage stage,
        ScanFailureCode failureCode,
        Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        StringBuilder line = CreatePrefix("ERROR", eventName, correlationId, stage);
        line.Append(" failure=")
            .Append(failureCode)
            .Append(" exceptionType=")
            .Append(SanitizeToken(exception.GetType().Name))
            .Append(" hresult=")
            .Append(exception.HResult.ToString(CultureInfo.InvariantCulture));
        WriteLine(line);
    }

    private static StringBuilder CreatePrefix(
        string level,
        string eventName,
        Guid correlationId,
        ScanStage stage) =>
        new StringBuilder()
            .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture))
            .Append(" level=")
            .Append(level)
            .Append(" event=")
            .Append(SanitizeToken(eventName))
            .Append(" correlationId=")
            .Append(correlationId.ToString("D"))
            .Append(" stage=")
            .Append(stage);

    private static string SanitizeToken(string value) =>
        new(value.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '-' or '_').ToArray());

    private void WriteLine(StringBuilder line)
    {
        string path = Path.Combine(
            _logDirectory,
            $"vrcva-{DateTime.UtcNow:yyyyMMdd}.log");
        lock (_sync)
        {
            File.AppendAllText(path, line.AppendLine().ToString(), Encoding.UTF8);
        }
    }
}
