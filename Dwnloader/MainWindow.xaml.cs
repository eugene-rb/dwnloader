using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Dwnloader.Core;

namespace Dwnloader;

public partial class MainWindow : Window
{
    private Session? _session;
    private TrayIcon? _tray;
    private bool _quitting;
    private bool _suppressToggleEvents = true;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppInfo.WindowTitle;
    }

    public void Attach(Session session, TrayIcon tray)
    {
        _session = session;
        _tray = tray;

        EntryList.ItemsSource = session.Entries;

        session.LogMessage += OnLog;
        session.SpeedSampled += OnSpeedSampled;
        session.Notification += OnNotify;
        session.StateChanged += SyncState;
        session.FocusRequested += FocusEntry;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (_session is null) return;

        // クリップボードの変化はこのウィンドウのメッセージとして受け取る
        _session.Clipboard.Attach(this);
        _session.Bootstrap();

        _suppressToggleEvents = true;
        WatchToggle.IsChecked = _session.Clipboard.IsEnabled;
        PlaylistToggle.IsChecked = _session.Settings.PlaylistAll;
        SelectKind(_session.MediaKindValue);
        Topmost = _session.Settings.AlwaysOnTop;
        _suppressToggleEvents = false;

        SyncState();
    }

    // ============================================================== 状態の反映

    private void SyncState()
    {
        if (_session is null) return;

        StatusText.Text = _session.StatusText_();
        StartAllBtn.IsEnabled = _session.HasStartable();

        int unfinished = _session.UnfinishedCount();
        RetryAllBtn.IsEnabled = unfinished > 0;
        RetryAllBtn.Content = unfinished > 0 ? $"未完了 {unfinished} 件を再試行" : "未完了を再試行";

        _tray?.SetStatus(_session.Clipboard.IsEnabled, _session.StatusText_());
    }

    private void OnLog(string level, string message)
    {
        var stamp = DateTime.Now.ToString("HH:mm:ss");
        var prefix = level switch
        {
            "error" => "[エラー] ",
            "warn" => "[警告] ",
            "ok" => "[完了] ",
            _ => "",
        };
        LogList.Items.Add($"{stamp} {prefix}{message}");

        // ログは伸び続けるので上限を決める。古い行から捨てる。
        while (LogList.Items.Count > 2000) LogList.Items.RemoveAt(0);
        if (LogPane.Visibility == Visibility.Visible && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void OnNotify(string title, string body, string kind)
    {
        // ウィンドウが見えていないときだけトレイから知らせる。
        // 両方に出すと過剰なので、見えているときは状態表示に任せる。
        if (!IsVisible || WindowState == WindowState.Minimized)
            _tray?.Notify(title, body);
        else
            StatusText.Text = body.Length > 0 ? $"{title} — {body}" : title;
    }

    private void FocusEntry(EntryVm entry)
    {
        EntryList.ScrollIntoView(entry);
        EntryList.SelectedItem = entry;
    }

    // ============================================================== 追加

    private void Add_Click(object sender, RoutedEventArgs e) => SubmitInput();

    private void UrlInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { SubmitInput(); e.Handled = true; }
    }

    private void SubmitInput()
    {
        var text = UrlInput.Text.Trim();
        if (text.Length == 0) return;
        UrlInput.Clear();
        _session?.AddText(text);
    }

    // ============================================================== 一覧の操作

    private EntryVm? EntryOf(object sender) =>
        sender is FrameworkElement { Tag: string jobId } ? _session?.Find(jobId) : null;

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) _session?.OpenResult(entry);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) _session?.RevealResult(entry);
    }

    private void Page_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) _session?.OpenPage(entry);
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) _session?.Retry(entry.JobId);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) _session?.Cancel(entry.JobId);
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is { } entry) _session?.Remove(entry.JobId);
    }

    private void SwapKind_Click(object sender, RoutedEventArgs e)
    {
        if (EntryOf(sender) is not { } entry) return;
        var next = entry.Reference.Kind == MediaKind.Audio ? MediaKind.Video : MediaKind.Audio;
        _session?.SetEntryKind(entry.JobId, next);
    }

    private void EntryList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryVm entry || _session is null) return;
        if (entry.CanOpen) _session.OpenResult(entry);
        else _session.OpenPage(entry);
    }

    private void EntryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (_session is null) return;

        if (e.Key == Key.Delete)
        {
            _session.RemoveMany(EntryList.SelectedItems.Cast<EntryVm>().ToList());
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && EntryList.SelectedItem is EntryVm entry)
        {
            if (entry.CanOpen) _session.OpenResult(entry);
            else _session.OpenPage(entry);
            e.Handled = true;
        }
    }

    // ============================================================== 一括操作

    private void StartAll_Click(object sender, RoutedEventArgs e) => _session?.StartAll();
    private void RetryAll_Click(object sender, RoutedEventArgs e) => _session?.RetryAll();
    private void ClearFinished_Click(object sender, RoutedEventArgs e) => _session?.ClearFinished();

    private void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _session.Entries.Count == 0) return;

        int running = _session.RunningCount();
        if (running > 0)
        {
            var answer = MessageBox.Show(this,
                $"{running} 件が進行中です。中止して全部消しますか？",
                "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes) return;
        }
        _session.ClearAll();
    }

    private void OpenPdfDir_Click(object sender, RoutedEventArgs e) => _session?.OpenOutputDir();
    private void OpenVideoDir_Click(object sender, RoutedEventArgs e)
        => _session?.OpenOutputDir(MediaKind.Video);
    private void OpenAudioDir_Click(object sender, RoutedEventArgs e)
        => _session?.OpenOutputDir(MediaKind.Audio);

    /// <summary>
    /// 速度の表示を更新する。グラフは畳まれていれば描き直さない
    /// （見えていないものを毎秒描くのは無駄なので）。
    /// </summary>
    private void OnSpeedSampled()
    {
        if (_session is null) return;

        var speed = _session.Speed;
        // 止まっているときに 0 B/s を出し続けても意味がないので、走っている
        // ものが無ければ何も出さない。
        bool busy = speed.Current > 0 || _session.RunningCount() > 0;
        SpeedText.Text = busy ? $"{Util.HumanSize(speed.Current)}/s" : "";

        if (SpeedPane.Visibility != Visibility.Visible) return;

        Graph.Update(speed);
        SpeedNow.Text = busy ? $"{Util.HumanSize(speed.Current)}/s" : "—";
        SpeedPeak.Text = speed.Peak > 0 ? $"最大 {Util.HumanSize(speed.Peak)}/s" : "";
        SpeedTotal.Text = speed.TotalBytes > 0
            ? $"累計 {Util.HumanSize(speed.TotalBytes)}" : "";
        int running = _session.RunningCount();
        SpeedActive.Text = running > 0 ? $"進行中 {running} 件" : "";
    }

    private void ToggleSpeed_Click(object sender, RoutedEventArgs e)
    {
        SpeedPane.Visibility = SpeedPane.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (SpeedPane.Visibility == Visibility.Visible) OnSpeedSampled();
    }

    private void ToggleLog_Click(object sender, RoutedEventArgs e)
    {
        LogPane.Visibility = LogPane.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (LogPane.Visibility == Visibility.Visible && LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[^1]);
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var dialog = new SettingsWindow(_session.Settings) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is { } values)
        {
            _session.SaveSettings(values);
            Topmost = values.AlwaysOnTop;
            _suppressToggleEvents = true;
            WatchToggle.IsChecked = values.WatchClipboard;
            _suppressToggleEvents = false;
        }
    }

    // ============================================================== トグル

    private void Watch_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents || _session is null) return;
        _session.SetWatch(WatchToggle.IsChecked == true);
        SyncState();
    }

    private void Playlist_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents || _session is null) return;
        _session.SetPlaylistAll(PlaylistToggle.IsChecked == true);
    }

    private void Kind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressToggleEvents || _session is null) return;
        if (KindBox.SelectedItem is ComboBoxItem { Tag: string kind })
            _session.SetMediaKind(kind);
    }

    private void SelectKind(string kind)
    {
        foreach (ComboBoxItem item in KindBox.Items)
        {
            if ((item.Tag as string) == kind) { KindBox.SelectedItem = item; return; }
        }
        KindBox.SelectedIndex = 0;
    }

    // ============================================================== 表示・終了

    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    public void BeginQuit()
    {
        _quitting = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_session is null) { base.OnClosing(e); return; }

        if (!_quitting && _session.Settings.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _tray?.Notify(AppInfo.Title, "タスクトレイで監視を続けます");
            return;
        }

        if (!_quitting)
        {
            int running = _session.RunningCount();
            if (running > 0)
            {
                var answer = MessageBox.Show(this,
                    $"{running} 件のダウンロードが進行中です。中止して終了しますか？",
                    "確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (answer != MessageBoxResult.Yes) { e.Cancel = true; return; }
            }
        }

        base.OnClosing(e);
    }
}
