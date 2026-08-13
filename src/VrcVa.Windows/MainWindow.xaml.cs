using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Capture;
using VrcVa.Windows.Ocr;
using VrcVa.Windows.Osc;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Rendering;
using VrcVa.Windows.Win32;

namespace VrcVa.Windows;

public partial class MainWindow : Window
{
    private const int ScanHotKeyIdentifier = 0x565243;
    private const int PanelInteractionHotKeyIdentifier = ScanHotKeyIdentifier + 1;
    private const int ModelToggleHotKeyIdentifier = ScanHotKeyIdentifier + 2;
    private readonly HttpClient _httpClient = new();
    private readonly CancellationTokenSource _windowLifetimeCancellation = new();
    private readonly PrivacySafeFileLogger _logger;
    private readonly IAnalyzer _analyzer;
    private readonly IResultRenderer _renderer;
    private readonly IXsOverlayNotificationSink _xsOverlayNotificationSink;
    private readonly SteamVrResultPanel _steamVrResultPanel;
    private readonly ScanPipeline _vrChatPipeline;
    private readonly string _privacyNotice;
    private readonly OpenAiTextTranslator? _openAiTranslator;
    private readonly bool _hasOpenAiApiKey;
    private readonly string _captureConfiguration;
    private readonly OscTriggerOptions? _oscTriggerOptions;
    private string _translationStatus;
    private string _oscStatus = "OSCトリガー: 無効";
    private string _ocrInfo = "OCR言語: 確認中";
    private bool _modelSelectorInitializing = true;
    private GlobalHotKey? _globalHotKey;
    private GlobalHotKey? _panelInteractionHotKey;
    private GlobalHotKey? _modelToggleHotKey;
    private OscTriggerService? _oscTriggerService;
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

