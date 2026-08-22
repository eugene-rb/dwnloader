using System.Diagnostics;

namespace Dwnloader.Jobs;

/// <summary>
/// yt-dlp の在り処を突き止める。
///
/// Python 版はプロセス内に import していたが、C# 版は外部プロセスとして呼ぶ。
/// 重い処理も異常終了もプロセスの外で起きるので、UI が巻き添えにならない。
/// </summary>
public static class YtDlp
{
    private static readonly object Gate = new();
    private static Resolved? _cached;

    public sealed record Resolved(string Exe, IReadOnlyList<string> Prefix, string Description);

    /// <summary>
    /// 実行方法を決める。優先順は
    ///   1. 設定で明示されたパス
    ///   2. PATH 上の yt-dlp.exe / yt-dlp
    ///   3. アプリと同じフォルダの yt-dlp.exe
    ///   4. python -m yt_dlp（Python 版と同じ環境をそのまま使える）
    /// </summary>
    public static Resolved? Locate(string configured)
    {
        lock (Gate)
        {
            if (_cached is not null && File.Exists(_cached.Exe)) return _cached;

            configured = (configured ?? "").Trim();
            if (configured.Length > 0 && File.Exists(configured))
                return _cached = new Resolved(configured, Array.Empty<string>(), configured);

            foreach (var name in new[] { "yt-dlp.exe", "yt-dlp" })
            {
                var found = FindOnPath(name);
                if (found is not null)
                    return _cached = new Resolved(found, Array.Empty<string>(), found);
            }

            var beside = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
            if (File.Exists(beside))
                return _cached = new Resolved(beside, Array.Empty<string>(), beside);

            foreach (var py in new[] { "python.exe", "python3.exe", "py.exe" })
            {
                var exe = FindOnPath(py);
                if (exe is null) continue;
                if (!HasYtDlpModule(exe)) continue;
                return _cached = new Resolved(exe, new[] { "-m", "yt_dlp" }, $"{exe} -m yt_dlp");
            }

            return null;
        }
    }

    /// <summary>導入されていないときに画面へ出す案内。</summary>
    public const string InstallHint =
        "yt-dlp が見つかりません。`winget install yt-dlp.yt-dlp` または " +
        "`pip install yt-dlp` で導入するか、設定でパスを指定してください。";

    private static string? FindOnPath(string fileName)
    {
        var paths = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in paths.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }       // PATH に不正な文字が混じっていることがある
        }
        return null;
    }

    private static bool HasYtDlpModule(string pythonExe)
    {
        try
        {
            var psi = new ProcessStartInfo(pythonExe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("import yt_dlp");

            using var proc = Process.Start(psi);
            if (proc is null) return false;
            if (!proc.WaitForExit(5000)) { KillTree(proc); return false; }
            return proc.ExitCode == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// 子プロセスごと確実に終わらせる。yt-dlp は ffmpeg を起動するので、
    /// 親だけ落とすと ffmpeg が残って CPU とファイルを掴み続ける。
    /// </summary>
    public static void KillTree(Process proc)
    {
        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException
                                       or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>ffmpeg の場所。無ければ空文字。</summary>
    public static string FfmpegPath()
    {
        foreach (var name in new[] { "ffmpeg.exe", "ffmpeg" })
        {
            var found = FindOnPath(name);
            if (found is not null) return found;
        }
        var beside = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        return File.Exists(beside) ? beside : "";
    }
}
