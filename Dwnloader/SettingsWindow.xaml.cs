using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Dwnloader.Core;
using Dwnloader.Jobs;

namespace Dwnloader;

public partial class SettingsWindow : Window
{
    private readonly SettingsData _original;

    /// <summary>保存が押されたときだけ入る。押されなければ null のまま。</summary>
    public SettingsData? Result { get; private set; }

    public SettingsWindow(SettingsData current)
    {
        InitializeComponent();
        _original = current.Clone();
        Load(_original);
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
