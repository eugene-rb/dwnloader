namespace Dwnloader.Core;

/// <summary>
/// ダウンロード速度の計測。
///
/// 各ジョブが実際に受け取ったバイト数を1か所へ積み上げ、1秒ごとに差分を取って
/// その秒の速度とする。ジョブ側が報告する「瞬間の速度」を足し合わせる方法は、
/// 報告の間隔がジョブごとに違うため合計が実際とずれる。累計バイト数の差分なら、
/// 何本走っていても合計は必ず実測と一致する。
/// </summary>
public sealed class SpeedMeter
{
    /// <summary>保持するサンプル数。1秒刻みなので、そのまま秒数になる。</summary>
    public const int Capacity = 180;

    private long _totalBytes;

    // 直近の値を上書きしていく輪。件数が増えても配列は伸びない。
    private readonly double[] _samples = new double[Capacity];
    private int _count;
    private int _head;                  // 次に書く位置

    private long _lastTotal;
    private long _lastTicks = Environment.TickCount64;

    /// <summary>ジョブが受け取ったバイト数を足す。どのスレッドから呼んでもよい。</summary>
    public void Add(long bytes)
    {
        if (bytes > 0) Interlocked.Add(ref _totalBytes, bytes);
    }

    /// <summary>起動してから受け取った合計。</summary>
    public long TotalBytes => Interlocked.Read(ref _totalBytes);

    /// <summary>直近の1秒の速度（バイト毎秒）。</summary>
    public double Current { get; private set; }

    /// <summary>保持している範囲での最大値。目盛りの決定に使う。</summary>
    public double Peak { get; private set; }

    /// <summary>
    /// 1秒ごとに呼ぶ。前回からの差分を、実際に経過した時間で割る。
    /// タイマーは要求どおりの間隔で来るとは限らないので、経過時間は毎回測る。
    /// </summary>
    public void Sample()
    {
        long now = Environment.TickCount64;
        long total = TotalBytes;

        double elapsed = (now - _lastTicks) / 1000.0;
        double speed = elapsed > 0.05 ? (total - _lastTotal) / elapsed : 0;
        if (speed < 0) speed = 0;              // 念のため（累計は減らない）

        _lastTicks = now;
        _lastTotal = total;

        _samples[_head] = speed;
        _head = (_head + 1) % Capacity;
        if (_count < Capacity) _count++;

        Current = speed;

        double peak = 0;
        for (int i = 0; i < _count; i++)
        {
            var v = _samples[i];
            if (v > peak) peak = v;
        }
        Peak = peak;
    }

    /// <summary>
    /// 古い順に並べ直して返す。描画側が輪の内部構造を知らずに済む。
    /// </summary>
    public void CopyTo(double[] destination, out int length)
    {
        length = _count;
        if (_count == 0) return;

        int start = (_head - _count + Capacity) % Capacity;
        for (int i = 0; i < _count; i++)
            destination[i] = _samples[(start + i) % Capacity];
    }

    public void Reset()
    {
        Interlocked.Exchange(ref _totalBytes, 0);
        Array.Clear(_samples);
        _count = 0;
        _head = 0;
        _lastTotal = 0;
        _lastTicks = Environment.TickCount64;
        Current = 0;
        Peak = 0;
    }
}
