using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using WinRT.Interop;

namespace Dwnloader;

/// <summary>
/// クリップボード監視。
///
/// WPF版は HwndSource.AddHook で1行フックできたが、WinUI3の Window には
/// 相当する仕組みが無い。ここではウィンドウプロシージャを自前でサブクラス化
/// （SetWindowLongPtr で差し替え、元のプロシージャには CallWindowProc で
/// 必ず委譲する）して WM_CLIPBOARDUPDATE を捕まえる。
///
/// Windows は変化をウィンドウメッセージで通知してくれるので、ポーリングは
/// 一切不要（監視のためのスレッドも要らず、取りこぼしも減る）。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private const int GWLP_WNDPROC = -4;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate newProc);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtrRaw(IntPtr hWnd, int nIndex, IntPtr newProc);

    [DllImport("user32.dll")]
    private static extern IntPtr CallWindowProc(
        IntPtr prevProc, IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    private readonly Action<string> _onText;
    // ネイティブ側に渡すデリゲートはGCされると即クラッシュするので、
    // フィールドに保持して参照を生かし続ける。
    private readonly WndProcDelegate _wndProcDelegate;
    private IntPtr _hwnd;
    private IntPtr _originalWndProc;
    private bool _registered;
    private bool _subclassed;
    private bool _enabled;
    private string _lastText = "";

    public ClipboardWatcher(Action<string> onText)
    {
        _onText = onText;
        _wndProcDelegate = WndProc;
    }

    public bool IsEnabled => _enabled;

    /// <summary>ウィンドウが出来てから呼ぶ。メッセージの受け口をそこに間借りする。</summary>
    public void Attach(Window window)
    {
        _hwnd = WindowNative.GetWindowHandle(window);
        if (_hwnd == IntPtr.Zero) return;

        _originalWndProc = SetWindowLongPtr(_hwnd, GWLP_WNDPROC, _wndProcDelegate);
        _subclassed = _originalWndProc != IntPtr.Zero;

        _registered = AddClipboardFormatListener(_hwnd);
    }

    public void SetEnabled(bool enabled)
    {
        // 止めている間のコピーを再開時にまとめて拾わない
        if (enabled && !_enabled) _ = RefreshLastTextAsync();
        _enabled = enabled;
    }

    private async Task RefreshLastTextAsync() => _lastText = await ReadTextAsync() ?? "";

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_CLIPBOARDUPDATE && _enabled)
        {
            _ = OnClipboardUpdateAsync();
        }
        // 自分が処理しないメッセージは、必ず元のプロシージャに委譲する。
        // これを忘れると WinUI3 内部のメッセージ処理が壊れる。
        return CallWindowProc(_originalWndProc, hwnd, msg, wParam, lParam);
    }

    private async Task OnClipboardUpdateAsync()
    {
        var text = await ReadTextAsync();
        if (!string.IsNullOrEmpty(text) && text != _lastText)
        {
            _lastText = text;
            try
            {
                _onText(text);
            }
            catch (Exception)
            {
                // 監視は止めない。1件の失敗で常駐が死ぬほうが困る。
            }
        }
    }

    /// <summary>
    /// クリップボードのテキストを読む。他のプロセスが開いている間は失敗するので、
    /// 少し待って何度か試す。
    /// </summary>
    private static async Task<string?> ReadTextAsync()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                var view = Clipboard.GetContent();
                if (view.Contains(StandardDataFormats.Text))
                    return await view.GetTextAsync();
                return null;
            }
            catch (COMException)
            {
                await Task.Delay(30);
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    public void Dispose()
    {
        _enabled = false;
        if (_registered && _hwnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hwnd);
            _registered = false;
        }
        if (_subclassed && _hwnd != IntPtr.Zero)
        {
            SetWindowLongPtrRaw(_hwnd, GWLP_WNDPROC, _originalWndProc);
            _subclassed = false;
        }
    }
}
