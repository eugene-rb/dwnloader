using Dwnloader.Core;
using Velopack;
using Velopack.Sources;

namespace Dwnloader;

/// <summary>
/// GitHub のリリースを見て新しい版を取ってくる。
///
/// 適用はプロセスの入れ替えなので、走っているダウンロードがあるときは行わない
/// （途中の .part や作りかけの PDF が道連れになる）。適用の判断は必ず人に委ねる。
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;

    /// <summary>インストーラ経由で入れた場合だけ更新できる。</summary>
    public bool IsSupported { get; }

    /// <summary>見つかった新しい版。無ければ null。</summary>
    public string? AvailableVersion => _pending?.TargetFullRelease.Version.ToString();

    /// <summary>ダウンロード済みで、あとは適用するだけの状態か。</summary>
    public bool IsDownloaded { get; private set; }

    public UpdateService()
    {
        try
        {
            _manager = new UpdateManager(BuildSource());
            IsSupported = _manager.IsInstalled;
        }
        catch (Exception)
        {
            // 更新の仕組みが使えなくても、アプリ自体は普通に動かす
            _manager = null;
            IsSupported = false;
        }
    }

    /// <summary>
    /// 新しい版があるか調べる。見つかれば版番号、無ければ null。
    /// 通信するので、必ず裏で呼ぶこと。
    /// </summary>
    public async Task<string?> CheckAsync()
    {
        if (_manager is null || !IsSupported) return null;

        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        _pending = info;
        IsDownloaded = false;
        return info?.TargetFullRelease.Version.ToString();
    }

    /// <summary>見つかった版を落としてくる。適用はまだしない。</summary>
    public async Task<bool> DownloadAsync(Action<int>? onProgress = null)
    {
        if (_manager is null || _pending is null) return false;

        await _manager.DownloadUpdatesAsync(_pending, p => onProgress?.Invoke(p))
                      .ConfigureAwait(false);
        IsDownloaded = true;
        return true;
    }

    /// <summary>
    /// 適用して再起動する。この呼び出しは戻ってこない。
    /// 呼ぶ前に、走っているダウンロードが無いことと、保存すべきものを
    /// 書き終えていることを確かめること。
    /// </summary>
    public void ApplyAndRestart()
    {
        if (_manager is null || _pending is null) return;
        _manager.ApplyUpdatesAndRestart(_pending);
    }

    /// <summary>
    /// 更新の入手元。既定は GitHub のリリース。
    ///
    /// 環境変数 DWNLOADER_UPDATE_SOURCE にフォルダを指すと、そちらを見る。
    /// 更新の一連の流れ（確認→取得→再起動）は、2つの版を公開しないと
    /// 試せない。公開せずに手元で通すための差し込み口として置いてある。
    /// </summary>
    private static IUpdateSource BuildSource()
    {
        var local = Environment.GetEnvironmentVariable("DWNLOADER_UPDATE_SOURCE");
        if (!string.IsNullOrWhiteSpace(local) && Directory.Exists(local))
            return new SimpleFileSource(new DirectoryInfo(local));

        return new GithubSource(AppInfo.RepositoryUrl, accessToken: null, prerelease: false);
    }

    /// <summary>更新できない理由を人向けに説明する。</summary>
    public static string UnsupportedReason =>
        "インストーラで入れた場合のみ自動更新できます。"
        + $"最新版は {AppInfo.RepositoryUrl}/releases から入手できます。";
}
