using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Dwnloader.Auth;
using Dwnloader.Core;
using Dwnloader.Jobs;

namespace Dwnloader;

public partial class SettingsWindow : Window
{
    private readonly SettingsData _original;
    private readonly UpdateService? _updates;

    /// <summary>保存が押されたときだけ入る。押されなければ null のまま。</summary>
    public SettingsData? Result { get; private set; }

    public SettingsWindow(SettingsData current, UpdateService? updates = null)
    {
        InitializeComponent();
        _original = current.Clone();
        _updates = updates;
        Load(_original);

        UpdateStatus.Text = updates is { IsSupported: true }
            ? $"現在 v{AppInfo.Version}"
            : UpdateService.UnsupportedReason;

        RefreshAccounts();
    }

    // ------------------------------------------------------------ アカウント

    private void PixivLogin_Click(object sender, RoutedEventArgs e) => Login(CookieStore.Pixiv);
    private void TwitterLogin_Click(object sender, RoutedEventArgs e) => Login(CookieStore.Twitter);

    private void PixivLogout_Click(object sender, RoutedEventArgs e) => Logout(CookieStore.Pixiv);
    private void TwitterLogout_Click(object sender, RoutedEventArgs e) => Logout(CookieStore.Twitter);

    /// <summary>
    /// アプリの中にログイン画面を出す。
    ///
    /// ログイン結果は保存ボタンとは無関係にその場で保存する。ログインし直した後に
    /// 「キャンセル」を押してログインまで消えてしまうと、何をしたのか分からなくなる。
    /// </summary>
    private void Login(string site)
    {
        var result = LoginWindow.Show(this, LoginTarget.For(site));
        RefreshAccounts();

        if (result is null) return;             // 自分で閉じた
        if (result.Ok)
        {
            MessageBox.Show(this, result.Message, "ログイン",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        MessageBox.Show(this,
                        result.Message + Environment.NewLine + Environment.NewLine +
                        "「詳細設定」から Cookie を手で指定することもできます。",
                        "ログイン", MessageBoxButton.OK, MessageBoxImage.Warning);
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
        FilenameMaxLen.Text = s.FilenameMaxLen.ToString(CultureInfo.InvariantCulture);

        WatchClipboard.IsChecked = s.WatchClipboard;
        AutoStart.IsChecked = s.AutoStart;
        SkipDownloaded.IsChecked = s.SkipDownloaded;
        GalleryWorkers.Text = s.GalleryWorkers.ToString(CultureInfo.InvariantCulture);
        ImageWorkers.Text = s.ImageWorkers.ToString(CultureInfo.InvariantCulture);
        VideoWorkers.Text = s.VideoWorkers.ToString(CultureInfo.InvariantCulture);
        Retries.Text = s.Retries.ToString(CultureInfo.InvariantCulture);
        Timeout.Text = s.Timeout.ToString("F0", CultureInfo.InvariantCulture);
        ClipboardBlacklist.Text = s.ClipboardBlacklist;
        ClipboardWhitelist.Text = s.ClipboardWhitelist;

        SelectByText(PreferFormat, s.PreferFormat);
        JpegQuality.Text = s.JpegQuality.ToString(CultureInfo.InvariantCulture);
        KeepImages.IsChecked = s.KeepImages;
        PixivCookie.Text = s.PixivCookie;

        SelectByText(VideoQuality, s.VideoQuality);
        SelectByText(AudioFormat, s.AudioFormat);
        AudioBitrate.Text = s.AudioBitrate.ToString(CultureInfo.InvariantCulture);
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
        s.FilenameMaxLen = ParseInt(FilenameMaxLen.Text, s.FilenameMaxLen);

        s.WatchClipboard = WatchClipboard.IsChecked == true;
        s.AutoStart = AutoStart.IsChecked == true;
        s.SkipDownloaded = SkipDownloaded.IsChecked == true;
        s.GalleryWorkers = ParseInt(GalleryWorkers.Text, s.GalleryWorkers);
        s.ImageWorkers = ParseInt(ImageWorkers.Text, s.ImageWorkers);
        s.VideoWorkers = ParseInt(VideoWorkers.Text, s.VideoWorkers);
        s.Retries = ParseInt(Retries.Text, s.Retries);
        s.Timeout = ParseInt(Timeout.Text, (int)s.Timeout);
        s.ClipboardBlacklist = ClipboardBlacklist.Text;
        s.ClipboardWhitelist = ClipboardWhitelist.Text;

        s.PreferFormat = SelectedText(PreferFormat, s.PreferFormat);
        s.JpegQuality = ParseInt(JpegQuality.Text, s.JpegQuality);
        s.KeepImages = KeepImages.IsChecked == true;
        s.PixivCookie = PixivCookie.Text.Trim();

        s.VideoQuality = SelectedText(VideoQuality, s.VideoQuality);
        s.AudioFormat = SelectedText(AudioFormat, s.AudioFormat);
        s.AudioBitrate = ParseInt(AudioBitrate.Text, s.AudioBitrate);
        s.PlaylistAll = PlaylistAll.IsChecked == true;
        s.MediaCookiesFile = MediaCookiesFile.Text.Trim();
        s.YtDlpPath = YtDlpPath.Text.Trim();

        s.Notify = Notify.IsChecked == true;
        s.MinimizeToTray = MinimizeToTray.IsChecked == true;
        s.AlwaysOnTop = AlwaysOnTop.IsChecked == true;

        Result = s;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    // ------------------------------------------------------------ 参照ボタン

    private void PickOutput_Click(object sender, RoutedEventArgs e)
        => PickFolder(OutputDir);
    private void PickVideo_Click(object sender, RoutedEventArgs e)
        => PickFolder(VideoDir);
    private void PickAudio_Click(object sender, RoutedEventArgs e)
        => PickFolder(AudioDir);

    private static void PickFolder(TextBox target)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            SelectedPath = target.Text,
            UseDescriptionForTitle = true,
            Description = "保存先を選んでください",
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            target.Text = dialog.SelectedPath;
    }

    /// <summary>フォルダを選んで一覧の末尾に足す。手で打つより間違えにくい。</summary>
    private void AddScanDir_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            UseDescriptionForTitle = true,
            Description = "重複の確認に使うフォルダを選んでください",
        };
        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

        var current = LibraryIndex.ParseFolderList(ScanDirs.Text);
        if (!current.Contains(dialog.SelectedPath, StringComparer.OrdinalIgnoreCase))
            current.Add(dialog.SelectedPath);
        ScanDirs.Text = string.Join(Environment.NewLine, current);
    }

    private void PickCookies_Click(object sender, RoutedEventArgs e)
        => PickFile(MediaCookiesFile, "テキストファイル (*.txt)|*.txt|すべてのファイル (*.*)|*.*");

    private void PickYtDlp_Click(object sender, RoutedEventArgs e)
        => PickFile(YtDlpPath, "実行ファイル (*.exe)|*.exe|すべてのファイル (*.*)|*.*");

    private static void PickFile(TextBox target, string filter)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = filter };
        if (target.Text.Length > 0)
        {
            try { dialog.InitialDirectory = System.IO.Path.GetDirectoryName(target.Text); }
            catch (ArgumentException) { }
        }
        if (dialog.ShowDialog() == true) target.Text = dialog.FileName;
    }

    // ------------------------------------------------------------ 小道具

    private static int ParseInt(string text, int fallback) =>
        int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
            ? v : fallback;

    private static void SelectByText(ComboBox box, string value)
    {
        foreach (ComboBoxItem item in box.Items)
        {
            if ((item.Content as string) == value) { box.SelectedItem = item; return; }
        }
        box.SelectedIndex = 0;
    }

    private static string SelectedText(ComboBox box, string fallback) =>
        box.SelectedItem is ComboBoxItem { Content: string text } ? text : fallback;
}
