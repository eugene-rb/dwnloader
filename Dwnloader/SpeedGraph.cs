using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Dwnloader.Core;

namespace Dwnloader;

/// <summary>
/// 速度の推移を描く。
///
/// 点ごとに要素を作る作りにすると、1秒ごとの更新で毎回180個の要素を作り直す
/// ことになる。OnRender で直接描けば、確保するのは figure 1本だけで済む。
/// </summary>
public sealed class SpeedGraph : FrameworkElement
{
    private readonly double[] _buffer = new double[SpeedMeter.Capacity];
    private int _length;
    private double _peak;
    private double _current;

    private static readonly Brush Fill = MakeFill();
    private static readonly Pen Line = MakePen("#FF6D8CFF", 1.6);
    private static readonly Pen Grid = MakePen("#FF272C39", 1.0);
    private static readonly Brush Label = new SolidColorBrush(
        (Color)ColorConverter.ConvertFromString("#FF9AA1B4")!);
    private static readonly Brush Background = new SolidColorBrush(
        (Color)ColorConverter.ConvertFromString("#FF0D0F14")!);

    private static readonly Typeface Face = new("Consolas");

    static SpeedGraph()
    {
        Fill.Freeze();
        Line.Freeze();
        Grid.Freeze();
        Label.Freeze();
        Background.Freeze();
    }

    private static Brush MakeFill()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
        };
        brush.GradientStops.Add(new GradientStop(
            (Color)ColorConverter.ConvertFromString("#806D8CFF")!, 0));
        brush.GradientStops.Add(new GradientStop(
            (Color)ColorConverter.ConvertFromString("#106D8CFF")!, 1));
        return brush;
    }

    private static Pen MakePen(string color, double thickness)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)!);
        brush.Freeze();
        return new Pen(brush, thickness);
    }

    /// <summary>新しいサンプルを取り込んで描き直す。UI スレッドから呼ぶこと。</summary>
    public void Update(SpeedMeter meter)
    {
        meter.CopyTo(_buffer, out _length);
        _peak = meter.Peak;
        _current = meter.Current;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 1 || h <= 1) return;

        dc.DrawRectangle(Background, null, new Rect(0, 0, w, h));

        // 目盛りは「切りのいい値」まで引き上げる。実測の最大値そのままだと、
        // 波形が常に天井に張り付いて増減が読めない。
        double scale = NiceCeiling(Math.Max(_peak, 64 * 1024));

        for (int i = 1; i < 4; i++)
        {
            double y = h * i / 4.0;
            dc.DrawLine(Grid, new Point(0, y), new Point(w, y));
        }

        if (_length >= 2)
        {
            // 横軸は常に Capacity 秒分。データが少ないうちは右詰めにして、
            // 新しい値が右から入ってくる形にする。
            double step = w / (SpeedMeter.Capacity - 1);
            double x0 = w - (_length - 1) * step;

            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x0, h), isFilled: true, isClosed: true);
                for (int i = 0; i < _length; i++)
                {
                    double x = x0 + i * step;
                    double y = h - Math.Min(1.0, _buffer[i] / scale) * (h - 4) - 2;
                    ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
                }
                ctx.LineTo(new Point(x0 + (_length - 1) * step, h), false, false);
            }
            geometry.Freeze();

            dc.DrawGeometry(Fill, null, geometry);

            // 折れ線は塗りとは別に引く。閉じた辺（底面）に線が乗らないようにする。
            var stroke = new StreamGeometry();
            using (var ctx = stroke.Open())
            {
                ctx.BeginFigure(
                    new Point(x0, h - Math.Min(1.0, _buffer[0] / scale) * (h - 4) - 2),
                    isFilled: false, isClosed: false);
                for (int i = 1; i < _length; i++)
                {
                    double x = x0 + i * step;
                    double y = h - Math.Min(1.0, _buffer[i] / scale) * (h - 4) - 2;
                    ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: true);
                }
            }
            stroke.Freeze();
            dc.DrawGeometry(null, Line, stroke);
        }

        DrawText(dc, $"{Util.HumanSize(scale)}/s", 4, 2);
        DrawText(dc, _length == 0 ? "計測中…" : $"現在 {Util.HumanSize(_current)}/s",
                 4, h - 16);
    }

    private void DrawText(DrawingContext dc, string text, double x, double y)
    {
        var ft = new FormattedText(text, CultureInfo.CurrentCulture,
                                   FlowDirection.LeftToRight, Face, 11, Label,
                                   VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(x, y));
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
