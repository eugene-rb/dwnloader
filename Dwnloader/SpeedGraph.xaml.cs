using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Dwnloader.Core;

namespace Dwnloader;

/// <summary>
/// 速度の推移を描く。
///
/// WPF版は FrameworkElement.OnRender で直接描画していたが、WinUI3には
/// 相当するAPIが無いため、Path 2本（塗り・線）を毎回作り直す方式にした。
/// 1秒ごとの更新でも Path 要素は2個+グリッド線3本+ラベル2個のままなので、
/// 「点ごとに要素を作らない」という元の設計意図は保たれている。
/// </summary>
public sealed partial class SpeedGraph : UserControl
{
    private static readonly Brush Fill = MakeFill();

    private readonly double[] _buffer = new double[SpeedMeter.Capacity];
    private int _length;
    private double _peak;
    private double _current;

    public SpeedGraph()
    {
        InitializeComponent();
        FillPath.Fill = Fill;
    }

    /// <summary>
    /// 塗りのグラデーションは固定色ではなく、ユーザーが Windows の設定で
    /// 選んでいるアクセントカラーを起点にする（線の Stroke は XAML 側で
    /// AccentFillColorDefaultBrush を直接参照しているのでこれと揃う）。
    /// </summary>
    private static Brush MakeFill()
    {
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var value)
            && value is Color c ? c : Color.FromArgb(0xFF, 0x6D, 0x8C, 0xFF);

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x80, accent.R, accent.G, accent.B), Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x10, accent.R, accent.G, accent.B), Offset = 1 });
        return brush;
    }

    /// <summary>新しいサンプルを取り込んで描き直す。UI スレッドから呼ぶこと。</summary>
    public void Update(SpeedMeter meter)
    {
        meter.CopyTo(_buffer, out _length);
        _peak = meter.Peak;
        _current = meter.Current;
        Redraw();
    }

    private void OnSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs e) => Redraw();

    private void Redraw()
    {
        double w = RootGrid.ActualWidth, h = RootGrid.ActualHeight;
        if (w <= 1 || h <= 1) return;

        // 目盛りは「切りのいい値」まで引き上げる。実測の最大値そのままだと、
        // 波形が常に天井に張り付いて増減が読めない。
        double scale = NiceCeiling(Math.Max(_peak, 64 * 1024));

        Grid1.X1 = 0; Grid1.X2 = w; Grid1.Y1 = Grid1.Y2 = h * 1 / 4.0;
        Grid2.X1 = 0; Grid2.X2 = w; Grid2.Y1 = Grid2.Y2 = h * 2 / 4.0;
        Grid3.X1 = 0; Grid3.X2 = w; Grid3.Y1 = Grid3.Y2 = h * 3 / 4.0;

        if (_length >= 2)
        {
            // 横軸は常に Capacity 秒分。データが少ないうちは右詰めにして、
            // 新しい値が右から入ってくる形にする。
            double step = w / (SpeedMeter.Capacity - 1);
            double x0 = w - (_length - 1) * step;

            var fillFigure = new PathFigure { StartPoint = new Point(x0, h), IsClosed = true, IsFilled = true };
            var fillPoly = new PolyLineSegment();
            var lineFigure = new PathFigure
            {
                StartPoint = new Point(x0, h - Math.Min(1.0, _buffer[0] / scale) * (h - 4) - 2),
                IsClosed = false,
                IsFilled = false,
            };
            var linePoly = new PolyLineSegment();

            for (int i = 0; i < _length; i++)
            {
                double x = x0 + i * step;
                double y = h - Math.Min(1.0, _buffer[i] / scale) * (h - 4) - 2;
                fillPoly.Points.Add(new Point(x, y));
                if (i > 0) linePoly.Points.Add(new Point(x, y));
            }
            fillPoly.Points.Add(new Point(x0 + (_length - 1) * step, h));

            fillFigure.Segments.Add(fillPoly);
            lineFigure.Segments.Add(linePoly);

            var fillGeometry = new PathGeometry();
            fillGeometry.Figures.Add(fillFigure);
            FillPath.Data = fillGeometry;

            var lineGeometry = new PathGeometry();
            lineGeometry.Figures.Add(lineFigure);
            LinePath.Data = lineGeometry;
        }
        else
        {
            FillPath.Data = null;
            LinePath.Data = null;
        }

        ScaleLabel.Text = $"{Util.HumanSize(scale)}/s";
        CurrentLabel.Text = _length == 0 ? "計測中…" : $"現在 {Util.HumanSize(_current)}/s";
        Canvas.SetTop(CurrentLabel, h - 16);
    }

    /// <summary>目盛りの上限を 1/2/5 × 10^n に丸める。</summary>
    private static double NiceCeiling(double value)
    {
        if (value <= 0) return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        double normalized = value / magnitude;
        double nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }
}
