using VrcVa.Core;
using VrcVa.Windows.Rendering;

namespace VrcVa.Windows.Tests;

public sealed class FeatureResultPresentationTests
{
    [Fact]
    public void Create_PreservesTranslationLabelsAndText()
    {
        FeatureResult result = new(
            FeatureIds.Translation,
            [
                new ResultSection("source-text", "OCR結果（英語）", "Emergency exit"),
                new ResultSection(
                    "japanese-text",
                    "日本語訳",
                    "非常口",
                    ResultSectionRole.Primary),
            ]);

        FeatureResultPresentation presentation = FeatureResultPresentation.Create(result);

        Assert.Equal("OCR結果（英語）", presentation.SourceTitle);
        Assert.Equal("Emergency exit", presentation.SourceText);
        Assert.Equal("日本語訳", presentation.PrimaryTitle);
        Assert.Equal("非常口", presentation.PrimaryText);
        Assert.Equal("日本語訳をコピー", presentation.CopyButtonText);
        Assert.Equal("翻訳が完了しました。", presentation.CompletionMessage);
    }

    [Fact]
    public void Create_PreservesOcrOnlyDesktopFallback()
    {
        FeatureResult result = new(
            FeatureIds.Translation,
            [
                new ResultSection(
                    "source-text",
                    "OCR結果（翻訳未設定）",
                    "Emergency exit",
                    ResultSectionRole.Primary),
            ])
        {
            Warning = "翻訳サービスは未設定です。",
        };

        FeatureResultPresentation presentation = FeatureResultPresentation.Create(result);

        Assert.Equal("OCR結果（翻訳未設定）", presentation.SourceTitle);
        Assert.Equal("Emergency exit", presentation.SourceText);
        Assert.Equal("日本語訳", presentation.PrimaryTitle);
        Assert.Equal(
            "翻訳サービスは未設定です。上のOCR結果を確認してください。",
            presentation.PrimaryText);
        Assert.Equal("翻訳サービスは未設定です。", presentation.CompletionMessage);
    }

    [Fact]
    public void Create_UsesGenericSectionsForAnotherFeature()
    {
        FeatureResult result = new(
            new FeatureId("test.summary"),
            [
                new ResultSection("source", "読み取った内容", "Long source"),
                new ResultSection("summary", "要約", "Short result", ResultSectionRole.Primary),
            ]);

        FeatureResultPresentation presentation = FeatureResultPresentation.Create(result);

        Assert.Equal("読み取った内容", presentation.SourceTitle);
        Assert.Equal("Long source", presentation.SourceText);
        Assert.Equal("要約", presentation.PrimaryTitle);
        Assert.Equal("Short result", presentation.PrimaryText);
        Assert.Equal("要約をコピー", presentation.CopyButtonText);
        Assert.Equal("要約が完了しました。", presentation.CompletionMessage);
    }
}
