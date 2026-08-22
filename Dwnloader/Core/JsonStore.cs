using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Dwnloader.Core;

/// <summary>保存先。設定・履歴・キューを %APPDATA%\dwnloader2\ に置く。</summary>
public static class AppPaths
{
    public const string AppName = "dwnloader2";

    public static string ConfigDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                                  Environment.SpecialFolderOption.DoNotVerify),
        AppName);

    public static string SettingsPath => Path.Combine(ConfigDir, "settings.json");
    public static string HistoryPath => Path.Combine(ConfigDir, "history.json");
    public static string QueuePath => Path.Combine(ConfigDir, "queue.json");

    /// <summary>ログインで受け取った Cookie（DPAPI で暗号化して置く）。</summary>
    public static string AccountsPath => Path.Combine(ConfigDir, "accounts.dat");

    /// <summary>
    /// 上の accounts.dat から組み立てる yt-dlp 用の cookies.txt。
    /// yt-dlp はファイルからしか Cookie を読めないので、暗号化したままでは渡せない。
    /// </summary>
    public static string CookiesTxtPath => Path.Combine(ConfigDir, "yt-dlp-cookies.txt");

    /// <summary>アプリ内ログイン画面の作業フォルダ。ログイン状態はここに残る。</summary>
    public static string WebViewDir => Path.Combine(ConfigDir, "webview");

    public static string DefaultOutputDir => Path.Combine(UserHome, "Downloads", "gallery-pdf");
    public static string DefaultVideoDir => Path.Combine(UserHome, "Downloads", AppName, "video");
    public static string DefaultAudioDir => Path.Combine(UserHome, "Downloads", AppName, "audio");

    private static string UserHome => Environment.GetFolderPath(
        Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
}

public static class Json
{
    /// <summary>
    /// BOM を付けない UTF-8。設定ファイルは人が開いて直すことがあり、
    /// BOM が付いていると他のツールが読めないことがある
    /// （.NET の Encoding.UTF8 は既定で BOM を書いてしまう）。
    /// </summary>
    private static readonly System.Text.UTF8Encoding Utf8NoBom = new(false);

    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // 日本語をそのまま読める形で書く（設定ファイルは手で編集されうる）
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// 一時ファイル経由で書く。書き込み中に落ちても既存を壊さない。
    /// 保存の失敗でアプリを止めはしない（設定が保存できないより、動き続ける方が大事）。
    /// </summary>
    public static void WriteAtomic<T>(string path, T data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(data, Options), Utf8NoBom);
            // File.Move(overwrite:true) は同一ボリューム上では置換として振る舞う
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException
                                       or NotSupportedException)
        {
        }
    }

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;                    // 壊れていたら既定値で立ち上がる
        }
    }
}

/// <summary>
/// 短い間隔に集中した書き込み要求を1回にまとめてディスクへ書く。
///
/// 大量の動画URLが次々に完了・失敗すると保存が連打される。呼び出しごとに
/// 同期でディスクへ書いていると、①I/Oがジョブのスレッドを塞ぎ ②複数スレッドの
/// 書き込みが競合して古い内容が最後に残ることがある。直近の1回だけを
/// タイマーにまとめて書かせ、両方を避ける。
/// </summary>
public sealed class DebouncedWriter<T> : IDisposable where T : class
{
    private readonly string _path;
    private readonly TimeSpan _delay;
    private readonly object _gate = new();
    private T? _pending;
    private Timer? _timer;
    private bool _disposed;

    public DebouncedWriter(string path, double delaySeconds = 0.3)
    {
        _path = path;
        _delay = TimeSpan.FromSeconds(delaySeconds);
    }

    public void Schedule(T data)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = data;
            _timer ??= new Timer(_ => Fire(), null, _delay, Timeout.InfiniteTimeSpan);
        }
    }

    private void Fire()
    {
        T? data;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_pending is null) return;
            data = _pending;
            _pending = null;
        }
        Json.WriteAtomic(_path, data);
    }

    /// <summary>待たずに確実に書く（終了処理用）。</summary>
    public void Flush()
    {
        T? data;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_pending is null) return;
            data = _pending;
            _pending = null;
        }
        Json.WriteAtomic(_path, data);
    }

    public void Dispose()
    {
        Flush();
        lock (_gate) { _disposed = true; }
    }
}
