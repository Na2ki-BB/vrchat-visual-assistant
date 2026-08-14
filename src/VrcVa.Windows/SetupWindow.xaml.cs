using System.Diagnostics;
using System.Windows;

namespace VrcVa.Windows;

internal sealed record SetupWizardResult(
    bool SteamVrAutoLaunchEnabled,
    string? OpenAiApiKey);

public partial class SetupWindow : Window
{
    private const int LastStepIndex = 2;
    private int _stepIndex;

    internal SetupWindow(
        bool steamVrAutoLaunchEnabled,
        bool englishOcrReady,
        string ocrReadinessDetail,
        bool hasStoredApiKey)
    {
        InitializeComponent();
        SteamVrAutoLaunchCheckBox.IsChecked = steamVrAutoLaunchEnabled;
        OcrReadinessTitleText.Text = englishOcrReady
            ? "英語OCRは準備できています"
            : "英語OCRが見つかりません";
        OcrReadinessTitleText.Foreground = englishOcrReady
            ? System.Windows.Media.Brushes.LightGreen
            : System.Windows.Media.Brushes.LightCoral;
        OcrReadinessDetailText.Text = ocrReadinessDetail;
        ExistingApiKeyStatusText.Text = hasStoredApiKey
            ? "APIキーはすでにWindows資格情報マネージャーへ保存済みです。変更しない場合は空欄のまま完了してください。"
            : "APIキーは未登録です。翻訳を後から設定することもできます。";
        UpdateStep();
    }

    internal SetupWizardResult? Result { get; private set; }

    private void BackButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_stepIndex > 0)
        {
            _stepIndex--;
            UpdateStep();
        }
    }

    private void NextButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        SetupErrorText.Text = string.Empty;
        if (_stepIndex < LastStepIndex)
        {
            _stepIndex++;
            UpdateStep();
            return;
        }

        string apiKey = OpenAiApiKeyPasswordBox.Password.Trim();
        Result = new SetupWizardResult(
            SteamVrAutoLaunchCheckBox.IsChecked == true,
            string.IsNullOrWhiteSpace(apiKey) ? null : apiKey);
        OpenAiApiKeyPasswordBox.Clear();
        DialogResult = true;
    }

    private void OpenLanguageSettingsButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:regionlanguage")
            {
                UseShellExecute = true,
            });
            SetupErrorText.Text = string.Empty;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            SetupErrorText.Text =
                "Windowsの言語設定を開けませんでした。Windows設定から［時刻と言語］→［言語と地域］を開いてください。";
        }
    }

    private void UpdateStep()
    {
        SteamVrStepPanel.Visibility = _stepIndex == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        OcrStepPanel.Visibility = _stepIndex == 1
            ? Visibility.Visible
            : Visibility.Collapsed;
        OpenAiStepPanel.Visibility = _stepIndex == 2
            ? Visibility.Visible
            : Visibility.Collapsed;
        StepIndicatorText.Text = _stepIndex switch
        {
            0 => "1 / 3  起動方法",
            1 => "2 / 3  英語OCR",
            _ => "3 / 3  日本語翻訳（任意）",
        };
        BackButton.IsEnabled = _stepIndex > 0;
        NextButton.Content = _stepIndex == LastStepIndex ? "設定を完了" : "次へ";
    }
}
