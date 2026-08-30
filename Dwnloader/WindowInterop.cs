using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace Dwnloader;

/// <summary>
/// WinUI3 の Window には WPF の Topmost/WindowState/Show/Hide に相当する
/// 単純なプロパティが無く、すべて AppWindow 経由になる。ここに集約する。
/// </summary>
public static class WindowInterop
{
    public static AppWindow GetAppWindow(Window window)
    {
        var hwnd = WindowNative.GetWindowHandle(window);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    public static void SetAlwaysOnTop(Window window, bool enabled)
    {
        if (GetAppWindow(window).Presenter is OverlappedPresenter p) p.IsAlwaysOnTop = enabled;
    }

    public static bool IsMinimized(Window window) =>
        GetAppWindow(window).Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };

    public static void Restore(Window window)
    {
        if (GetAppWindow(window).Presenter is OverlappedPresenter p) p.Restore();
    }

    public static void Hide(Window window) => GetAppWindow(window).Hide();

    public static void ShowNormal(Window window)
    {
        var appWindow = GetAppWindow(window);
        appWindow.Show();
        if (appWindow.Presenter is OverlappedPresenter p) p.Restore();
    }
}
