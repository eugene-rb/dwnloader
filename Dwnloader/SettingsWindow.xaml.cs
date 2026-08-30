using System.Globalization;
using Dwnloader.Auth;
using Dwnloader.Core;
using Dwnloader.Jobs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Dwnloader;

public partial class SettingsWindow : Window
{
    private readonly SettingsData _original;
    private readonly UpdateService? _updates;
    private readonly TaskCompletionSource<SettingsData?> _tcs = new();

    /// <summary>保存が押されたときだけ入る。押されなければ null のまま。</summary>
    public SettingsData? Result { get; private set; }

    private SettingsWindow(SettingsData current, UpdateService? updates)
    {
        InitializeComponent();

        var appWindow = WindowInterop.GetAppWindow(this);
        appWindow.Resize(new Windows.Graphics.SizeInt32(700, 600));

        Title = "設定";

        _original = current.Clone();
        _updates = updates;
        Load(_original);

        UpdateStatus.Text = updates is { IsSupported: true }
            ? $"現在 v{AppInfo.Version}"
            : UpdateService.UnsupportedReason;

        RefreshAccounts();

        Closed += (_, _) => _tcs.TrySetResult(Result);
    }

    /// <summary>
    /// 設定ダイアログを開き、閉じられるまで待つ。保存されれば新しい設定、
    /// キャンセルされれば null を返す。
    /// </summary>
    public static Task<SettingsData?> ShowAsync(SettingsData current, UpdateService? updates = null)
    {
        var window = new SettingsWindow(current, updates);
        window.Activate();
        return window._tcs.Task;
    }

    // ------------------------------------------------------------ ダイアログ

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

    // ------------------------------------------------------------ アカウント

    private async void PixivLogin_Click(object sender, RoutedEventArgs e) => await Login(CookieStore.Pixiv);
    private async void TwitterLogin_Click(object sender, RoutedEventArgs e) => await Login(CookieStore.Twitter);

    private void PixivLogout_Click(object sender, RoutedEventArgs e) => Logout(CookieStore.Pixiv);
    private void TwitterLogout_Click(object sender, RoutedEventArgs e) => Logout(CookieStore.Twitter);

    /// <summary>
    /// アプリの中にログイン画面を出す。
    ///
    /// ログイン結果は保存ボタンとは無関係にその場で保存する。ログインし直した後に
    /// 「キャンセル」を押してログインまで消えてしまうと、何をしたのか分からなくなる。
    /// </summary>
    private async Task Login(string site)
    {
        var result = await LoginWindow.ShowAsync(LoginTarget.For(site));
        RefreshAccounts();

        if (result is null) return;             // 自分で閉じた
        if (result.Ok)
        {
            await ShowMessageAsync("ログイン", result.Message);
            return;
        }

        await ShowMessageAsync("ログイン",
            result.Message + Environment.NewLine + Environment.NewLine +
            "「詳細設定」から Cookie を手で指定することもできます。");
    }

    private void Logout(string site)
    {
        CookieStore.Clear(site);
        RefreshAccounts();
    }

    /// <summary>ログイン状態の表示を作り直す。</summary>
    private void RefreshAccounts()
    {
        PixivStatus.Text = StatusFor(
            CookieStore.Pixiv, PixivCookie.Text.Trim().Length > 0,
            "R-18作品も取得できます。", "手入力の PHPSESSID を使います。");

        // cookies.txt は「書いてあるか」ではなく「実在するか」で見る。
        // MediaJob 側も、存在しないパスは無視してログインの分に切り替える。
        var manualCookies = MediaCookiesFile.Text.Trim();
        TwitterStatus.Text = StatusFor(
            CookieStore.Twitter, manualCookies.Length > 0 && File.Exists(manualCookies),
            "センシティブな投稿も取得できます。", "手入力の cookies.txt を使います。");
    }

    /// <summary>
    /// 手入力があればそちらが優先されるので、ログイン済みでも
    /// 「実際に使われるのはどちらか」が分かるように出す。
    /// </summary>
    private static string StatusFor(string site, bool manualInUse, string loggedInNote, string manualNote)
    {
        if (manualInUse) return "設定済み — " + manualNote;

        var at = CookieStore.SavedAt(site);
        return at is null
            ? "未ログイン — 公開されているものだけ取得できます。"
            : $"ログイン済み（{at:yyyy/MM/dd HH:mm}） — {loggedInNote}";
    }

    /// <summary>手で更新を確認する。見つかったら本体の「更新」ボタンから適用する。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_updates is null || !_updates.IsSupported)
        {
            UpdateStatus.Text = UpdateService.UnsupportedReason;
            return;
        }

