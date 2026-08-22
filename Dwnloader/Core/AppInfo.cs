namespace Dwnloader.Core;

public static class AppInfo
{
    public const string Name = "dwnloader";
    public const string Title = "Gallery → PDF Downloader";
    public const string Version = "2.0.0";

    public static string WindowTitle => $"{Title} v{Version}";
}
