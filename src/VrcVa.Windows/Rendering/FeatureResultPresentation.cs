using VrcVa.Core;

namespace VrcVa.Windows.Rendering;

internal sealed record FeatureResultPresentation(
    string SourceTitle,
    string SourceText,
    string PrimaryTitle,
    string PrimaryText,
    string CopyButtonText,
    string CompletionMessage)
{
    public static FeatureResultPresentation Create(FeatureResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        ResultSection primary = result.PrimarySection;
        ResultSection? source = result.Sections.FirstOrDefault(section => !section.IsPrimary);
        bool isTranslation = result.FeatureId == FeatureIds.Translation;
        bool translationWithoutProvider = isTranslation
            && string.IsNullOrWhiteSpace(result.JapaneseText);

        string sourceTitle = source?.Title ?? primary.Title;
        string sourceText = source?.Text ?? primary.Text;
        string primaryTitle = translationWithoutProvider ? "日本語訳" : primary.Title;
        string primaryText = translationWithoutProvider
            ? "翻訳サービスは未設定です。上のOCR結果を確認してください。"
            : primary.Text;
        string completionMessage;
        if (translationWithoutProvider)
        {
            completionMessage = result.Warning ?? "OCRが完了しました。";
        }
        else
        {
            string completedLabel = isTranslation ? "翻訳" : primary.Title;
            completionMessage = result.Warning is null
                ? $"{completedLabel}が完了しました。"
                : $"{completedLabel}が完了しました。注意: {result.Warning}";
        }

        return new FeatureResultPresentation(
            sourceTitle,
            sourceText,
            primaryTitle,
            primaryText,
            $"{primaryTitle}をコピー",
            completionMessage);
    }
}
