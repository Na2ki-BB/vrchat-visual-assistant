namespace VrcVa.Core;

public sealed class AdaptiveOcrEngine(
    IOcrEngine primaryEngine,
    IOcrRegionSource regionSource) : IOcrEngine
{
    private const int EnhancementThreshold = 80;
    private const int MinimumApproximateMatchLength = 12;
    private const int ApproximateMatchLengthPerEdit = 12;
    private const int MaximumApproximateMatchDistance = 3;

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

            string enhancedText = MergeUniqueLines(
                regionOutputs
                    .Select(output => output.Text)
                    .Prepend(primary.Text));

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
        List<string> lines = [];
        List<string> comparisonKeys = [];
        foreach (string candidate in candidates)
        {
            int existingLineCount = lines.Count;
            HashSet<int> matchedExistingLines = [];
            foreach (string line in NormalizeLines(candidate))
            {
                string comparisonKey = line.ToUpperInvariant();
                int matchIndex = FindBestMatchIndex(
                    comparisonKey,
                    comparisonKeys,
                    existingLineCount,
                    matchedExistingLines);
                if (matchIndex >= 0)
                {
                    matchedExistingLines.Add(matchIndex);
                    continue;
                }

                lines.Add(line);
                comparisonKeys.Add(comparisonKey);
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static int FindBestMatchIndex(
        string candidate,
        IReadOnlyList<string> existingLines,
        int existingLineCount,
        IReadOnlySet<int> excludedIndices)
    {
        int bestIndex = -1;
        int bestDistance = int.MaxValue;
        for (int index = 0; index < existingLineCount; index++)
        {
            if (excludedIndices.Contains(index))
            {
                continue;
            }

            string existing = existingLines[index];
            int maximumDistance = GetMaximumEditDistance(candidate.Length, existing.Length);
            if (Math.Abs(candidate.Length - existing.Length) > maximumDistance)
            {
                continue;
            }

            int distance = CalculateEditDistance(candidate, existing, maximumDistance);
            if (distance > maximumDistance)
            {
                continue;
            }

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestIndex = index;
                if (distance == 0)
                {
                    break;
                }
            }
        }

        return bestIndex;
    }

    private static int GetMaximumEditDistance(int firstLength, int secondLength)
    {
        int shorterLength = Math.Min(firstLength, secondLength);
        if (shorterLength < MinimumApproximateMatchLength)
        {
            return 0;
        }

        int longerLength = Math.Max(firstLength, secondLength);
        return Math.Min(
            MaximumApproximateMatchDistance,
            Math.Max(1, longerLength / ApproximateMatchLengthPerEdit));
    }

    private static int CalculateEditDistance(
        string first,
        string second,
        int maximumDistance)
    {
        if (string.Equals(first, second, StringComparison.Ordinal))
        {
            return 0;
        }

        if (maximumDistance == 0)
        {
            return 1;
        }

        int[] previous = new int[second.Length + 1];
        int[] current = new int[second.Length + 1];
        for (int column = 0; column <= second.Length; column++)
        {
            previous[column] = column;
        }

        for (int row = 1; row <= first.Length; row++)
        {
            current[0] = row;
            int rowMinimum = current[0];
            for (int column = 1; column <= second.Length; column++)
            {
                int substitutionCost = first[row - 1] == second[column - 1] ? 0 : 1;
                current[column] = Math.Min(
                    Math.Min(
                        current[column - 1] + 1,
                        previous[column] + 1),
                    previous[column - 1] + substitutionCost);
                rowMinimum = Math.Min(rowMinimum, current[column]);
            }

            if (rowMinimum > maximumDistance)
            {
                return maximumDistance + 1;
            }

            (previous, current) = (current, previous);
        }

        return previous[second.Length] <= maximumDistance
            ? previous[second.Length]
            : maximumDistance + 1;
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
