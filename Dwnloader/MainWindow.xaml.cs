using System.ComponentModel;
using Dwnloader.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace Dwnloader;

public partial class MainWindow : Window
{
    private Session? _session;
    private TrayIcon? _tray;
    private UpdateService? _updates;
    private bool _updateBusy;
    private bool _quitting;
    private bool _suppressToggleEvents = true;

    public MainWindow()
    {
        InitializeComponent();
        Title = AppInfo.WindowTitle;

        var appWindow = WindowInterop.GetAppWindow(this);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1040, 760));
        appWindow.Closing += OnAppWindowClosing;

        // タイトルバーのアイコン設定
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "app.ico");
            if (System.IO.File.Exists(iconPath))
            {
                appWindow.SetIcon(iconPath);
            }
        }
        catch { }

        // ウィンドウ初期化後にタイトルバー色を設定
        // ContentGrid のテーマ変更時に追従
        DispatcherQueue.TryEnqueue(() =>
        {
            UpdateTitleBarColor();
            if (Content is FrameworkElement contentGrid)
            {
                contentGrid.ActualThemeChanged += (s, e) => UpdateTitleBarColor();
            }
        });
    }

    // ============================================================== テーマ設定

    private void UpdateTitleBarColor()
    {
        try
        {
            var appWindow = WindowInterop.GetAppWindow(this);
            var titleBar = appWindow.TitleBar;

            // Content Grid のテーマから判定（Window 直下ではなく Grid から取得）
            var isDark = (Content as FrameworkElement)?.ActualTheme == ElementTheme.Dark;

            // ダークモード時とライトモード時で色を切り替え
            // カード背景やシステムリソースに合わせて、統一感を持たせる
            if (isDark)
            {
                // ダークモード：暗いグレー（Mica 背景に溶け込む）
                titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
                titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 32, 32, 32);
                titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
            }
            else
            {
                // ライトモード：明るいグレー
                titleBar.BackgroundColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
                titleBar.ForegroundColor = Windows.UI.Color.FromArgb(255, 0, 0, 0);
                titleBar.InactiveBackgroundColor = Windows.UI.Color.FromArgb(255, 240, 240, 240);
                titleBar.InactiveForegroundColor = Windows.UI.Color.FromArgb(255, 128, 128, 128);
            }
        }
        catch { }
    }

    public void Attach(Session session, TrayIcon tray, UpdateService? updates = null)
    {
        _session = session;
        _tray = tray;
        _updates = updates;

        EntryList.ItemsSource = session.Entries;

        session.LogMessage += OnLog;
        session.SpeedSampled += OnSpeedSampled;
        session.Notification += OnNotify;
        session.StateChanged += SyncState;
        session.FocusRequested += FocusEntry;

        // クリップボードの変化はこのウィンドウのメッセージとして受け取る
        _session.Clipboard.Attach(this);
        _session.Bootstrap();

        _suppressToggleEvents = true;
        WatchToggle.IsChecked = _session.Clipboard.IsEnabled;
        PlaylistToggle.IsChecked = _session.Settings.PlaylistAll;
        SelectKind(_session.MediaKindValue);
        WindowInterop.SetAlwaysOnTop(this, _session.Settings.AlwaysOnTop);
        _suppressToggleEvents = false;

        SyncState();
        _ = CheckUpdatesQuietlyAsync();
    }

    // ============================================================== 確認ダイアログ

    private async Task<bool> ConfirmAsync(string title, string message,
        string yesText = "はい", string noText = "いいえ")
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            PrimaryButtonText = yesText,
            CloseButtonText = noText,
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = title,
            Content = message,
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }

    // ============================================================== 更新

    /// <summary>
    /// 起動時に静かに確認する。見つかったときだけ「更新」ボタンを出す。
    /// 見つからなかった場合や通信できなかった場合は何も言わない
    /// （起動のたびに更新の話を持ち出さない）。
    /// </summary>
    private async Task CheckUpdatesQuietlyAsync()
    {
        if (_updates is null || !_updates.IsSupported) return;

        // 起動直後は復元や走査で忙しい。少し待ってから確認する。
        await Task.Delay(TimeSpan.FromSeconds(5));
        try
        {
            var found = await _updates.CheckAsync();
            if (found is null) return;

            UpdateBtn.Content = $"v{found} に更新";
            ToolTipService.SetToolTip(UpdateBtn,
                $"新しい版 v{found} が公開されています（現在 v{AppInfo.Version}）");
            UpdateBtn.Visibility = Visibility.Visible;
            _session?.Log("info", $"新しい版 v{found} が公開されています。"
                                  + "下の「更新」から適用できます。");
        }
        catch (Exception e)
        {
            // 通信できないだけで騒がない。ログにだけ残す。
            _session?.Log("info", $"更新の確認をとばしました: {e.Message}");
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is null || _session is null || _updateBusy) return;

        // 適用はプロセスの入れ替えなので、走っているものがあると道連れになる。
        int running = _session.RunningCount();
        if (running > 0)
        {
            await ShowMessageAsync("更新できません",
                $"{running} 件が進行中です。終わるか中止してから更新してください。"
                + Environment.NewLine
                + "（更新はアプリを一度終了させるため、途中のダウンロードは失われます）");
            return;
        }

        var version = _updates.AvailableVersion ?? "新しい版";
        bool proceed = await ConfirmAsync("更新",
            $"v{version} に更新します。ダウンロードのあとアプリを再起動します。");
        if (!proceed) return;

        _updateBusy = true;
        UpdateBtn.IsEnabled = false;
        try
        {
            UpdateBtn.Content = "取得中… 0%";
            await _updates.DownloadAsync(percent => DispatcherQueue.TryEnqueue(
                () => UpdateBtn.Content = $"取得中… {percent}%"));

            // 落としている間に何か走り出していないか、直前にもう一度見る
            if (_session.RunningCount() > 0)
            {
                await ShowMessageAsync("更新を中断しました",
                    "取得中に新しいダウンロードが始まりました。終わってから「更新」を押し直してください。");
                UpdateBtn.Content = "更新";
                return;
            }

            // 書きかけの設定・履歴・キューを確実に書き出してから入れ替える
            _session.Shutdown();
            _tray?.Dispose();
            _updates.ApplyAndRestart();     // ここから戻ってこない
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("更新", $"更新できませんでした: {ex.Message}");
            UpdateBtn.Content = "更新";
        }
        finally
        {
            _updateBusy = false;
            UpdateBtn.IsEnabled = true;
        }
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
        if (WindowInterop.IsMinimized(this))
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

    private void UrlInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { SubmitInput(); e.Handled = true; }
    }

    private void SubmitInput()
    {
        var text = UrlInput.Text.Trim();
        if (text.Length == 0) return;
        UrlInput.Text = "";
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

    private void EntryList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryVm entry || _session is null) return;
        if (entry.CanOpen) _session.OpenResult(entry);
        else _session.OpenPage(entry);
    }

    private void EntryList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_session is null) return;

        if (e.Key == VirtualKey.Delete)
        {
            _session.RemoveMany(EntryList.SelectedItems.Cast<EntryVm>().ToList());
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter && EntryList.SelectedItem is EntryVm entry)
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

    private async void ClearAll_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null || _session.Entries.Count == 0) return;

        int running = _session.RunningCount();
        if (running > 0)
        {
            bool proceed = await ConfirmAsync("確認",
                $"{running} 件が進行中です。中止して全部消しますか？", "はい", "いいえ");
            if (!proceed) return;
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

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        var values = await SettingsWindow.ShowAsync(_session.Settings, _updates);
        if (values is not null)
        {
            _session.SaveSettings(values);
            WindowInterop.SetAlwaysOnTop(this, values.AlwaysOnTop);
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
        foreach (ComboBoxItem item in KindBox.Items.Cast<ComboBoxItem>())
        {
            if ((item.Tag as string) == kind) { KindBox.SelectedItem = item; return; }
        }
        KindBox.SelectedIndex = 0;
    }

    // ============================================================== 表示・終了

    public void ShowFromTray()
    {
        WindowInterop.ShowNormal(this);
        Activate();
    }

    public void BeginQuit()
    {
        _quitting = true;
        Close();
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_session is null) return;

        if (!_quitting && _session.Settings.MinimizeToTray)
        {
            args.Cancel = true;
            WindowInterop.Hide(this);
            _tray?.Notify(AppInfo.Title, "タスクトレイで監視を続けます");
            return;
        }

        if (!_quitting)
        {
            int running = _session.RunningCount();
            if (running > 0)
            {
                args.Cancel = true;
                bool proceed = await ConfirmAsync("確認",
                    $"{running} 件のダウンロードが進行中です。中止して終了しますか？");
                if (proceed)
                {
                    _quitting = true;
                    Close();
                }
                return;
            }
        }
    }
}
