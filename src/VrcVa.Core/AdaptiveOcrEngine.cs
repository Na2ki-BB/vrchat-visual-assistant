namespace VrcVa.Core;

public sealed class AdaptiveOcrEngine(
    IOcrEngine primaryEngine,
    IOcrRegionSource regionSource) : IOcrEngine
{
    private const int EnhancementThreshold = 80;
    private const int MinimumImprovement = 4;

    public async Task<OcrOutput> RecognizeAsync(
        CapturedFrame frame,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);

        OcrOutput primary = await primaryEngine
            .RecognizeAsync(frame, cancellationToken)
            .ConfigureAwait(false);
        int primaryScore = Score(primary.Text);
        if (primaryScore >= EnhancementThreshold)
        {
            return primary;
        }

        IReadOnlyList<CapturedFrame> regions = await regionSource
            .CreateRegionsAsync(frame, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            List<OcrOutput> regionOutputs = [];
            foreach (CapturedFrame region in regions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                regionOutputs.Add(await primaryEngine
                    .RecognizeAsync(region, cancellationToken)
                    .ConfigureAwait(false));
            }

            string enhancedText = MergeUniqueLines(regionOutputs.Select(output => output.Text));
            if (Score(enhancedText) < primaryScore + MinimumImprovement)
            {
                return primary;
            }

            string? warning = CombineWarnings(
                "画面全体を分割したOCR強化処理を使用しました。",
                regionOutputs.Select(output => output.Warning));
            string language = regionOutputs
                .Select(output => output.RecognizerLanguage)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? primary.RecognizerLanguage;
            return new OcrOutput(enhancedText, language, warning);
        }
        finally
        {
            foreach (CapturedFrame region in regions)
            {
                region.Dispose();
            }
        }
    }

    internal static string MergeUniqueLines(IEnumerable<string> candidates)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        List<string> lines = [];
        foreach (string candidate in candidates)
        {
            foreach (string line in NormalizeLines(candidate))
            {
                if (seen.Add(line))
                {
                    lines.Add(line);
                }
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static int Score(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        int score = 0;
        foreach (char character in text)
        {
            if (character is >= 'A' and <= 'Z'
                or >= 'a' and <= 'z'
                or >= '0' and <= '9')
            {
                score++;
            }
        }

        return score;
    }

    private static IEnumerable<string> NormalizeLines(string text) =>
        text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => string.Join(
                ' ',
                line.Split(
                    [' ', '\t'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
            .Where(line => line.Length > 0);

    private static string? CombineWarnings(
        string enhancementWarning,
        IEnumerable<string?> warnings)
    {
        IEnumerable<string> values = warnings
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.Ordinal);
        return string.Join(' ', values.Prepend(enhancementWarning));
    }
}
