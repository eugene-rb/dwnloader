using System.Text.Json.Serialization;

namespace Dwnloader.Core;

/// <summary>
/// 設定の実体。値の変更ごとにディスクへ書く（書き込み自体はまとめられる）。
/// JSON のキーは Python 版と同じ snake_case にしてある。設定ファイルは
/// 人が手で開いて直すことがあるため、見た目を変えない。
/// </summary>
public sealed class SettingsData
{
    [JsonPropertyName("output_dir")] public string OutputDir { get; set; } = AppPaths.DefaultOutputDir;
    [JsonPropertyName("site_subfolder")] public bool SiteSubfolder { get; set; }
    /// <summary>
    /// 重複を調べるときに、保存先に加えて見るフォルダ（1行に1つ）。
    /// 過去に別の場所へ集めた分を「取得済み」として扱える。
    /// </summary>
    [JsonPropertyName("scan_dirs")] public string ScanDirs { get; set; } = "";
    [JsonPropertyName("filename_template")] public string FilenameTemplate { get; set; } = "{title} [{artist}] ({site}-{id})";
    [JsonPropertyName("filename_max_len")] public int FilenameMaxLen { get; set; } = 120;

    [JsonPropertyName("watch_clipboard")] public bool WatchClipboard { get; set; } = true;
    /// <summary>検出したら確認なしでダウンロードを開始する。</summary>
    [JsonPropertyName("auto_start")] public bool AutoStart { get; set; } = true;
    /// <summary>履歴にあるものは飛ばす。</summary>
    [JsonPropertyName("skip_downloaded")] public bool SkipDownloaded { get; set; } = true;

    /// <summary>
    /// クリップボード監視は既知サイト以外も yt-dlp に賭ける（ブラックリスト式）。
    /// ここに列挙したドメイン（完全一致。サブドメインは別ドメインとして区別する）
    /// だけは対象外にする。手動のURL貼り付けはこの影響を受けない。
    /// </summary>
    [JsonPropertyName("clipboard_blacklist")] public string ClipboardBlacklist { get; set; } =
        "google.com, www.google.com, bing.com, www.bing.com, " +
        "duckduckgo.com, yahoo.co.jp, www.yahoo.co.jp, " +
        "github.com, gitlab.com, stackoverflow.com, " +
        "en.wikipedia.org, ja.wikipedia.org, " +
        "docs.google.com, drive.google.com, mail.google.com, " +
        "outlook.live.com, outlook.office.com, outlook.office365.com, " +
        "notion.so, www.notion.so, " +
        "amazon.co.jp, www.amazon.co.jp, amazon.com, www.amazon.com, " +
        "paypal.com, www.paypal.com";

    /// <summary>取得に実際に成功したドメイン（完全一致）。自動で増える。除外リストより優先。</summary>
    [JsonPropertyName("clipboard_whitelist")] public string ClipboardWhitelist { get; set; } = "";

    /// <summary>同時にPDF化する作品数。</summary>
    [JsonPropertyName("gallery_workers")] public int GalleryWorkers { get; set; } = 2;
    /// <summary>1作品内で同時取得する画像数。</summary>
    [JsonPropertyName("image_workers")] public int ImageWorkers { get; set; } = 5;
    [JsonPropertyName("retries")] public int Retries { get; set; } = 3;
    [JsonPropertyName("timeout")] public double Timeout { get; set; } = 30;
    /// <summary>Optional proxy URL for app HTTP and yt-dlp. Empty means system/default network.</summary>
    [JsonPropertyName("proxy_url")] public string ProxyUrl { get; set; } = "";

    /// <summary>hitomi の画像形式優先: avif / webp。</summary>
    [JsonPropertyName("prefer_format")] public string PreferFormat { get; set; } = "avif";
    [JsonPropertyName("jpeg_quality")] public int JpegQuality { get; set; } = 90;
    /// <summary>変換前の元画像も残す。</summary>
    [JsonPropertyName("keep_images")] public bool KeepImages { get; set; }

    /// <summary>PHPSESSID（R-18作品に必要）。</summary>
    [JsonPropertyName("pixiv_cookie")] public string PixivCookie { get; set; } = "";

    [JsonPropertyName("video_dir")] public string VideoDir { get; set; } = AppPaths.DefaultVideoDir;
    [JsonPropertyName("audio_dir")] public string AudioDir { get; set; } = AppPaths.DefaultAudioDir;
    /// <summary>cookies.txt のパス（ログイン必須の動画に必要）。</summary>
    [JsonPropertyName("media_cookies_file")] public string MediaCookiesFile { get; set; } = "";
    /// <summary>追加種別トグルの現在値: "video" / "audio"。</summary>
    [JsonPropertyName("media_kind")] public string MediaKindValue { get; set; } = MediaKind.Video;
    /// <summary>再生リスト・チャンネルのURLを全部取得するか。既定は先頭の1本だけ。</summary>
    [JsonPropertyName("playlist_all")] public bool PlaylistAll { get; set; }
    /// <summary>best / 1080 / 720 / 480。</summary>
    [JsonPropertyName("video_quality")] public string VideoQuality { get; set; } = "best";
    /// <summary>mp3 / m4a / opus。</summary>
    [JsonPropertyName("audio_format")] public string AudioFormat { get; set; } = "mp3";
    [JsonPropertyName("audio_bitrate")] public int AudioBitrate { get; set; } = 192;
    /// <summary>同時ダウンロード数（yt-dlp）。</summary>
    [JsonPropertyName("video_workers")] public int VideoWorkers { get; set; } = 2;

