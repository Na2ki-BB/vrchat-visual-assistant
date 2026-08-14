using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using VrcVa.Core;
using VrcVa.Infrastructure;
using VrcVa.Windows.Capture;
using VrcVa.Windows.Ocr;
using VrcVa.Windows.OpenVr;
using VrcVa.Windows.Osc;
using VrcVa.Windows.Rendering;
using VrcVa.Windows.Security;
using VrcVa.Windows.Settings;
using VrcVa.Windows.Startup;
using VrcVa.Windows.Translation;
using VrcVa.Windows.Win32;

namespace VrcVa.Windows;

public partial class MainWindow : Window
{
    private const int ScanHotKeyIdentifier = 0x565243;
    private const int ModelToggleHotKeyIdentifier = ScanHotKeyIdentifier + 1;
    private const double WindowWorkAreaMargin = 16;
    private static readonly TimeSpan OscActionMenuSettleDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CaptureNoticeDuration = TimeSpan.FromMilliseconds(250);
    private readonly HttpClient _httpClient = new();
    private readonly CancellationTokenSource _windowLifetimeCancellation = new();
    private readonly WindowsCredentialStore _openAiCredentialStore = new();
    private readonly VrcVaSettingsStore _settingsStore = new();
    private readonly SteamVrAutoLaunchRegistration _steamVrAutoLaunchRegistration = new();
    private readonly TranslationRequestQuota _translationRequestQuota = new();
    private readonly PrivacySafeFileLogger _logger;
    private readonly TranslationRuntimeFactory _translationRuntimeFactory;
    private readonly ReloadableAnalyzer _analyzer;
    private readonly IResultRenderer _renderer;
    private readonly IXsOverlayNotificationSink _xsOverlayNotificationSink;
    private readonly SteamVrResultPanel _steamVrResultPanel;
    private readonly ScanPipeline _vrChatPipeline;
    private readonly string _captureConfiguration;
    private readonly OscTriggerOptions? _oscTriggerOptions;
    private VrcVaSettings _settings = VrcVaSettings.Default;
    private ResultPanelPlacement _resultPanelPlacement = ResultPanelPlacement.Default;
    private string _translationStatus;
    private string _oscStatus = "OSCトリガー: 無効";
    private string _steamVrAutoLaunchStatus = "SteamVR自動起動: 初期設定待ち";
    private string _ocrInfo = "OCR言語: 確認中";
    private bool _modelSelectorInitializing = true;
    private bool _placementControlsInitializing = true;
    private bool _placementCalibrationActive;
    private bool _setupWindowOpen;
    private bool _englishOcrReady;
    private string _ocrReadinessDetail = "OCR認識器を確認中です。";
    private GlobalHotKey? _globalHotKey;
    private GlobalHotKey? _modelToggleHotKey;
    private string? _modelToggleHotKeyDisplayText;
    private OscTriggerService? _oscTriggerService;
    private CancellationTokenSource? _activeScanCancellation;
    private int _uiScanRunning;
    private string? _startupWarning;

    public MainWindow()
    {
        InitializeComponent();
        FitWindowToWorkingArea();

        string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VrcVa",
            "logs");
        _logger = new PrivacySafeFileLogger(logDirectory);

        try
        {
            _settings = _settingsStore.Load();
            _resultPanelPlacement = _settings.ResultPanel;
            _steamVrAutoLaunchStatus = _settings.Onboarding.IsCompleted
                ? "SteamVR自動起動: 確認待ち"
                : "SteamVR自動起動: 初期設定待ち";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or System.Text.Json.JsonException)
        {
            _startupWarning = "保存済み設定を読み込めなかったため、初期設定を使います。設定欄から保存し直せます。";
            _logger.Error(
                "startup.result_panel_placement_load_failed",
                Guid.Empty,
                ScanStage.Rendering,
                ScanFailureCode.Unexpected,
                exception);
        }

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

        WindowsOcrEngine windowsOcrEngine = new();
        IOcrEngine ocrEngine = new AdaptiveOcrEngine(
            windowsOcrEngine,
            new WindowsOcrRegionSource());
        _translationRuntimeFactory = new TranslationRuntimeFactory(
            _httpClient,
            ocrEngine,
            _translationRequestQuota);
        TranslationRuntime initialTranslationRuntime = CreateTranslationRuntime(
            preferredModel: null,
            isStartup: true);
        _analyzer = new ReloadableAnalyzer(initialTranslationRuntime);
        _translationStatus = initialTranslationRuntime.Status;
        _xsOverlayNotificationSink = new XsOverlayUdpNotificationSink();
        _steamVrResultPanel = new SteamVrResultPanel(
            Dispatcher,
            _resultPanelPlacement,
            _logger,
            _settings.WristLauncher);
        _steamVrResultPanel.PlacementFallback += SteamVrResultPanel_PlacementFallback;
        _steamVrResultPanel.ScanRequested += SteamVrResultPanel_ScanRequested;
        _steamVrResultPanel.PlacementCalibrationFinished +=
            SteamVrResultPanel_PlacementCalibrationFinished;
        _steamVrResultPanel.WristLauncherPlacementCalibrationStarted +=
            SteamVrResultPanel_WristLauncherPlacementCalibrationStarted;
        _steamVrResultPanel.WristLauncherPlacementCalibrationFinished +=
            SteamVrResultPanel_WristLauncherPlacementCalibrationFinished;
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
        InitializeResultPanelPlacementControls();
        UpdateTranslationSettingsUi(initialTranslationRuntime);

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

