using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using System.Windows.Threading;
using VrcVa.Core;
using VrcVa.Infrastructure;

namespace VrcVa.Windows.Voice;

public partial class VoiceInputPanel : System.Windows.Controls.UserControl
{
    private readonly VoiceInputFlow _flow;
    private readonly ExecutionCoordinator _execution;
    private readonly VoiceInputConfiguration _configuration;
    private readonly VoiceRequestQuota _quota;
    private readonly Func<VoiceInputOptions> _options;
    private readonly DispatcherTimer _timer;

    internal VoiceInputPanel(VoiceInputFlow flow, ExecutionCoordinator execution,
        VoiceInputConfiguration configuration, VoiceRequestQuota quota, Func<VoiceInputOptions> options)
    {
        InitializeComponent();
        _flow = flow;
        _execution = execution;
        _configuration = configuration;
        _quota = quota;
        _options = options;
        _flow.Changed += Flow_Changed;
        _timer = new(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
            (_, _) => _flow.Refresh(), Dispatcher);
        _timer.Stop();
        Loaded += (_, _) => { _timer.Start(); Refresh(); };
        Unloaded += (_, _) => _timer.Stop();
        Refresh();
    }

    // K5 can populate a feature-specific action view here, using CurrentInput and the same gate.
    // No search buttons or feature registration are exposed until their actual handlers are connected.
    internal ContentControl ResultActions => ResultActionsHost;
    internal TextInputSession? CurrentInput => _flow.CurrentInput;

    private void Flow_Changed(object? sender, EventArgs eventArgs)
    {
        // This flow and all its public operations are dispatcher-owned. Recheck the live
        // session here rather than posting a captured transcript that could arrive late.
        Dispatcher.VerifyAccess();
        Refresh();
    }

    internal void Refresh()
    {
        VoiceInputOptions options = _options();
        bool busy = _execution.IsRunning;
        ConsentCheckBox.IsChecked = options.IsEnabled;
        ConsentCheckBox.IsEnabled = !busy;
        VoiceKeyBox.IsEnabled = !busy;
        SaveKeyButton.IsEnabled = !busy;
        DeleteKeyButton.IsEnabled = !busy;
        RecordButton.IsEnabled = _flow.CanStop || (!busy && options.IsEnabled && !_flow.RequiresRestart);
        RecordButton.Content = _flow.CanStop ? "停止して文字起こし" : _flow.CurrentInput is null ? "録音開始 / 録り直し" : "録り直し（全文置換）";
        CancelVoiceButton.IsEnabled = busy && _flow.IsCurrent && _flow.State != VoiceFlowState.Cancelling;
        RetryButton.IsEnabled = options.IsEnabled && _flow.CanRetry;
        CloseVoiceButton.IsEnabled = _flow.SessionId != Guid.Empty;
        TranscriptBox.Text = _flow.CurrentInput?.Transcript ?? string.Empty;
        ResultActionsHost.IsEnabled = !busy && _flow.CurrentInput is not null;
        ResultActionsHost.Visibility = _flow.CurrentInput is null ? Visibility.Collapsed : Visibility.Visible;
        FlowStatusText.Text = _flow.Message;
        FlowDetailText.Text = _flow.CanStop
            ? $"録音中 / 残り {_flow.RemainingSeconds} 秒（最大 {options.MaximumRecordingSeconds} 秒）"
            : $"段階: {_flow.State}" + (_flow.FailureCode is string code ? $" / 失敗: {code}" : string.Empty);
        QuotaText.Text = $"音声枠: 残り {_quota.RemainingSeconds}/{_quota.MaximumSeconds} 秒・{_quota.RemainingRequests}/{_quota.MaximumRequests} 送信（起動ごと）"
            + $" / 録音上限 {options.MaximumRecordingSeconds} 秒 / 失敗音声の保持 {options.FailedAudioRetentionSeconds} 秒。再送で期限は延びません。";
    }

    private void RecordButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        if (_flow.CanStop) { _flow.StopRecording(); }
        else if (!_flow.TryStart() && _execution.IsRunning) { ConfigurationStatusText.Text = "前の処理を実行中です。完了後にもう一度操作してください。"; }
        Refresh();
    }

    private void CancelVoiceButton_Click(object sender, RoutedEventArgs eventArgs) => _flow.Cancel();
    private void RetryButton_Click(object sender, RoutedEventArgs eventArgs) { _flow.TryRetry(); Refresh(); }
    private async void CloseVoiceButton_Click(object sender, RoutedEventArgs eventArgs) => await _flow.CloseAsync();

    private void ConsentCheckBox_Click(object sender, RoutedEventArgs eventArgs)
    {
        bool enable = ConsentCheckBox.IsChecked == true;
        Configure(() => _configuration.SetEnabled(enable), enable
            ? "音声入力を有効にしました。録音ボタンを押すまでマイクは開きません。"
            : "音声入力を無効にしました。");
    }

    private void SaveKeyButton_Click(object sender, RoutedEventArgs eventArgs)
    {
        try { Configure(() => _configuration.SaveKey(VoiceKeyBox.Password), "音声専用キーをWindows資格情報マネージャーに保存しました。同意設定は変更していません。"); }
        finally { VoiceKeyBox.Clear(); }
    }

    private void DeleteKeyButton_Click(object sender, RoutedEventArgs eventArgs) =>
        Configure(_configuration.DeleteKey, "音声専用キーを削除しました。");

    private void Configure(Action update, string success)
    {
        if (!_execution.TryBeginConfiguration(out ExecutionOperation? operation))
        {
            ConfigurationStatusText.Text = "前の処理を実行中です。設定は変更しませんでした。";
            Refresh();
            return;
        }
        using (operation)
        {
            try { update(); ConfigurationStatusText.Text = success; }
            catch (Exception) { ConfigurationStatusText.Text = "設定または音声専用キーを保存・削除できませんでした。内容はログへ保存していません。"; }
        }
        Refresh();
    }

    private void PolicyLink_RequestNavigate(object sender, RequestNavigateEventArgs eventArgs)
    {
        // Only two fixed documentation links from our own XAML can launch a browser.
        if (eventArgs.Uri.AbsoluteUri is "https://developers.openai.com/api/docs/pricing"
            or "https://developers.openai.com/api/docs/guides/your-data")
        {
            try { Process.Start(new ProcessStartInfo(eventArgs.Uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception) { ConfigurationStatusText.Text = "ブラウザーで公式説明を開けませんでした。READMEの公式リンクを確認してください。"; }
        }
        eventArgs.Handled = true;
    }

    internal void Detach()
    {
        _timer.Stop();
        _flow.Changed -= Flow_Changed;
        TranscriptBox.Clear();
        VoiceKeyBox.Clear();
        ResultActionsHost.Content = null;
    }
}
