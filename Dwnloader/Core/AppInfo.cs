using System.Reflection;

namespace Dwnloader.Core;

public static class AppInfo
{
    public const string Name = "dwnloader";
    public const string Title = "Gallery → PDF Downloader";

    /// <summary>更新の確認先。</summary>
    public const string RepositoryUrl = "https://github.com/eugene-rb/dwnloader";

    /// <summary>
    /// 表示するバージョン。csproj の &lt;Version&gt; から取る。
    /// 定数で持つと、自動更新でファイルだけ新しくなったのに題名が古いまま、
    /// という食い違いが起きる。
    /// </summary>
    public static string Version { get; } = ReadVersion();

    public static string WindowTitle => $"{Title} v{Version}";

    private static string ReadVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();

        // InformationalVersion には "2.1.0+<コミットハッシュ>" が入ることがある
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                           ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            int plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : $"{version.Major}.{version.Minor}.{version.Build}";
    }
}
