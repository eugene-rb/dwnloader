using System.Drawing;
using System.Windows.Forms;
using Dwnloader.Core;

namespace Dwnloader;

/// <summary>
/// タスクトレイ常駐。ウィンドウを閉じても監視を続けられるようにする。
/// アイコンは外部ファイルを持たず、その場で描く。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _watchItem;
    private readonly Action _onShow;
    private readonly Action _onToggleWatch;
    private readonly Action _onQuit;

    private bool _watching = true;
    private string _status = "";
    private readonly Icon _generated;

    public TrayIcon(Action onShow, Action onToggleWatch, Action onQuit)
    {
        _onShow = onShow;
        _onToggleWatch = onToggleWatch;
        _onQuit = onQuit;

        _generated = MakeIcon();

        _watchItem = new ToolStripMenuItem("クリップボード監視を停止", null, (_, _) => _onToggleWatch());

        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem("ウィンドウを表示", null, (_, _) => _onShow()));
        menu.Items.Add(_watchItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("終了", null, (_, _) => _onQuit()));

        _icon = new NotifyIcon
        {
            Icon = _generated,
            Text = AppInfo.Title,
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => _onShow();
    }

    /// <summary>カーソルを乗せたときに状況が分かるようにする。</summary>
    public void SetStatus(bool watching, string status)
    {
        _watching = watching;
        _watchItem.Text = watching ? "クリップボード監視を停止" : "クリップボード監視を再開";

        if (status == _status) return;
        _status = status;
        // NotifyIcon.Text は 63 文字までしか受け付けない
        var text = $"{AppInfo.Title}\n{status}";
        _icon.Text = text.Length > 62 ? text[..62] : text;
    }

    public void Notify(string title, string message)
    {
        try
        {
            _icon.BalloonTipTitle = title;
            _icon.BalloonTipText = message;
            _icon.ShowBalloonTip(4000);
        }
        catch (Exception)
        {
            // 通知に対応しない環境でも常駐は続ける
        }
    }

    /// <summary>角丸の四角に "PDF" を描いたアイコンを作る。</summary>
    private static Icon MakeIcon(int size = 32)
    {
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            int pad = Math.Max(1, size / 16);
            var rect = new Rectangle(pad, pad, size - pad * 2, size - pad * 2);
            using var brush = new SolidBrush(Color.FromArgb(255, 109, 140, 255));
            using var path = RoundedRect(rect, size / 5);
            g.FillPath(brush, path);

            using var font = new Font("Segoe UI", size / 4.2f, FontStyle.Bold,
                                      GraphicsUnit.Pixel);
            using var text = new SolidBrush(Color.FromArgb(255, 11, 14, 21));
            var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("PDF", font, text, rect, format);
        }

        // Icon.FromHandle が返すものは自前で破棄できないので、複製して持つ
        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _generated.Dispose();
    }
}