        _englishOcrReady = hasEnglishRecognizer && usesEnglishRecognizer;
        _ocrReadinessDetail =
            $"使用する認識器: {selectedRecognizerDisplay} / 利用可能: {availableRecognizerDisplay}";
        OcrLanguageWarningBorder.Visibility = _englishOcrReady
            ? Visibility.Collapsed
            : Visibility.Visible;
        OcrLanguageWarningText.Text = languageTags.Count == 0
            ? "Windows OCRの認識器を確認できませんでした。"
                + WindowsOcrEngine.EnglishRecognizerInstallationSteps
            : WindowsOcrEngine.EnglishRecognizerMissingWarning;

        StatusText.Text = _startupWarning
            ?? (_englishOcrReady
                ? "準備完了。VRChatを表示してSCANしてください。"
                : "英語OCRが未導入です。上の警告を確認してください。SCANは引き続き使用できます。");
        UpdateEnvironmentDetails();

        try
        {
            _ = _steamVrResultPanel.PreloadStatusAtlas();
            _ = _steamVrResultPanel.TryStartWristLauncher();
        }
        catch (Exception exception)
        {
            _logger.Error(
                "startup.steamvr_status_preload_failed",
                Guid.Empty,
                ScanStage.Rendering,
                ScanFailureCode.Unexpected,
                exception);
        }

        if (_oscTriggerOptions?.Enabled == true)
        {
            await StartOscTriggerAsync(_oscTriggerOptions);
        }