        try
        {
            _oscTriggerOptions = OscTriggerOptions.FromEnvironment();
            _oscStatus = _oscTriggerOptions.Enabled
                ? "OSCトリガー: 起動待ち"
                : "OSCトリガー: 無効";
        }
        catch (InvalidOperationException exception)
        {
            _startupWarning = "OSCトリガー設定が不正なため、OSCを無効にしました。READMEの設定例を確認してください。";
            _logger.Error(
                "startup.osc_configuration_invalid",
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
        }

        string providerId = Environment.GetEnvironmentVariable("VRCVA_TRANSLATION_PROVIDER")?
            .Trim()
            .ToLowerInvariant()
            ?? "none";
        IAnalyzer analyzer;
        WindowsOcrEngine windowsOcrEngine = new();
        IOcrEngine ocrEngine = new AdaptiveOcrEngine(
            windowsOcrEngine,
            new WindowsOcrRegionSource());
        try
        {
            switch (providerId)
            {
                case "none":
                    analyzer = new OcrAnalyzer(ocrEngine);
                    _privacyNotice =
                        "画像とOCRはWindows内で処理し、保存しません。OCR結果は表示しますが、翻訳未設定のため外部送信しません。";
                    _translationStatus = "OCRのみ / 翻訳バックエンド未選定";
                    break;
                case "openai":
                    string? apiKey = Environment.GetEnvironmentVariable("VRCVA_OPENAI_API_KEY");
                    OpenAiTranslatorOptions openAiOptions = OpenAiTranslatorOptions.FromEnvironment();
                    _openAiTranslator = new OpenAiTextTranslator(_httpClient, openAiOptions, apiKey);
                    analyzer = new TranslateAnalyzer(ocrEngine, _openAiTranslator);
                    _hasOpenAiApiKey = !string.IsNullOrWhiteSpace(apiKey);
                    _privacyNotice = _hasOpenAiApiKey
                        ? "画像はWindows内でOCRし、保存しません。翻訳時はOCRテキストだけをOpenAIへ送信します（API従量課金）。"
                        : "画像はWindows内でOCRし、保存しません。OpenAIが選択されていますが、専用APIキー未設定のため外部送信しません。";
                    _translationStatus = _hasOpenAiApiKey
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
            analyzer = new TranslateAnalyzer(
                ocrEngine,
                new ConfigurationFailureTranslator(_startupWarning));
        }

        _analyzer = analyzer;
        _xsOverlayNotificationSink = new XsOverlayUdpNotificationSink();
        _steamVrResultPanel = new SteamVrResultPanel(Dispatcher);
        OpenVrEyeCaptureOptions eyeOptions = OpenVrEyeCaptureOptions.FromEnvironment(
            out string? eyeConfigurationWarning);
        if (eyeConfigurationWarning is not null)
        {
            _startupWarning = string.IsNullOrWhiteSpace(_startupWarning)
                ? eyeConfigurationWarning
                : $"{_startupWarning} {eyeConfigurationWarning}";
        }

        _captureConfiguration = eyeOptions.Eye == OpenVrEye.Left
            ? "キャプチャ: SteamVR左眼を優先 / ウィンドウ自動フォールバック"
            : "キャプチャ: SteamVR右眼を優先 / ウィンドウ自動フォールバック";
        _renderer = new CompositeResultRenderer(
            new WpfResultRenderer(
                Dispatcher,
                RenderProgress,
                RenderOutcome),
            new SteamVrOverlayResultRenderer(
                Dispatcher,
                _steamVrResultPanel,
                _xsOverlayNotificationSink,
                _logger),
            new XsOverlayNotificationRenderer(_xsOverlayNotificationSink));
        _vrChatPipeline = new ScanPipeline(
            new FallbackCaptureSource(
                new OpenVrEyeCaptureSource(Dispatcher, _steamVrResultPanel, eyeOptions),
                new VrChatWindowCaptureSource()),
            _analyzer,
            _renderer,
            _logger);

        InitializeModelSelector();
        PrivacyText.Text = _privacyNotice;

        Loaded += MainWindow_Loaded;
        SourceInitialized += MainWindow_SourceInitialized;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs eventArgs)
    {
        IReadOnlyList<string> languageTags;
        string? selectedRecognizerTag;
        try
        {
            languageTags = WindowsOcrEngine.GetAvailableLanguageTags();
            selectedRecognizerTag = WindowsOcrEngine.GetSelectedRecognizerLanguageTag();
        }
        catch (Exception exception)
        {
            languageTags = [];
            selectedRecognizerTag = null;
            _logger.Error(
                "startup.ocr_language_query_failed",
                Guid.Empty,
                ScanStage.Ocr,
                ScanFailureCode.OcrUnavailable,
                exception);
        }

        _ocrInfo = languageTags.Count == 0
            ? "OCR言語: 検出できません"
            : $"OCR言語: {string.Join(", ", languageTags)}";

        bool hasEnglishRecognizer = WindowsOcrEngine.HasEnglishRecognizer(languageTags);
        bool usesEnglishRecognizer = selectedRecognizerTag is not null
            && WindowsOcrEngine.HasEnglishRecognizer([selectedRecognizerTag]);
        string selectedRecognizerDisplay = selectedRecognizerTag ?? "検出できません";
        if (!usesEnglishRecognizer && selectedRecognizerTag is not null)
        {
            selectedRecognizerDisplay += "（英語用の代替）";
        }

        string availableRecognizerDisplay = languageTags.Count == 0
            ? "検出できません"
            : string.Join(", ", languageTags);
        OcrLanguageStatusText.Text =
            $"使用するOCR認識器: {selectedRecognizerDisplay} / 利用可能: {availableRecognizerDisplay}";

        bool englishOcrReady = hasEnglishRecognizer && usesEnglishRecognizer;
        OcrLanguageWarningBorder.Visibility = englishOcrReady
            ? Visibility.Collapsed
            : Visibility.Visible;
        OcrLanguageWarningText.Text = languageTags.Count == 0
            ? "Windows OCRの認識器を確認できませんでした。"
                + WindowsOcrEngine.EnglishRecognizerInstallationSteps
            : WindowsOcrEngine.EnglishRecognizerMissingWarning;

        StatusText.Text = _startupWarning
            ?? (englishOcrReady
                ? "準備完了。VRChatを表示してSCANしてください。"
                : "英語OCRが未導入です。上の警告を確認してください。SCANは引き続き使用できます。");
        UpdateEnvironmentDetails();

        if (_oscTriggerOptions?.Enabled == true)
        {
            await StartOscTriggerAsync(_oscTriggerOptions);
        }
    }

    private async Task StartOscTriggerAsync(OscTriggerOptions options)
    {
        try
        {
            _oscTriggerService = await OscTriggerService.StartAsync(
                options,
                _windowLifetimeCancellation.Token);
            _oscTriggerService.Triggered += OscTriggerService_Triggered;
            _oscTriggerService.Faulted += OscTriggerService_Faulted;
            _oscStatus = $"OSCトリガー: 有効 / 動的ポート {_oscTriggerService.OscPort}";
            _logger.Info(
                "startup.osc_trigger_ready",
                Guid.Empty,
                ScanStage.Trigger,
                numericMetrics: new Dictionary<string, long>
                {
                    ["oscPort"] = _oscTriggerService.OscPort,
                    ["queryPort"] = _oscTriggerService.QueryPort,
                });
            UpdateEnvironmentDetails();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or COMException
                or System.Net.Sockets.SocketException)
        {
            _oscStatus = "OSCトリガー: 起動失敗 / ホットキーは使用可能";
            StatusText.Text = "OSCトリガーを開始できませんでした。ホットキーまたはSCANボタンは使用できます。";
            _logger.Error(
                "startup.osc_trigger_failed",
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
            UpdateEnvironmentDetails();
        }
        catch (OperationCanceledException) when (_windowLifetimeCancellation.IsCancellationRequested)
        {
            // Window shutdown cancelled startup. The service factory disposes partial sockets.
        }
    }

    private async void OscTriggerService_Triggered(object? sender, EventArgs eventArgs)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            await Dispatcher
                .InvokeAsync(() => RunPipelineAsync(_vrChatPipeline, "vrchat-osc"))
                .Task
                .Unwrap();
        }
        catch (Exception exception) when (
            exception is TaskCanceledException or InvalidOperationException)
        {
            _logger.Error(
                "osc.trigger_dispatch_failed",
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
        }
    }

    private void OscTriggerService_Faulted(object? sender, Exception exception) =>
        _logger.Error(
            "osc.trigger_listener_failed",
            Guid.Empty,
            ScanStage.Trigger,
            ScanFailureCode.Unexpected,
            exception);

    private void MainWindow_SourceInitialized(object? sender, EventArgs eventArgs)
    {
        try
        {
            HotKeyDefinition definition = HotKeyDefinition.FromEnvironment();
            _globalHotKey = new GlobalHotKey(
                new WindowInteropHelper(this).Handle,
                ScanHotKeyIdentifier,
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

        try
        {
            HotKeyDefinition definition = HotKeyDefinition.FromEnvironment(
                "VRCVA_PANEL_INTERACTION_HOTKEY",
                "Ctrl+Shift+I");
            _panelInteractionHotKey = new GlobalHotKey(
                new WindowInteropHelper(this).Handle,
                PanelInteractionHotKeyIdentifier,
                definition);
            _panelInteractionHotKey.Pressed += PanelInteractionHotKey_Pressed;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "結果パネル操作の切替ホットキーを登録できませんでした。";
            _logger.Error(
                "startup.panel_interaction_hotkey_registration_failed",
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
        }

        if (_openAiTranslator is null)
        {
            return;
        }

        try
        {
            HotKeyDefinition definition = HotKeyDefinition.FromEnvironment(
                "VRCVA_MODEL_TOGGLE_HOTKEY",
                "Ctrl+Shift+G");
            _modelToggleHotKey = new GlobalHotKey(
                new WindowInteropHelper(this).Handle,
                ModelToggleHotKeyIdentifier,
                definition);
            _modelToggleHotKey.Pressed += ModelToggleHotKey_Pressed;
            TranslationModelHintText.Text = $"次回から反映 / 切替 {definition.DisplayText}";
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or System.ComponentModel.Win32Exception)
        {
            StatusText.Text = "モデル切替ホットキーを登録できませんでした。画面の選択欄は使用できます。";
            _logger.Error(
                "startup.model_toggle_hotkey_registration_failed",
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
        }
    }

    private async void GlobalHotKey_Pressed(object? sender, EventArgs eventArgs)
    {
        await RunPipelineAsync(_vrChatPipeline, "global-hotkey");
    }

    private async void ModelToggleHotKey_Pressed(object? sender, EventArgs eventArgs)
    {
        if (_uiScanRunning != 0)
        {
            await SendXsOverlayStatusAsync(
                "モデル切替待ち",
                "SCAN完了後にもう一度切り替えてください。",
                XsOverlayNotificationKind.Result);
            return;
        }

        if (TranslationModelComboBox.Items.Count == 0)
        {
            return;
        }

        int nextIndex = (TranslationModelComboBox.SelectedIndex + 1)
            % TranslationModelComboBox.Items.Count;
        TranslationModelComboBox.SelectedIndex = nextIndex;
        if (TranslationModelComboBox.SelectedItem is TranslationModelChoice choice)
        {
            await SendXsOverlayStatusAsync(
                "翻訳モデル切替",
                $"次回のSCAN: {choice.DisplayName}",
                XsOverlayNotificationKind.Result);
        }
    }

    private async void PanelInteractionHotKey_Pressed(object? sender, EventArgs eventArgs)
    {
        try
        {
            SteamVrPanelInteractionChange change = _steamVrResultPanel.ToggleInteraction();
            (string title, string content) = change switch
            {
                SteamVrPanelInteractionChange.Enabled => (
                    "結果パネル操作 ON",
                    "レーザーでスクロール・閉じる操作ができます。もう一度押すとVRChat操作へ戻ります。"),
                SteamVrPanelInteractionChange.Disabled => (
                    "結果パネル操作 OFF",
                    "VRChat操作へ戻りました。結果パネルは表示を続けます。"),
                _ => (
                    "結果パネルなし",
                    "先にSCANして結果パネルを表示してください。"),
            };
            await SendXsOverlayStatusAsync(title, content, XsOverlayNotificationKind.Result);
        }
        catch (Exception exception)
        {
            _logger.Error(
                "rendering.steamvr_overlay_interaction_toggle_failed",
                Guid.Empty,
                ScanStage.Rendering,
                ScanFailureCode.Unexpected,
                exception);
            await SendXsOverlayStatusAsync(
                "結果パネル操作エラー",
                "パネルを閉じました。SteamVRを確認して再度SCANしてください。",
                XsOverlayNotificationKind.Error);
        }
    }

    private async void ScanButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        await RunPipelineAsync(_vrChatPipeline, "scan-button");
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
        await RunPipelineAsync(imagePipeline, "explicit-image");
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

    private void TranslationModelComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        if (_modelSelectorInitializing
            || _openAiTranslator is null
            || TranslationModelComboBox.SelectedItem is not TranslationModelChoice choice)
        {
            return;
        }

        _openAiTranslator.SelectModel(choice.ModelId);
        _translationStatus = _hasOpenAiApiKey
            ? $"翻訳API: OpenAI / {choice.ModelId}（従量課金）"
            : $"翻訳API: OpenAI / {choice.ModelId} / 専用キー未設定";
        StatusText.Text = $"翻訳モデルを {choice.DisplayName} に変更しました。次回のSCANから使います。";
        UpdateEnvironmentDetails();
    }

    private async Task RunPipelineAsync(
        ScanPipeline pipeline,
        string triggerName)
    {
        if (Interlocked.CompareExchange(ref _uiScanRunning, 1, 0) != 0)
        {
            StatusText.Text = "前のSCANを処理中です。";
            return;
        }

        _activeScanCancellation = new CancellationTokenSource();
        SetScanControls(isRunning: true);

        try
        {
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
        TranslationModelComboBox.IsEnabled = !isRunning && _openAiTranslator is not null;
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
        bool ocrOnly = string.IsNullOrWhiteSpace(outcome.Result.JapaneseText);
        TranslationTextBox.Text = ocrOnly
            ? "翻訳サービスは未設定です。上のOCR結果を確認してください。"
            : outcome.Result.JapaneseText;
        StatusText.Text = ocrOnly
            ? outcome.Result.Warning ?? "OCRが完了しました。"
            : outcome.Result.Warning is null
                ? "翻訳が完了しました。"
                : $"翻訳が完了しました。注意: {outcome.Result.Warning}";
        DetailText.Text =
            $"合計 {outcome.TotalDuration.TotalSeconds:0.0}秒 "
            + $"(OCR {outcome.Result.OcrDuration.TotalSeconds:0.0}秒 / 翻訳 {outcome.Result.TranslationDuration.TotalSeconds:0.0}秒) "
            + $"/ 取得 {CaptureSourceDisplayName.Get(outcome.Result.CaptureSourceKind)} "
            + $"/ OCR {outcome.Result.OcrLanguage} / {outcome.Result.TranslationProvider} {outcome.Result.TranslationModel} "
            + $"/ 相関ID: {outcome.CorrelationId:D}";
    }

    private void MainWindow_Closed(object? sender, EventArgs eventArgs)
    {
        _windowLifetimeCancellation.Cancel();
        _activeScanCancellation?.Cancel();
        _activeScanCancellation?.Dispose();
        _globalHotKey?.Dispose();
        _panelInteractionHotKey?.Dispose();
        _modelToggleHotKey?.Dispose();
        if (_oscTriggerService is not null)
        {
            _oscTriggerService.Triggered -= OscTriggerService_Triggered;
            _oscTriggerService.Faulted -= OscTriggerService_Faulted;
            _oscTriggerService.Dispose();
        }

        _steamVrResultPanel.Dispose();
        _httpClient.Dispose();
        _windowLifetimeCancellation.Dispose();
    }

    private async Task SendXsOverlayStatusAsync(
        string title,
        string content,
        XsOverlayNotificationKind kind)
    {
        try
        {
            await _xsOverlayNotificationSink.SendAsync(
                title,
                content,
                kind,
                CancellationToken.None);
        }
        catch (Exception exception) when (
            exception is System.Net.Sockets.SocketException
                or InvalidOperationException)
        {
            _logger.Error(
                "rendering.xsoverlay_notification_failed",
                Guid.Empty,
                ScanStage.Rendering,
                ScanFailureCode.Unexpected,
                exception);
        }
    }

    private void InitializeModelSelector()
    {
        List<TranslationModelChoice> choices =
        [
            new("低料金 — GPT-5.4 nano", OpenAiTranslatorOptions.BudgetModel),
            new("標準 — GPT-5.6 Luna", OpenAiTranslatorOptions.QualityModel),
        ];

        string selectedModel = _openAiTranslator?.Model ?? OpenAiTranslatorOptions.DefaultModel;
        TranslationModelChoice? selectedChoice = choices.FirstOrDefault(
            choice => string.Equals(choice.ModelId, selectedModel, StringComparison.Ordinal));
        if (selectedChoice is null)
        {
            selectedChoice = new($"環境設定 — {selectedModel}", selectedModel);
            choices.Add(selectedChoice);
        }

        TranslationModelComboBox.ItemsSource = choices;
        TranslationModelComboBox.SelectedItem = selectedChoice;
        TranslationModelComboBox.IsEnabled = _openAiTranslator is not null;
        TranslationModelHintText.Text = _openAiTranslator is null
            ? "OpenAIを有効にした場合に選択できます"
            : "次回のSCANから反映（従量課金）";
        _modelSelectorInitializing = false;
    }

    private void UpdateEnvironmentDetails() =>
        DetailText.Text =
            $"{_captureConfiguration} / {_ocrInfo} / {_translationStatus} / {_oscStatus} / ログ: {_logger.LogDirectory}";

    private sealed record TranslationModelChoice(string DisplayName, string ModelId);

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
