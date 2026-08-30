using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Dwnloader.Core;

/// <summary>ファイル名生成・日付解釈まわり。Python 版 util.py の移植。</summary>
public static partial class Util
{
    // 時差の書き方はサイトごとに揺れる（"-05" / "+09:00" / "Z" / 無し）。
    // 落とすと PDF の作成日時が最大14時間ずれるので、あるものは必ず拾う。
    [GeneratedRegex(@"(\d{4})-(\d{2})-(\d{2})[T ](\d{2}):(\d{2})(?::(\d{2}))?\s*(?:(Z)|([+-])(\d{2}):?(\d{2})?)?")]
    private static partial Regex DateTimeHead();

    [GeneratedRegex(@"(\d{4})-(\d{2})-(\d{2})")]
    private static partial Regex DateOnly();

    /// <summary>
    /// サイトが返す日付文字列を DateTime にする。解釈できなければ null。
    /// 時差が書かれていれば、それを適用した現地時間として返す。
    /// </summary>
    public static DateTime? ParseDateTime(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var m = DateTimeHead().Match(value);
        if (m.Success)
        {
            try
            {
                int year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                int month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                int day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
                int hour = int.Parse(m.Groups[4].Value, CultureInfo.InvariantCulture);
                int minute = int.Parse(m.Groups[5].Value, CultureInfo.InvariantCulture);
                int second = m.Groups[6].Success
                    ? int.Parse(m.Groups[6].Value, CultureInfo.InvariantCulture) : 0;

                var naive = new DateTime(year, month, day, hour, minute, second,
                                         DateTimeKind.Unspecified);

                if (m.Groups[7].Success)                    // "Z"
                    return DateTime.SpecifyKind(naive, DateTimeKind.Utc);

                if (!m.Groups[8].Success)                   // 時差の記載なし
                    return naive;

                int offHours = int.Parse(m.Groups[9].Value, CultureInfo.InvariantCulture);
                int offMinutes = m.Groups[10].Success
                    ? int.Parse(m.Groups[10].Value, CultureInfo.InvariantCulture) : 0;
                var offset = new TimeSpan(offHours, offMinutes, 0);
                if (m.Groups[8].Value == "-") offset = -offset;
                return new DateTimeOffset(naive, offset).DateTime;
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;                                // 13月など、あり得ない値
            }
        }

        var d2 = DateOnly().Match(value);
        if (d2.Success)
        {
            try
            {
                return new DateTime(
                    int.Parse(d2.Groups[1].Value, CultureInfo.InvariantCulture),
                    int.Parse(d2.Groups[2].Value, CultureInfo.InvariantCulture),
                    int.Parse(d2.Groups[3].Value, CultureInfo.InvariantCulture));
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        return null;
    }

    // Windows で使えない文字。全角に置換して情報を落とさないようにする。
    private static readonly Dictionary<char, char> Translate = new()
    {
        ['<'] = '＜', ['>'] = '＞', [':'] = '：', ['"'] = '”',
        ['/'] = '／', ['\\'] = '＼', ['|'] = '｜', ['?'] = '？', ['*'] = '＊',
    };

    // CON, PRN などの予約デバイス名（拡張子を付けても使えない）
    private static readonly HashSet<string> Reserved = BuildReserved();

    private static HashSet<string> BuildReserved()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "CON", "PRN", "AUX", "NUL" };
        for (int i = 1; i <= 9; i++) { set.Add($"COM{i}"); set.Add($"LPT{i}"); }
        return set;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    /// <summary>Windows のファイル名として安全な文字列にする。</summary>
    public static string SanitizeFilename(string? name, int maxLen = 120,
                                          string fallback = "untitled")
    {
        var normalized = (name ?? "").Normalize(NormalizationForm.FormC);

        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (ch <= 0x1f || ch == 0x7f) continue;         // 制御文字は落とす
            sb.Append(Translate.TryGetValue(ch, out var rep) ? rep : ch);
        }

        var result = Whitespace().Replace(sb.ToString(), " ").Trim();
        // 末尾のドットと空白は Windows が黙って削るので先に落とす
        result = result.TrimEnd(' ', '.');

        if (result.Length > maxLen)
            result = result[..maxLen].TrimEnd(' ', '.');

        if (result.Length == 0 || Reserved.Contains(result))
            return fallback;
        return result;
    }

    /// <summary>同名ファイルがあれば " (2)", " (3)" … を付けて衝突を避ける。</summary>
    public static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;

        var dir = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);

        for (int i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(dir, $"{stem} ({Environment.ProcessId}){ext}");
    }

    [GeneratedRegex(@"[\[\(（【]\s*[\]\)）】]")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex DoubleSpace();

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex TemplateToken();

    /// <summary>
    /// テンプレートからPDFのファイル名（拡張子なし）を組み立てる。
    /// 未知のトークンは空文字にして、テンプレートの記述ミスで名前ごと失わないようにする。
    /// </summary>
    public static string FormatFilename(string template, GalleryMeta meta, int maxLen = 120)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = meta.Title,
            ["id"] = meta.Gid,
            ["site"] = meta.Site,
            ["artist"] = Join(meta.Artists) is { Length: > 0 } a ? a : "unknown",
            ["group"] = Join(meta.Groups),
            ["series"] = Join(meta.Series),
            ["pages"] = meta.Images.Count.ToString(CultureInfo.InvariantCulture),
            ["lang"] = meta.Language,
        };

        string name;
        if (string.IsNullOrWhiteSpace(template))
        {
            name = $"{meta.Title} ({meta.Site}-{meta.Gid})";
        }
        else
        {
            name = TemplateToken().Replace(template, m =>
                values.TryGetValue(m.Groups[1].Value, out var v) ? v : "");
        }

        // 空トークンが残した空の括弧と余分な区切りを掃除する
        name = EmptyBrackets().Replace(name, "");
        name = DoubleSpace().Replace(name, " ").Trim(' ', '-', '_');

        return SanitizeFilename(name, maxLen, $"{meta.Site}-{meta.Gid}");
    }

    private static string Join(IEnumerable<string> values) => string.Join("、", values);

    /// <summary>バイト数を読みやすくする。</summary>
    public static string HumanSize(double num)
    {
        string[] units = { "B", "KB", "MB", "GB" };
        foreach (var unit in units)
        {
            if (Math.Abs(num) < 1024.0)
            {
                return unit == "B"
                    ? $"{num:F0} {unit}"
                    : $"{num:F1} {unit}";
            }
            num /= 1024.0;
        }
        return $"{num:F1} TB";
    }
}
