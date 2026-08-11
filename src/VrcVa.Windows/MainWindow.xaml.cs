using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Capture;
using VrcVa.Windows.Ocr;
using VrcVa.Windows.Rendering;
using VrcVa.Windows.Win32;

namespace VrcVa.Windows;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient = new();
    private readonly PrivacySafeFileLogger _logger;
    private readonly IAnalyzer _analyzer;
    private readonly IResultRenderer _renderer;
    private readonly ScanPipeline _vrChatPipeline;
    private readonly string _privacyNotice;
    private readonly string _translationStatus;
    private GlobalHotKey? _globalHotKey;
    private CancellationTokenSource? _activeScanCancellation;
    private int _uiScanRunning;
    private string? _startupWarning;

    public MainWindow()
    {
        InitializeComponent();

        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcVa",
            "logs");
        _logger = new PrivacySafeFileLogger(logDirectory);

        string providerId = Environment.GetEnvironmentVariable("VRCVA_TRANSLATION_PROVIDER")?
            .Trim()
            .ToLowerInvariant()
            ?? "none";
        ITextTranslator translator;
        try
        {
            switch (providerId)
            {
                case "none":
                    const string selectionRequired =
                        "翻訳バックエンドは未選定です。候補の評価が完了するまで外部送信しません。";
                    translator = new ConfigurationFailureTranslator(selectionRequired);
                    _privacyNotice =
                        "画像とOCRはWindows内で処理し、保存しません。翻訳バックエンド未選定のため外部送信しません。";
                    _translationStatus = "翻訳: バックエンド未選定";
                    break;
                case "openai":
                    string? apiKey = Environment.GetEnvironmentVariable("VRCVA_OPENAI_API_KEY");
                    OpenAiTranslatorOptions openAiOptions = OpenAiTranslatorOptions.FromEnvironment();
                    translator = new OpenAiTextTranslator(_httpClient, openAiOptions, apiKey);
                    bool hasApiKey = !string.IsNullOrWhiteSpace(apiKey);
                    _privacyNotice = hasApiKey
                        ? "画像はWindows内でOCRし、保存しません。翻訳時はOCRテキストだけをOpenAIへ送信します（API従量課金）。"
                        : "画像はWindows内でOCRし、保存しません。OpenAIが選択されていますが、専用APIキー未設定のため外部送信しません。";
                    _translationStatus = hasApiKey
                        ? $"翻訳API: OpenAI / {openAiOptions.Model}（従量課金）"
                        : "翻訳API: OpenAI / 専用キー未設定";
                    break;
                default:
                    throw new InvalidOperationException(
                        "VRCVA_TRANSLATION_PROVIDER must be either none or openai.");
            }
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or UriFormatException)
        {
            _startupWarning = "翻訳設定の環境変数が不正です。READMEの設定例を確認してください。";
            _privacyNotice = "翻訳設定が不正なため、外部送信は行われません。";
            _translationStatus = "翻訳: 設定エラー";
            _logger.Error(
                "startup.translation_configuration_invalid",
                Guid.Empty,
                ScanStage.Translation,
                ScanFailureCode.TranslationNotConfigured,
                exception);
            translator = new ConfigurationFailureTranslator(_startupWarning);
        }

        _analyzer = new TranslateAnalyzer(new WindowsOcrEngine(), translator);
        _renderer = new WpfResultRenderer(
            Dispatcher,
            RenderProgress,
            RenderOutcome);
        _vrChatPipeline = new ScanPipeline(
            new VrChatWindowCaptureSource(),
            _analyzer,
            _renderer,
            _logger);

        PrivacyText.Text = _privacyNotice;

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        IReadOnlyList<string> languageTags;
        try
        {
            languageTags = WindowsOcrEngine.GetAvailableLanguageTags();
        }
        catch (Exception exception)
        {
            languageTags = [];
            _logger.Error(
                "startup.ocr_language_query_failed",
                Guid.Empty,
                ScanStage.Ocr,
                ScanFailureCode.OcrUnavailable,
                exception);
        }

        string ocrInfo = languageTags.Count == 0
            ? "OCR言語: 検出できません"
            : $"OCR言語: {string.Join(", ", languageTags)}";

        StatusText.Text = _startupWarning ?? "準備完了。VRChatを表示してSCANしてください。";
        DetailText.Text = $"{ocrInfo} / {_translationStatus} / ログ: {_logger.LogDirectory}";
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs eventArgs)
    {
        try
        {
            HotKeyDefinition definition = HotKeyDefinition.FromEnvironment();
            _globalHotKey = new GlobalHotKey(
                new WindowInteropHelper(this).Handle,
                definition);
            _globalHotKey.Pressed += GlobalHotKey_Pressed;
            ScanButton.Content = $"SCAN  ({definition.DisplayText})";
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "グローバルホットキーを登録できませんでした。SCANボタンは使用できます。";
            _logger.Error(
                "startup.hotkey_registration_failed",
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
        }
    }

    private async void GlobalHotKey_Pressed(object? sender, EventArgs eventArgs)
    {
        await RunPipelineAsync(_vrChatPipeline, "global-hotkey", hideWindowBeforeCapture: false);
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        await RunPipelineAsync(
            _vrChatPipeline,
            "scan-button",
            hideWindowBeforeCapture: IsActive);
    }

    private async void ImageButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        Microsoft.Win32.OpenFileDialog dialog = new()
        {
            Title = "OCR診断に使う画像を選択",
            Filter = "画像 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp",
            CheckFileExists = true,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        ScanPipeline imagePipeline = new(
            new ImageFileCaptureSource(dialog.FileName),
            _analyzer,
            _renderer,
            _logger);
        await RunPipelineAsync(imagePipeline, "explicit-image", hideWindowBeforeCapture: false);
    }

    private void CancelButton_Click(object sender, RoutedEventArgs eventArgs) =>
        _activeScanCancellation?.Cancel();

    private void CopyButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (string.IsNullOrWhiteSpace(TranslationTextBox.Text))
        {
            StatusText.Text = "コピーできる日本語訳がまだありません。";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(TranslationTextBox.Text);
            StatusText.Text = "日本語訳をクリップボードへコピーしました。";
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            StatusText.Text = "クリップボードへコピーできませんでした。";
            _logger.Error(
                "ui.clipboard_failed",
                Guid.Empty,
                ScanStage.Rendering,
                ScanFailureCode.Unexpected,
                exception);
        }
    }

    private void TopmostCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            Topmost = checkBox.IsChecked == true;
        }
    }

    private async Task RunPipelineAsync(
        ScanPipeline pipeline,
        string triggerName,
        bool hideWindowBeforeCapture)
    {
        if (Interlocked.CompareExchange(ref _uiScanRunning, 1, 0) != 0)
        {
            StatusText.Text = "前のSCANを処理中です。";
            return;
        }

        bool hidden = false;
        _activeScanCancellation = new CancellationTokenSource();
        SetScanControls(isRunning: true);

        try
        {
            if (hideWindowBeforeCapture)
            {
                Hide();
                hidden = true;
                await Task.Delay(180, _activeScanCancellation.Token);
            }

            await pipeline.RunAsync(
                ScanRequest.Create(triggerName),
                _activeScanCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "SCANをキャンセルしました。";
        }
        finally
        {
            if (hidden)
            {
                Show();
                Activate();
            }

            _activeScanCancellation.Dispose();
            _activeScanCancellation = null;
            SetScanControls(isRunning: false);
            Volatile.Write(ref _uiScanRunning, 0);
        }
    }

    private void SetScanControls(bool isRunning)
    {
        ScanButton.IsEnabled = !isRunning;
        ImageButton.IsEnabled = !isRunning;
        CancelButton.IsEnabled = isRunning;
    }

    private void RenderProgress(ScanProgress progress)
    {
        StatusText.Text = progress.Message;
        DetailText.Text = $"段階: {progress.Stage} / {progress.Elapsed.TotalSeconds:0.0}秒 / 相関ID: {progress.CorrelationId:D}";
    }

    private void RenderOutcome(ScanOutcome outcome)
    {
        if (!outcome.IsSuccess || outcome.Result is null)
        {
            StatusText.Text = outcome.Failure?.Message ?? "SCANに失敗しました。";
            DetailText.Text = $"失敗: {outcome.Failure?.Code} / 段階: {outcome.Failure?.Stage} / 相関ID: {outcome.CorrelationId:D}";
            return;
        }

        SourceTextBox.Text = outcome.Result.SourceText;
        TranslationTextBox.Text = outcome.Result.JapaneseText;
        StatusText.Text = outcome.Result.Warning is null
            ? "翻訳が完了しました。"
            : $"翻訳が完了しました。注意: {outcome.Result.Warning}";
        DetailText.Text =
            $"合計 {outcome.TotalDuration.TotalSeconds:0.0}秒 "
            + $"(OCR {outcome.Result.OcrDuration.TotalSeconds:0.0}秒 / 翻訳 {outcome.Result.TranslationDuration.TotalSeconds:0.0}秒) "
            + $"/ OCR {outcome.Result.OcrLanguage} / {outcome.Result.TranslationProvider} {outcome.Result.TranslationModel} "
            + $"/ 相関ID: {outcome.CorrelationId:D}";
    }

    private void MainWindow_Closed(object? sender, EventArgs eventArgs)
    {
        _activeScanCancellation?.Cancel();
        _activeScanCancellation?.Dispose();
        _globalHotKey?.Dispose();
        _httpClient.Dispose();
    }

    private sealed class ConfigurationFailureTranslator(string message) : ITextTranslator
    {
        public Task<TranslationOutput> TranslateToJapaneseAsync(
            string sourceText,
            CancellationToken cancellationToken) =>
            Task.FromException<TranslationOutput>(new ScanException(
                ScanFailureCode.TranslationNotConfigured,
                ScanStage.Translation,
                message));
    }
}
