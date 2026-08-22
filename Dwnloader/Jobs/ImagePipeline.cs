using ImageMagick;

namespace Dwnloader.Jobs;

/// <summary>
/// 画像の復号と JPEG への正規化。
///
/// hitomi は既定で AVIF、ページによっては WebP を配る。PDF へ入れる前に
/// 一度 JPEG へ揃える（PDF に AVIF/WebP は入れられない）。
/// </summary>
public static class ImagePipeline
{
    private static int _configured;

    /// <summary>
    /// 巨大な画像で落ちないよう資源の上限を決める。同人誌の見開きは
    /// 数千万画素になることがあり、既定のままだと途中で例外になる。
    /// 逆に無制限にすると1ページで実メモリを食い潰してプロセスごと落ちるので、
    /// ディスクへ逃がす設定にして「遅くなるが死なない」側へ倒す。
    /// </summary>
    public static void EnsureConfigured()
    {
        if (Interlocked.Exchange(ref _configured, 1) != 0) return;

        ResourceLimits.Width = 100_000;
        ResourceLimits.Height = 100_000;
        // 物理メモリの4分の1を上限にし、超えた分は一時ファイルへ退避させる
        ResourceLimits.Memory = (ulong)Math.Max(256L * 1024 * 1024,
                                                GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 4);
        ResourceLimits.LimitMemory(new Percentage(25));
    }

    /// <summary>任意形式の画像を RGB JPEG にして dest へ書き出す。</summary>
    public static void NormalizeToJpeg(byte[] data, string dest, int quality = 90)
    {
        EnsureConfigured();

        using var image = new MagickImage(data);

        // 透過を白で埋める。PDF/JPEG は透過を持てないので、残すと黒く潰れる。
        if (image.HasAlpha)
        {
            image.BackgroundColor = MagickColors.White;
            image.Alpha(AlphaOption.Remove);
        }

        image.ColorSpace = ColorSpace.sRGB;
        image.Format = MagickFormat.Jpeg;
        image.Quality = (uint)Math.Clamp(quality, 40, 100);
        // 高画質指定のときだけ色差の間引きをやめる（Python 版と同じ判断）
        if (quality >= 95) image.Settings.SetDefine(MagickFormat.Jpeg, "sampling-factor", "1x1");

        image.Strip();                  // 元画像の EXIF は持ち込まない
        image.Write(dest);
    }

    /// <summary>表紙用の小さな JPEG を作る。1ページ目から作るので追加の通信は要らない。</summary>
    public static byte[] MakeThumbnail(string jpegPath, uint width = 220)
    {
        EnsureConfigured();

        using var image = new MagickImage(jpegPath);
        if (image.Width > width)
        {
            uint height = Math.Max(1u, (uint)Math.Round(image.Height * (double)width / image.Width));
            image.Resize(width, height);
        }
        image.Format = MagickFormat.Jpeg;
        image.Quality = 82;
        image.Strip();
        return image.ToByteArray();
    }
}
