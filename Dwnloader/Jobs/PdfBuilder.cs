using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace Dwnloader.Jobs;

public sealed class PdfException : Exception
{
    public PdfException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>PDF の文書情報。ビューアやエクスプローラーの検索に効く。</summary>
public sealed class PdfMeta
{
    public string Title { get; init; } = "";
    public string Author { get; init; } = "";
    public string Subject { get; init; } = "";
    public IReadOnlyList<string> Keywords { get; init; } = Array.Empty<string>();
    public string Creator { get; init; } = "";
    public DateTime? Created { get; init; }
}

/// <summary>JPEG の並びを1つの PDF にまとめる。</summary>
public static class PdfBuilder
{
    /// <summary>画像の実寸をそのままページの大きさにするための解像度。</summary>
    private const double DefaultDpi = 96.0;

    /// <summary>
    /// JPEG をそのまま埋め込んで PDF にする。戻り値は PDF に入れられなかった
    /// 枚数（呼び出し側はこれを欠落数に加算すること。加算し忘れると、
    /// 実際には欠けたPDFが「完了」として履歴に記録されてしまう）。
    ///
    /// PdfSharp は JPEG を再エンコードせず /DCTDecode として格納するので、
    /// 画質が落ちず速い（Python 版の img2pdf と同じ性質）。
    /// 一時ファイルに書いてから置き換えるため、途中で失敗しても既存の
    /// PDF は壊れない。
    /// </summary>
    public static int Build(IReadOnlyList<string> jpegPaths, string outPath,
                            PdfMeta? meta = null, Action<string>? warn = null)
    {
        if (jpegPaths.Count == 0)
            throw new PdfException("PDF に入れる画像がありません");

        Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
        var tmp = outPath + ".part";

        int skipped = 0;
        try
        {
            using (var doc = new PdfDocument())
            {
                ApplyMeta(doc, meta);

                foreach (var path in jpegPaths)
                {
                    try
                    {
                        AddPage(doc, path);
                    }
                    catch (Exception e)
                    {
                        // 1ページ壊れていても残りは束ねる。全部捨てる方が損失が大きい。
                        skipped++;
                        warn?.Invoke($"{Path.GetFileName(path)} をPDFに入れられませんでした: {e.Message}");
                    }
                }

                if (doc.PageCount == 0)
                    throw new PdfException("PDF に入れられる画像が1枚もありませんでした");
                if (skipped > 0)
                    warn?.Invoke($"{skipped} ページをPDFに入れられませんでした");

                doc.Save(tmp);
            }

            File.Move(tmp, outPath, overwrite: true);
            return skipped;
        }
        catch (Exception e)
        {
            TryDelete(tmp);
            if (e is PdfException) throw;
            throw new PdfException($"PDF の生成に失敗しました: {e.Message}", e);
        }
    }

    private static void AddPage(PdfDocument doc, string jpegPath)
    {
        using var image = XImage.FromFile(jpegPath);

        // 画像の画素数をそのままページ寸法（ポイント）へ写す
        double widthPt = image.PixelWidth * 72.0 / DefaultDpi;
        double heightPt = image.PixelHeight * 72.0 / DefaultDpi;

        var page = doc.AddPage();
        page.Width = XUnit.FromPoint(widthPt);
        page.Height = XUnit.FromPoint(heightPt);

        using var gfx = XGraphics.FromPdfPage(page);
        gfx.DrawImage(image, 0, 0, widthPt, heightPt);
    }

    private static void ApplyMeta(PdfDocument doc, PdfMeta? meta)
    {
        if (meta is null) return;

        if (meta.Title.Length > 0) doc.Info.Title = meta.Title;
        if (meta.Author.Length > 0) doc.Info.Author = meta.Author;
        if (meta.Subject.Length > 0) doc.Info.Subject = meta.Subject;
        if (meta.Keywords.Count > 0) doc.Info.Keywords = string.Join(", ", meta.Keywords);
        // Producer は PdfSharp が自分で書くので触らない（読み取り専用）
        if (meta.Creator.Length > 0) doc.Info.Creator = meta.Creator;
        if (meta.Created is { } created)
        {
            // PDF の日時は種別を持てないので、素朴な日時として書く
            doc.Info.CreationDate = created.Kind == DateTimeKind.Utc
                ? created.ToLocalTime()
                : created;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