        UpdateStatus.Text = "確認しています…";
        try
        {
            var found = await _updates.CheckAsync();
            UpdateStatus.Text = found is null
                ? $"最新です（v{AppInfo.Version}）"
                : $"v{found} が利用できます。この画面を閉じて、下の「更新」を押してください。";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = $"確認できませんでした: {ex.Message}";
        }
    }

    private void Load(SettingsData s)
    {
        OutputDir.Text = s.OutputDir;
        VideoDir.Text = s.VideoDir;
        AudioDir.Text = s.AudioDir;
        SiteSubfolder.IsChecked = s.SiteSubfolder;
        // 1行に1フォルダで見せる（設定ファイル側はカンマ区切りも受ける）
        ScanDirs.Text = string.Join(Environment.NewLine,
                                    LibraryIndex.ParseFolderList(s.ScanDirs));
        FilenameTemplate.Text = s.FilenameTemplate;
        FilenameMaxLen.Value = s.FilenameMaxLen;

        WatchClipboard.IsChecked = s.WatchClipboard;
        AutoStart.IsChecked = s.AutoStart;
        SkipDownloaded.IsChecked = s.SkipDownloaded;
        GalleryWorkers.Value = s.GalleryWorkers;
        ImageWorkers.Value = s.ImageWorkers;
        VideoWorkers.Value = s.VideoWorkers;
        Retries.Value = s.Retries;
        Timeout.Value = s.Timeout;
        ClipboardBlacklist.Text = s.ClipboardBlacklist;
        ClipboardWhitelist.Text = s.ClipboardWhitelist;

        SelectByText(PreferFormat, s.PreferFormat);
        JpegQuality.Value = s.JpegQuality;
        KeepImages.IsChecked = s.KeepImages;
        PixivCookie.Text = s.PixivCookie;

        SelectByText(VideoQuality, s.VideoQuality);
        SelectByText(AudioFormat, s.AudioFormat);
        AudioBitrate.Value = s.AudioBitrate;
        PlaylistAll.IsChecked = s.PlaylistAll;
        MediaCookiesFile.Text = s.MediaCookiesFile;
        YtDlpPath.Text = s.YtDlpPath;

        var found = YtDlp.Locate(s.YtDlpPath);
        YtDlpStatus.Text = found is not null
            ? $"検出: {found.Description}"
            : "yt-dlp が見つかりません。動画・音声は取得できません。";

        Notify.IsChecked = s.Notify;
        MinimizeToTray.IsChecked = s.MinimizeToTray;
        AlwaysOnTop.IsChecked = s.AlwaysOnTop;
        ConfigPathText.Text = $"設定の保存場所: {AppPaths.ConfigDir}";
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _original.Clone();

        s.OutputDir = OutputDir.Text.Trim();
        s.VideoDir = VideoDir.Text.Trim();
        s.AudioDir = AudioDir.Text.Trim();
        s.SiteSubfolder = SiteSubfolder.IsChecked == true;
        s.ScanDirs = string.Join(Environment.NewLine,
                                 LibraryIndex.ParseFolderList(ScanDirs.Text));
        s.FilenameTemplate = FilenameTemplate.Text;
        s.FilenameMaxLen = NumberOr(FilenameMaxLen, s.FilenameMaxLen);

        s.WatchClipboard = WatchClipboard.IsChecked == true;
        s.AutoStart = AutoStart.IsChecked == true;
        s.SkipDownloaded = SkipDownloaded.IsChecked == true;
        s.GalleryWorkers = NumberOr(GalleryWorkers, s.GalleryWorkers);
        s.ImageWorkers = NumberOr(ImageWorkers, s.ImageWorkers);
        s.VideoWorkers = NumberOr(VideoWorkers, s.VideoWorkers);
        s.Retries = NumberOr(Retries, s.Retries);
        s.Timeout = NumberOr(Timeout, (int)s.Timeout);
        s.ClipboardBlacklist = ClipboardBlacklist.Text;
        s.ClipboardWhitelist = ClipboardWhitelist.Text;

        s.PreferFormat = SelectedText(PreferFormat, s.PreferFormat);
        s.JpegQuality = NumberOr(JpegQuality, s.JpegQuality);
        s.KeepImages = KeepImages.IsChecked == true;
        s.PixivCookie = PixivCookie.Text.Trim();

        s.VideoQuality = SelectedText(VideoQuality, s.VideoQuality);
        s.AudioFormat = SelectedText(AudioFormat, s.AudioFormat);
        s.AudioBitrate = NumberOr(AudioBitrate, s.AudioBitrate);
        s.PlaylistAll = PlaylistAll.IsChecked == true;
        s.MediaCookiesFile = MediaCookiesFile.Text.Trim();
        s.YtDlpPath = YtDlpPath.Text.Trim();

        s.Notify = Notify.IsChecked == true;
        s.MinimizeToTray = MinimizeToTray.IsChecked == true;
        s.AlwaysOnTop = AlwaysOnTop.IsChecked == true;

        Result = s;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    // ------------------------------------------------------------ 参照ボタン

    private async void PickOutput_Click(object sender, RoutedEventArgs e) => await PickFolderAsync(OutputDir);
    private async void PickVideo_Click(object sender, RoutedEventArgs e) => await PickFolderAsync(VideoDir);
    private async void PickAudio_Click(object sender, RoutedEventArgs e) => await PickFolderAsync(AudioDir);

    private async Task PickFolderAsync(TextBox target)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null) target.Text = folder.Path;
    }

    /// <summary>フォルダを選んで一覧の末尾に足す。手で打つより間違えにくい。</summary>
    private async void AddScanDir_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var current = LibraryIndex.ParseFolderList(ScanDirs.Text);
        if (!current.Contains(folder.Path, StringComparer.OrdinalIgnoreCase))
            current.Add(folder.Path);
        ScanDirs.Text = string.Join(Environment.NewLine, current);
    }

    private async void PickCookies_Click(object sender, RoutedEventArgs e)
        => await PickFileAsync(MediaCookiesFile, ".txt");

    private async void PickYtDlp_Click(object sender, RoutedEventArgs e)
        => await PickFileAsync(YtDlpPath, ".exe");

    private async Task PickFileAsync(TextBox target, string extension)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(extension);
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        var file = await picker.PickSingleFileAsync();
        if (file is not null) target.Text = file.Path;
    }

    // ------------------------------------------------------------ 小道具

    private static int NumberOr(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : (int)box.Value;

    private static void SelectByText(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items.Cast<ComboBoxItem>())
        {
            if ((item.Content as string) == value) { box.SelectedItem = item; return; }
        }
        box.SelectedIndex = 0;
    }

    private static string SelectedText(ComboBox box, string fallback) =>
        box.SelectedItem is ComboBoxItem { Content: string text } ? text : fallback;
}