    /// <summary>yt-dlp の実行ファイル。空なら自動検出。</summary>
    [JsonPropertyName("ytdlp_path")] public string YtDlpPath { get; set; } = "";

    [JsonPropertyName("notify")] public bool Notify { get; set; } = true;
    [JsonPropertyName("minimize_to_tray")] public bool MinimizeToTray { get; set; }
    [JsonPropertyName("always_on_top")] public bool AlwaysOnTop { get; set; }

    public SettingsData Clone() => (SettingsData)MemberwiseClone();
}

/// <summary>
/// 設定へのスレッド安全な入口。読み出しは Current のスナップショットを返し、
/// 走行中のジョブが途中で値を書き換えられないようにする。
/// </summary>
public sealed class AppSettings : IDisposable
{
    private readonly object _gate = new();
    private readonly DebouncedWriter<SettingsData> _writer;
    private SettingsData _data;

    public AppSettings()
    {
        _data = Json.Read<SettingsData>(AppPaths.SettingsPath) ?? new SettingsData();
        _writer = new DebouncedWriter<SettingsData>(AppPaths.SettingsPath);
        Normalize(_data);
    }

    /// <summary>現在値のスナップショット。呼び出し側はこれを持ち回ってよい。</summary>
    public SettingsData Current
    {
        get { lock (_gate) return _data; }
    }

    /// <summary>値を差し替える。壊れた値は既定へ寄せてから保存する。</summary>
    public void Update(Action<SettingsData> mutate)
    {
        lock (_gate)
        {
            var copy = _data.Clone();
            mutate(copy);
            Normalize(copy);
            _data = copy;
            _writer.Schedule(copy);
        }
    }

    public void Replace(SettingsData incoming)
    {
        lock (_gate)
        {
            Normalize(incoming);
            _data = incoming;
            _writer.Schedule(incoming);
        }
    }

    /// <summary>
    /// 設定画面から来た値や、手で編集された設定ファイルを安全な範囲へ収める。
    /// 0 や負数がそのまま同時実行数やタイムアウトに渡ると、無限待ちや例外になる。
    /// </summary>
    private static void Normalize(SettingsData s)
    {
        s.GalleryWorkers = Math.Clamp(s.GalleryWorkers, 1, 16);
        s.ImageWorkers = Math.Clamp(s.ImageWorkers, 1, 32);
        s.VideoWorkers = Math.Clamp(s.VideoWorkers, 1, 16);
        s.Retries = Math.Clamp(s.Retries, 1, 10);
        s.Timeout = Math.Clamp(s.Timeout, 5, 600);
        s.JpegQuality = Math.Clamp(s.JpegQuality, 40, 100);
        s.FilenameMaxLen = Math.Clamp(s.FilenameMaxLen, 40, 200);
        s.AudioBitrate = Math.Clamp(s.AudioBitrate, 64, 320);

        if (s.PreferFormat is not ("avif" or "webp")) s.PreferFormat = "avif";
        if (s.VideoQuality is not ("best" or "1080" or "720" or "480")) s.VideoQuality = "best";
        if (s.AudioFormat is not ("mp3" or "m4a" or "opus")) s.AudioFormat = "mp3";
        s.MediaKindValue = MediaKind.Normalize(s.MediaKindValue);

        // 空欄なら既定へ戻す（保存先が無い状態を作らない）
        if (string.IsNullOrWhiteSpace(s.OutputDir)) s.OutputDir = AppPaths.DefaultOutputDir;
        if (string.IsNullOrWhiteSpace(s.VideoDir)) s.VideoDir = AppPaths.DefaultVideoDir;
        if (string.IsNullOrWhiteSpace(s.AudioDir)) s.AudioDir = AppPaths.DefaultAudioDir;
        s.FilenameTemplate ??= "";
        s.ClipboardBlacklist ??= "";
        s.ClipboardWhitelist ??= "";
        s.ScanDirs ??= "";
        s.PixivCookie ??= "";
        s.MediaCookiesFile ??= "";
        s.YtDlpPath ??= "";
        s.ProxyUrl = NormalizeProxyUrl(s.ProxyUrl);
    }

    private static string NormalizeProxyUrl(string? value)
    {
        var proxy = (value ?? "").Trim();
        if (proxy.Length == 0) return "";
        if (!Uri.TryCreate(proxy, UriKind.Absolute, out var uri)) return "";

        return uri.Scheme.ToLowerInvariant() switch
        {
            "http" or "https" or "socks4" or "socks4a" or "socks5" => proxy,
            _ => "",
        };
    }

    /// <summary>動画・音声の保存先。</summary>
    public static string MediaDir(SettingsData s, string kind) =>
        kind == MediaKind.Audio ? s.AudioDir : s.VideoDir;

    public void Flush() => _writer.Flush();
    public void Dispose() => _writer.Dispose();
}