        if (!_settings.Onboarding.IsCompleted)
        {
            if (WindowState == WindowState.Minimized)
            {
                WindowState = WindowState.Normal;
                Activate();
            }

            ShowSetupWizard();
        }
        else
        {
            SteamVrAutoLaunchResult autoLaunchResult = ApplySteamVrAutoLaunchPreference();
            if (!autoLaunchResult.Succeeded)
            {
                StatusText.Text = autoLaunchResult.UserMessage;
            }
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
                .InvokeAsync(() => RunPipelineAsync(
                    _vrChatPipeline,
                    "vrchat-osc",
                    OscActionMenuSettleDelay))
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

    private async void SteamVrResultPanel_ScanRequested(object? sender, EventArgs eventArgs)
    {
        try
        {
            await RunPipelineAsync(_vrChatPipeline, "wrist-launcher");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _steamVrResultPanel.ReturnToLauncher();
            _logger.Error(
                "wrist_launcher.trigger_failed",
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

        UpdateModelToggleHotKeyRegistration();
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

        if (_analyzer.Current.OpenAiTranslator is null
            || TranslationModelComboBox.Items.Count == 0)
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
        string resultTitle = PrimaryResultGroupBox.Header as string ?? "結果";
        if (string.IsNullOrWhiteSpace(TranslationTextBox.Text))
        {
            StatusText.Text = $"コピーできる{resultTitle}がまだありません。";
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(TranslationTextBox.Text);
            StatusText.Text = $"{resultTitle}をクリップボードへコピーしました。";
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

    private void OpenSetupButton_Click(object sender, RoutedEventArgs eventArgs) =>
        ShowSetupWizard();

    private void ShowSetupWizard()
    {
        if (_setupWindowOpen)
        {
            return;
        }

        if (Volatile.Read(ref _uiScanRunning) != 0 || _placementCalibrationActive)
        {
            StatusText.Text = "SCANまたはVR位置調整が終わってから初期設定を開いてください。";
            return;
        }

        _setupWindowOpen = true;
        OpenSetupButton.IsEnabled = false;
        try
        {
            TranslationRuntime currentRuntime = _analyzer.Current;
            SetupWindow setupWindow = new(
                _settings.Onboarding.SteamVrAutoLaunchEnabled,
                _englishOcrReady,
                _ocrReadinessDetail,
                currentRuntime.HasStoredApiKey)
            {
                Owner = this,
            };
            if (setupWindow.ShowDialog() != true || setupWindow.Result is null)
            {
                return;
            }

            SetupWizardResult result = setupWindow.Result;
            bool settingsSaved = TrySaveOnboardingSettings(
                new VrcVaOnboardingSettings(
                    IsCompleted: true,
                    SteamVrAutoLaunchEnabled: result.SteamVrAutoLaunchEnabled));
            SteamVrAutoLaunchResult? autoLaunchResult = settingsSaved
                ? ApplySteamVrAutoLaunchPreference()
                : null;
            bool apiKeySaved = true;
            bool clipboardCleared = true;
            if (!string.IsNullOrWhiteSpace(result.OpenAiApiKey))
            {
                apiKeySaved = TrySaveOpenAiApiKey(result.OpenAiApiKey, out _);
                clipboardCleared = TryClearClipboard();
            }

            string status = settingsSaved && apiKeySaved && autoLaunchResult is not null
                ? $"初期設定を保存しました。{autoLaunchResult.UserMessage}"
                : "一部の初期設定を保存できませんでした。画面の状態を確認して、もう一度お試しください。";
            StatusText.Text = clipboardCleared
                ? status
                : $"{status} クリップボードを消去できませんでした。別の文字列をコピーしてAPIキーを上書きしてください。";
        }
        finally
        {
            _setupWindowOpen = false;
            OpenSetupButton.IsEnabled = Volatile.Read(ref _uiScanRunning) == 0;
        }
    }

    private bool TrySaveOnboardingSettings(VrcVaOnboardingSettings onboarding)
    {
        try
        {
            VrcVaSettings updatedSettings = _settings with { Onboarding = onboarding };
            _settingsStore.Save(updatedSettings);
            _settings = updatedSettings;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or InvalidDataException)
        {
            _logger.Error(
                "ui.onboarding_settings_save_failed",
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
            return false;
        }
    }

    private SteamVrAutoLaunchResult ApplySteamVrAutoLaunchPreference()
    {
        bool enabled = _settings.Onboarding.SteamVrAutoLaunchEnabled;
        SteamVrAutoLaunchResult result;
        try
        {
            result = _steamVrAutoLaunchRegistration.Apply(enabled);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or ArgumentException
                or ExternalException
                or NotSupportedException)
        {
            result = SteamVrAutoLaunchResult.Failed();
            _logger.Error(
                "startup.steamvr_auto_launch_failed",
                Guid.Empty,
                ScanStage.Trigger,
                ScanFailureCode.Unexpected,
                exception);
        }

        _steamVrAutoLaunchStatus = result.Status switch
        {
            SteamVrAutoLaunchStatus.Enabled => "SteamVR自動起動: 有効",
            SteamVrAutoLaunchStatus.Disabled => "SteamVR自動起動: 無効",
            SteamVrAutoLaunchStatus.PendingSteamVr => enabled
                ? "SteamVR自動起動: 登録保留"
                : "SteamVR自動起動: 無効化確認を保留",
            _ => "SteamVR自動起動: 更新失敗",
        };
        _logger.Info(
            "startup.steamvr_auto_launch_checked",
            Guid.Empty,
            ScanStage.Trigger,
            numericMetrics: new Dictionary<string, long>
            {
                ["requestedEnabled"] = enabled ? 1 : 0,
                ["status"] = (long)result.Status,
                ["registered"] = result.IsRegistered ? 1 : 0,
            });
        UpdateEnvironmentDetails();
        return result;
    }

    private void SaveOpenAiApiKeyButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Volatile.Read(ref _uiScanRunning) != 0)
        {
            StatusText.Text = "SCAN完了後にAPIキーを保存してください。";
            return;
        }

        string apiKey = OpenAiApiKeyPasswordBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusText.Text = "保存するOpenAI APIキーを貼り付けてください。";
            return;
        }

        bool saved = false;
        try
        {
            saved = TrySaveOpenAiApiKey(apiKey, out TranslationRuntime? runtime);
            if (runtime is not null)
            {
                StatusText.Text = runtime.Warning is null
                    ? runtime.HasEffectiveApiKey
                        ? "APIキーを暗号化して保存しました。次回のSCANから使います。"
                        : "APIキーを暗号化して保存しました。現在は翻訳が無効なため、外部送信は行いません。"
                    : runtime.Warning;
            }
        }
        finally
        {
            OpenAiApiKeyPasswordBox.Clear();
            if (!TryClearClipboard())
            {
                string warning = "クリップボードを消去できませんでした。別の文字列をコピーしてAPIキーを上書きしてください。";
                OpenAiApiKeyStatusText.Text = saved
                    ? $"{OpenAiApiKeyStatusText.Text} {warning}"
                    : warning;
                StatusText.Text = $"{StatusText.Text} {warning}";
            }
        }
    }

    private bool TrySaveOpenAiApiKey(
        string apiKey,
        out TranslationRuntime? runtime)
    {
        runtime = null;
        try
        {
            _openAiCredentialStore.Write(apiKey);
            runtime = ReloadTranslationRuntime();
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or ExternalException)
        {
            StatusText.Text = "APIキーをWindows資格情報マネージャーへ保存できませんでした。";
            _logger.Error(
                "ui.api_key_save_failed",
                Guid.Empty,
                ScanStage.Translation,
                ScanFailureCode.TranslationNotConfigured,
                exception);
            return false;
        }
    }

    private void DeleteOpenAiApiKeyButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (Volatile.Read(ref _uiScanRunning) != 0)
        {
            StatusText.Text = "SCAN完了後にAPIキーを削除してください。";
            return;
        }

        MessageBoxResult confirmation = System.Windows.MessageBox.Show(
            this,
            "Windows資格情報マネージャーからVRCVAのOpenAI APIキーを削除しますか？",
            "OpenAI APIキーを削除",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.Yes)
        {
            return;
        }

        if (Volatile.Read(ref _uiScanRunning) != 0)
        {
            StatusText.Text = "SCANが開始されたため、APIキーは削除していません。完了後にもう一度お試しください。";
            return;
        }

        try
        {
            _openAiCredentialStore.Delete();
            OpenAiApiKeyPasswordBox.Clear();
            TranslationRuntime runtime = ReloadTranslationRuntime();
            StatusText.Text = runtime.Warning is null
                ? runtime.HasEffectiveApiKey
                    ? "保存済みAPIキーを削除しました。明示設定された環境変数のキーは次回のSCANでも使います。"
                    : "保存済みAPIキーを削除しました。次回のSCANから外部送信しません。"
                : runtime.Warning;
        }
        catch (ExternalException exception)
        {
            StatusText.Text = "OpenAI APIキーを削除できませんでした。";
            _logger.Error(
                "ui.api_key_delete_failed",
                Guid.Empty,
                ScanStage.Translation,
                ScanFailureCode.TranslationNotConfigured,
                exception);
        }
    }

    private bool TryClearClipboard()
    {
        try
        {
            System.Windows.Clipboard.Clear();
            return true;
        }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException)
        {
            _logger.Error(
                "ui.api_key_clipboard_clear_failed",
                Guid.Empty,
                ScanStage.Translation,
                ScanFailureCode.Unexpected,
                exception);
            return false;
        }
    }

    private void TopmostCheckBox_Changed(object sender, RoutedEventArgs eventArgs)
    {
        if (sender is System.Windows.Controls.CheckBox checkBox)
        {
            Topmost = checkBox.IsChecked == true;
        }
    }

    private void InitializeResultPanelPlacementControls()
    {
        List<ResultPanelAnchorChoice> choices =
        [
            new("左手", ResultPanelAnchor.LeftHand),
            new("右手", ResultPanelAnchor.RightHand),
            new("ヘッドセット正面", ResultPanelAnchor.Headset),
        ];
        ResultPanelAnchorComboBox.ItemsSource = choices;
        ResultPanelAnchorComboBox.SelectedItem = choices.First(
            choice => choice.Anchor == _resultPanelPlacement.Anchor);
        _placementControlsInitializing = false;
    }

    private void ResultPanelAnchorComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        if (_placementControlsInitializing
            || ResultPanelAnchorComboBox.SelectedItem is not ResultPanelAnchorChoice choice)
        {
            return;
        }

        _resultPanelPlacement = ResultPanelPlacement.CreateDefault(choice.Anchor);
        ApplyResultPanelPlacement("追従先を変更しました。必要ならVR内で位置を調整してください。");
        TrySaveResultPanelPlacement(
            "追従先を保存しました。必要ならVR内で位置を調整してください。");
    }

    private void StartResultPanelCalibrationButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        try
        {
            bool shown = _steamVrResultPanel.TryShowPlacementCalibration(_resultPanelPlacement);
            _placementCalibrationActive = shown;
            PlacementSettingsExpander.IsEnabled = !shown;
            ResultPanelPlacementStatusText.Text = shown
                ? "VR内に調整画面を表示しました。保存または中止までVR内で操作してください。"
                : _steamVrResultPanel.LastPlacementUsedFallback
                    ? "選択したコントローラーが見つかりません。追従先を変更するか、コントローラーを確認してください。"
                    : "SteamVRへ接続できないか、表示準備中です。数秒後にもう一度押してください。";
        }
        catch (Exception exception)
        {
            ResultPanelPlacementStatusText.Text = "VR内の位置調整画面を表示できませんでした。";
            LogResultPanelPlacementFailure("ui.result_panel_calibration_start_failed", exception);
        }
    }

    private void SteamVrResultPanel_PlacementCalibrationFinished(
        object? sender,
        ResultPanelPlacementCalibrationEventArgs eventArgs)
    {
        _placementCalibrationActive = false;
        PlacementSettingsExpander.IsEnabled = Volatile.Read(ref _uiScanRunning) == 0;
        _resultPanelPlacement = eventArgs.Placement;
        if (eventArgs.SaveRequested)
        {
            TrySaveResultPanelPlacement("VR内で調整した配置を保存しました。");
        }
        else
        {
            ResultPanelPlacementStatusText.Text = "位置調整を中止しました。保存済みの配置へ戻しました。";
        }
    }

    private void SteamVrResultPanel_WristLauncherPlacementCalibrationFinished(
        object? sender,
        WristLauncherPlacementCalibrationEventArgs eventArgs)
    {
        _placementCalibrationActive = false;
        PlacementSettingsExpander.IsEnabled = Volatile.Read(ref _uiScanRunning) == 0;
        if (!eventArgs.SaveRequested)
        {
            ResultPanelPlacementStatusText.Text =
                "左手ランチャーの位置調整を中止し、保存済みの配置へ戻しました。";
            return;
        }

        try
        {
            VrcVaSettings updatedSettings = _settings with
            {
                WristLauncher = eventArgs.Placement,
            };
            _settingsStore.Save(updatedSettings);
            _settings = updatedSettings;
            ResultPanelPlacementStatusText.Text =
                "左手ランチャーの位置・角度・大きさを保存しました。";
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or InvalidDataException)
        {
            ResultPanelPlacementStatusText.Text =
                "左手ランチャーの配置を保存できませんでした。現在の起動中だけ反映します。";
            LogResultPanelPlacementFailure(
                "ui.wrist_launcher_placement_save_failed",
                exception);
        }
    }

    private void SteamVrResultPanel_WristLauncherPlacementCalibrationStarted(
        object? sender,
        EventArgs eventArgs)
    {
        _placementCalibrationActive = true;
        PlacementSettingsExpander.IsEnabled = false;
        ResultPanelPlacementStatusText.Text =
            "VR内で左手ランチャーを調整中です。保存または中止までVR内で操作してください。";
    }

    private void TrySaveResultPanelPlacement(string successMessage)
    {
        try
        {
            VrcVaSettings updatedSettings = _settings with
            {
                ResultPanel = _resultPanelPlacement,
            };
            _settingsStore.Save(updatedSettings);
            _settings = updatedSettings;
            ResultPanelPlacementStatusText.Text = successMessage;
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or InvalidOperationException
                or InvalidDataException)
        {
            ResultPanelPlacementStatusText.Text = "配置を保存できませんでした。";
            LogResultPanelPlacementFailure("ui.result_panel_placement_save_failed", exception);
        }
    }

    private void ApplyResultPanelPlacement(string successMessage)
    {
        try
        {
            bool usedFallback = _steamVrResultPanel.UpdatePlacement(_resultPanelPlacement);
            ResultPanelPlacementStatusText.Text = usedFallback
                ? "選択したコントローラーが見つからないため、現在は正面へ表示します。"
                : successMessage;
        }
        catch (Exception exception)
        {
            ResultPanelPlacementStatusText.Text = "SteamVRへ配置を反映できませんでした。";
            LogResultPanelPlacementFailure("ui.result_panel_placement_apply_failed", exception);
        }
    }

    private void SteamVrResultPanel_PlacementFallback(object? sender, EventArgs eventArgs)
    {
        ResultPanelPlacementStatusText.Text =
            "選択したコントローラーが見つからないため、今回はヘッドセット正面へ表示しました。";
    }

    private void LogResultPanelPlacementFailure(string eventName, Exception exception) =>
        _logger.Error(
            eventName,
            Guid.Empty,
            ScanStage.Rendering,
            ScanFailureCode.Unexpected,
            exception);

    private void TranslationModelComboBox_SelectionChanged(
        object sender,
        System.Windows.Controls.SelectionChangedEventArgs eventArgs)
    {
        TranslationRuntime runtime = _analyzer.Current;
        OpenAiTextTranslator? translator = runtime.OpenAiTranslator;
        if (_modelSelectorInitializing
            || translator is null
            || TranslationModelComboBox.SelectedItem is not TranslationModelChoice choice)
        {
            return;
        }

        translator.SelectModel(choice.ModelId);
        _translationStatus = runtime.HasEffectiveApiKey
            ? CreateOpenAiTranslationStatus(choice.ModelId)
            : $"翻訳API: OpenAI / {choice.ModelId} / 専用キー未設定";
        StatusText.Text = $"翻訳モデルを {choice.DisplayName} に変更しました。次回のSCANから使います。";
        UpdateEnvironmentDetails();
    }

    private async Task RunPipelineAsync(
        ScanPipeline pipeline,
        string triggerName,
        TimeSpan preCaptureDelay = default)
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
            _steamVrResultPanel.BeginScan();
            _steamVrResultPanel.Hide();

            if (preCaptureDelay > TimeSpan.Zero)
            {
                _ = _steamVrResultPanel.TryShowStatus(ResultPanelTexture.WaitingCell);

                StatusText.Text = "SCANを受け付けました。Action Menuを閉じてください。";
                DetailText.Text = "段階: Trigger / OSCメニュー消去待ち";
                await Task.Delay(preCaptureDelay, _activeScanCancellation.Token);

                _steamVrResultPanel.ShowStatus(ResultPanelTexture.CapturingCell);
                StatusText.Text = "撮影を開始します。";
                DetailText.Text = "段階: Capture / 撮影開始";
                await Task.Delay(CaptureNoticeDuration, _activeScanCancellation.Token);
                _steamVrResultPanel.Hide();
            }

            await pipeline.RunAsync(
                ScanRequest.Create(triggerName),
                _activeScanCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            _steamVrResultPanel.Hide();
            _steamVrResultPanel.ReturnToLauncher();
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
        OpenAiApiKeyPasswordBox.IsEnabled = !isRunning;
        SaveOpenAiApiKeyButton.IsEnabled = !isRunning;
        DeleteOpenAiApiKeyButton.IsEnabled = !isRunning;
        OpenSetupButton.IsEnabled = !isRunning && !_setupWindowOpen;
        TranslationModelComboBox.IsEnabled =
            !isRunning && _analyzer.Current.OpenAiTranslator is not null;
        PlacementSettingsExpander.IsEnabled = !isRunning && !_placementCalibrationActive;
    }

    private void RenderProgress(ScanProgress progress)
    {
        StatusText.Text = progress.Message;
        DetailText.Text = $"段階: {progress.Stage} / {progress.Elapsed.TotalSeconds:0.0}秒 / 相関ID: {progress.CorrelationId:D}";
    }

    private void RenderOutcome(ScanOutcome outcome)
    {
        TranslationRuntime runtime = _analyzer.Current;
        if (runtime.OpenAiTranslator is not null && runtime.HasEffectiveApiKey)
        {
            _translationStatus = CreateOpenAiTranslationStatus(runtime.OpenAiTranslator.Model);
        }

        if (!outcome.IsSuccess || outcome.Result is null)
        {
            StatusText.Text = outcome.Failure?.Message ?? "SCANに失敗しました。";
            DetailText.Text = $"失敗: {outcome.Failure?.Code} / 段階: {outcome.Failure?.Stage}"
                + CreateOpenAiUsageSuffix()
                + $" / 相関ID: {outcome.CorrelationId:D}";
            return;
        }

        FeatureResultPresentation presentation =
            FeatureResultPresentation.Create(outcome.Result);
        SourceResultGroupBox.Header = presentation.SourceTitle;
        SourceTextBox.Text = presentation.SourceText;
        PrimaryResultGroupBox.Header = presentation.PrimaryTitle;
        TranslationTextBox.Text = presentation.PrimaryText;
        CopyPrimaryResultButton.Content = presentation.CopyButtonText;
        StatusText.Text = presentation.CompletionMessage;
        string modelStageLabel = outcome.Result.FeatureId == FeatureIds.Translation
            ? "翻訳"
            : outcome.Result.PrimarySection.Title;
        DetailText.Text =
            $"合計 {outcome.TotalDuration.TotalSeconds:0.0}秒 "
            + $"(OCR {outcome.Result.OcrDuration.TotalSeconds:0.0}秒 / {modelStageLabel} {outcome.Result.TranslationDuration.TotalSeconds:0.0}秒) "
            + $"/ 取得 {CaptureSourceDisplayName.Get(outcome.Result.CaptureSourceKind)} "
            + $"/ OCR {outcome.Result.OcrLanguage} / {outcome.Result.TranslationProvider} {outcome.Result.TranslationModel}"
            + CreateOpenAiUsageSuffix()
            + $" / 相関ID: {outcome.CorrelationId:D}";
    }

    private void MainWindow_Closed(object? sender, EventArgs eventArgs)
    {
        _windowLifetimeCancellation.Cancel();
        _activeScanCancellation?.Cancel();
        _activeScanCancellation?.Dispose();
        _globalHotKey?.Dispose();
        _modelToggleHotKey?.Dispose();
        if (_oscTriggerService is not null)
        {
            _oscTriggerService.Triggered -= OscTriggerService_Triggered;
            _oscTriggerService.Faulted -= OscTriggerService_Faulted;
            _oscTriggerService.Dispose();
        }

        _steamVrResultPanel.PlacementFallback -= SteamVrResultPanel_PlacementFallback;
        _steamVrResultPanel.PlacementCalibrationFinished -=
            SteamVrResultPanel_PlacementCalibrationFinished;
        _steamVrResultPanel.WristLauncherPlacementCalibrationStarted -=
            SteamVrResultPanel_WristLauncherPlacementCalibrationStarted;
        _steamVrResultPanel.WristLauncherPlacementCalibrationFinished -=
            SteamVrResultPanel_WristLauncherPlacementCalibrationFinished;
        _steamVrResultPanel.Dispose();
        _httpClient.Dispose();
        _windowLifetimeCancellation.Dispose();
    }

    private void FitWindowToWorkingArea()
    {
        Rect workArea = SystemParameters.WorkArea;
        Rect fitted = WindowPlacement.Fit(
            workArea,
            Width,
            Height,
            WindowWorkAreaMargin);

        MinWidth = Math.Min(MinWidth, fitted.Width);
        MinHeight = Math.Min(MinHeight, fitted.Height);
        Width = fitted.Width;
        Height = fitted.Height;
        MaxWidth = fitted.Width;
        MaxHeight = fitted.Height;
        Left = fitted.Left;
        Top = fitted.Top;
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

    private TranslationRuntime ReloadTranslationRuntime()
    {
        string? preferredModel = TranslationModelComboBox.SelectedItem is TranslationModelChoice choice
            ? choice.ModelId
            : null;
        TranslationRuntime runtime = CreateTranslationRuntime(
            preferredModel,
            isStartup: false);
        _analyzer.Swap(runtime);
        UpdateTranslationSettingsUi(runtime);
        UpdateModelToggleHotKeyRegistration();
        UpdateEnvironmentDetails();
        return runtime;
    }

    private TranslationRuntime CreateTranslationRuntime(
        string? preferredModel,
        bool isStartup)
    {
        string? storedApiKey = null;
        Exception? credentialReadFailure = null;
        try
        {
            storedApiKey = _openAiCredentialStore.Read();
        }
        catch (Exception exception) when (
            exception is ExternalException or InvalidOperationException)
        {
            credentialReadFailure = exception;
            _logger.Error(
                isStartup
                    ? "startup.openai_credential_read_failed"
                    : "ui.openai_credential_read_failed",
                Guid.Empty,
                ScanStage.Translation,
                ScanFailureCode.TranslationNotConfigured,
                exception);
        }

        string? environmentApiKey = Environment.GetEnvironmentVariable("VRCVA_OPENAI_API_KEY");
        bool hasEnvironmentApiKey = !string.IsNullOrWhiteSpace(environmentApiKey);
        bool hasStoredApiKey = !string.IsNullOrWhiteSpace(storedApiKey);
        TranslationRuntime runtime;
        try
        {
            runtime = _translationRuntimeFactory.Create(
                Environment.GetEnvironmentVariable("VRCVA_TRANSLATION_PROVIDER"),
                environmentApiKey,
                storedApiKey,
                OpenAiTranslatorOptions.FromEnvironment,
                preferredModel);
        }
        catch (Exception exception) when (
            exception is InvalidOperationException
                or UriFormatException
                or ArgumentException)
        {
            const string warning =
                "翻訳設定が不正か安全要件を満たさないため、外部送信を停止しました。保存済みAPIキーは公式OpenAI接続先にだけ送信します。READMEの設定例を確認してください。";
            _logger.Error(
                isStartup
                    ? "startup.translation_configuration_invalid"
                    : "ui.translation_configuration_invalid",
                Guid.Empty,
                ScanStage.Translation,
                ScanFailureCode.TranslationNotConfigured,
                exception);
            if (isStartup)
            {
                AppendStartupWarning(warning);
            }

            return _translationRuntimeFactory.CreateConfigurationFailure(
                hasEnvironmentApiKey,
                hasStoredApiKey,
                warning);
        }

        if (credentialReadFailure is null)
        {
            return runtime;
        }

        string credentialWarning = runtime.KeySource == TranslationKeySource.Environment
            && runtime.HasEffectiveApiKey
                ? "保存済みOpenAI APIキーは読み込めませんでしたが、明示設定された環境変数のキーを使用します。"
                : "Windows資格情報マネージャーからOpenAI APIキーを読み込めませんでした。外部送信は行いません。";
        if (isStartup)
        {
            AppendStartupWarning(credentialWarning);
        }

        return runtime with { Warning = credentialWarning };
    }

    private void UpdateTranslationSettingsUi(TranslationRuntime runtime)
    {
        OpenAiTextTranslator? translator = runtime.OpenAiTranslator;
        _translationStatus = translator is not null && runtime.HasEffectiveApiKey
            ? CreateOpenAiTranslationStatus(translator.Model)
            : runtime.Status;
        PrivacyText.Text = runtime.PrivacyNotice;
        OpenAiApiKeyStatusText.Text = runtime.HasStoredApiKey
            ? runtime.KeySource == TranslationKeySource.Environment
                ? "Windows資格情報マネージャーへ保存済みです。現在は明示設定された環境変数のキーを優先しています。"
                : runtime.HasEffectiveApiKey
                    ? "Windows資格情報マネージャーへ保存済みです。次回のSCANから自動的に使います。"
                    : "Windows資格情報マネージャーへ保存済みです。現在は翻訳が無効か、設定エラーのため送信しません。"
            : runtime.KeySource == TranslationKeySource.Environment
                ? "この起動中だけ有効な環境変数のキーを使用しています。保存すると次回から入力不要です。"
                : "未登録です。VRCVA専用キーを貼り付け、暗号化して保存してください。";
        TranslationModelComboBox.IsEnabled =
            Volatile.Read(ref _uiScanRunning) == 0 && translator is not null;
        TranslationModelHintText.Text = translator is null
            ? "OpenAIを有効にした場合に選択できます"
            : $"次回のSCANから反映（従量課金・1起動最大{translator.MaxRequestsPerSession}回）";
    }

    private void UpdateModelToggleHotKeyRegistration()
    {
        if (_analyzer.Current.OpenAiTranslator is null)
        {
            if (_modelToggleHotKey is not null)
            {
                _modelToggleHotKey.Pressed -= ModelToggleHotKey_Pressed;
                _modelToggleHotKey.Dispose();
                _modelToggleHotKey = null;
                _modelToggleHotKeyDisplayText = null;
            }

            return;
        }

        if (_modelToggleHotKey is not null)
        {
            TranslationModelHintText.Text =
                $"次回から反映 / 切替 {_modelToggleHotKeyDisplayText}";
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
            _modelToggleHotKeyDisplayText = definition.DisplayText;
            TranslationModelHintText.Text =
                $"次回から反映 / 切替 {definition.DisplayText}";
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

    private void AppendStartupWarning(string warning) =>
        _startupWarning = string.IsNullOrWhiteSpace(_startupWarning)
            ? warning
            : $"{_startupWarning} {warning}";

    private void InitializeModelSelector()
    {
        List<TranslationModelChoice> choices =
        [
            new("推奨・低料金 — GPT-5.6 Luna", OpenAiTranslatorOptions.QualityModel),
            new("比較用 — GPT-5.4 nano", OpenAiTranslatorOptions.BudgetModel),
        ];

        OpenAiTextTranslator? translator = _analyzer.Current.OpenAiTranslator;
        string selectedModel = translator?.Model ?? OpenAiTranslatorOptions.DefaultModel;
        TranslationModelChoice? selectedChoice = choices.FirstOrDefault(
            choice => string.Equals(choice.ModelId, selectedModel, StringComparison.Ordinal));
        if (selectedChoice is null)
        {
            selectedChoice = new($"環境設定 — {selectedModel}", selectedModel);
            choices.Add(selectedChoice);
        }

        TranslationModelComboBox.ItemsSource = choices;
        TranslationModelComboBox.SelectedItem = selectedChoice;
        TranslationModelComboBox.IsEnabled = translator is not null;
        TranslationModelHintText.Text = translator is null
            ? "OpenAIを有効にした場合に選択できます"
            : $"次回のSCANから反映（従量課金・1起動最大{translator.MaxRequestsPerSession}回）";
        _modelSelectorInitializing = false;
    }

    private void UpdateEnvironmentDetails() =>
        DetailText.Text =
            $"{_captureConfiguration} / {_ocrInfo} / {_translationStatus} / {_oscStatus} / {_steamVrAutoLaunchStatus} / ログ: {_logger.LogDirectory}";

    private string CreateOpenAiTranslationStatus(string model)
    {
        OpenAiTextTranslator? translator = _analyzer.Current.OpenAiTranslator;
        return $"翻訳API: OpenAI / {model}（従量課金・残り{translator?.RemainingRequests ?? 0}"
            + $"/{translator?.MaxRequestsPerSession ?? 0}回）";
    }

    private string CreateOpenAiUsageSuffix()
    {
        TranslationRuntime runtime = _analyzer.Current;
        return runtime.OpenAiTranslator is null || !runtime.HasEffectiveApiKey
            ? string.Empty
            : $" / 翻訳API残り {runtime.OpenAiTranslator.RemainingRequests}/{runtime.OpenAiTranslator.MaxRequestsPerSession}回";
    }

    private sealed record TranslationModelChoice(string DisplayName, string ModelId);

    private sealed record ResultPanelAnchorChoice(string DisplayName, ResultPanelAnchor Anchor);
}
