using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Dwnloader;

/// <summary>
/// クリップボード監視。
///
/// Python 版は 0.5 秒ごとに変化を問い合わせていたが、Windows は変化を
/// ウィンドウメッセージで通知してくれる。ポーリングを一切やめられるので、
/// 監視のためのスレッドも要らず、取りこぼしも減る。
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    private readonly Action<string> _onText;
    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;
    private bool _enabled;
    private string _lastText = "";

    public ClipboardWatcher(Action<string> onText) => _onText = onText;

    public bool IsEnabled => _enabled;

    /// <summary>ウィンドウが出来てから呼ぶ。メッセージの受け口をそこに間借りする。</summary>
    public void Attach(System.Windows.Window window)
    {
        var helper = new WindowInteropHelper(window);
        _hwnd = helper.Handle;
        if (_hwnd == IntPtr.Zero) return;

        _source = HwndSource.FromHwnd(_hwnd);
        _source?.AddHook(WndProc);

        _registered = AddClipboardFormatListener(_hwnd);
    }

    public void SetEnabled(bool enabled)
    {
        // 止めている間のコピーを再開時にまとめて拾わない
        if (enabled && !_enabled) _lastText = ReadText() ?? "";
        _enabled = enabled;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_CLIPBOARDUPDATE || !_enabled) return IntPtr.Zero;

        var text = ReadText();
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
        return IntPtr.Zero;
    }

    /// <summary>
    /// クリップボードのテキストを読む。他のプロセスが開いている間は失敗するので、
    /// 少し待って何度か試す。
    /// </summary>
    private static string? ReadText()
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                    return System.Windows.Clipboard.GetText();
                return null;
            }
            catch (COMException)
            {
                Thread.Sleep(30);
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
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
