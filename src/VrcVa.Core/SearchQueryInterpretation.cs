using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VrcVa.Core;

/// <summary>A validated, feature-owned query. It never replaces the shared transcript.</summary>
public sealed class SearchQueryInterpretation
{
    public const int MaximumQueryUtf8Bytes = 1_000;
    private static readonly Regex UrlPattern = new(
        @"(?:[a-z][a-z0-9+.-]*:(?://|/|\?)|(?:https?|ftp|sftp|file|data|javascript|mailto|tel|blob|about|urn|magnet):|www\.|(?:youtube\.com|youtu\.be)(?:/|\b)|(?:[0-9]{1,3}\.){3}[0-9]{1,3}(?:/|\?|:[0-9])|localhost(?:/|\?|:[0-9])|(?:[\p{L}\p{N}-]+\.)+(?:[\p{L}\p{N}-]{2,}(?:/|\?|:[0-9])|(?:com|org|net|io|jp|dev|site|edu|gov|mil|xyz|info|biz|tv|uk|au|ca|de|fr|cn|ru)(?:/|\b)))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(100));

    public SearchQueryInterpretation(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        string value = query.Trim();
        if (value.Length == 0 || value.Length > MaximumQueryUtf8Bytes
            || Encoding.UTF8.GetByteCount(value) > MaximumQueryUtf8Bytes
            || value.Any(character => char.IsControl(character)
                || char.GetUnicodeCategory(character) is UnicodeCategory.Format or UnicodeCategory.LineSeparator
                    or UnicodeCategory.ParagraphSeparator)
            || ContainsInvalidSurrogate(value) || UrlPattern.IsMatch(value)
            || value.Contains('`') || value[0] is '"' or '\'' or '「' or '『' or '{' or '['
            || value[^1] is '"' or '\'' or '」' or '』' or '}' or ']')
        {
            throw new ScanException(ScanFailureCode.SearchInterpretationInvalidResponse,
                ScanStage.SearchInterpretation, "検索AI解釈から有効な1行の検索語が返りませんでした。");
        }

        Query = value;
    }

    public string Query { get; }

    private static bool ContainsInvalidSurrogate(string value)
    {
        for (int index = 0; index < value.Length; index++)
        {
            if (!char.IsSurrogate(value[index])) { continue; }
            if (!char.IsHighSurrogate(value[index]) || index + 1 >= value.Length
                || !char.IsLowSurrogate(value[++index])) { return true; }
        }
        return false;
    }
}

public interface ISearchQueryInterpreter
{
    Task<SearchQueryInterpretation> InterpretAsync(TextInputSession input, CancellationToken cancellationToken);
}
