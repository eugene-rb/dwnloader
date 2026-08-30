using System.Text.Json.Serialization;

namespace Dwnloader.Core;

public sealed class HistoryData
{
    [JsonPropertyName("done")] public Dictionary<string, string> Done { get; set; } = new();
}

/// <summary>ダウンロード済みキーの集合。再起動しても重複取得しないため。</summary>
public sealed class History : IDisposable
{
    private readonly object _gate = new();
    private readonly DebouncedWriter<HistoryData> _writer;
    private readonly Dictionary<string, string> _done;

    public History()
    {
        _done = Json.Read<HistoryData>(AppPaths.HistoryPath)?.Done ?? new Dictionary<string, string>();
        _writer = new DebouncedWriter<HistoryData>(AppPaths.HistoryPath);
    }

    public bool Contains(string key)
    {
        lock (_gate) return _done.ContainsKey(key);
    }

    public string? PathFor(string key)
    {
        lock (_gate) return _done.TryGetValue(key, out var p) ? p : null;
    }

    public void Add(string key, string path)
    {
        lock (_gate)
        {
            _done[key] = path;
            _writer.Schedule(new HistoryData { Done = new Dictionary<string, string>(_done) });
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _done.Clear();
            _writer.Schedule(new HistoryData());
        }
    }

    public int Count
    {
        get { lock (_gate) return _done.Count; }
    }

    public void Flush() => _writer.Flush();
    public void Dispose() => _writer.Dispose();
}

/// <summary>キューに残っている1件分の記録。</summary>
public sealed class QueueRecord
{
    [JsonPropertyName("site")] public string Site { get; set; } = "";
    [JsonPropertyName("gid")] public string Gid { get; set; } = "";
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    /// <summary>表示文言は変わりうるので列挙子の名前で保存する。</summary>
    [JsonPropertyName("status")] public string Status { get; set; } = nameof(JobStatus.Queued);
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("subtitle")] public string Subtitle { get; set; } = "";
    [JsonPropertyName("done")] public long Done { get; set; }
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("out_path")] public string OutPath { get; set; } = "";
    [JsonPropertyName("message")] public string Message { get; set; } = "";
}

public sealed class QueueData
{
    [JsonPropertyName("items")] public List<QueueRecord> Items { get; set; } = new();
}

/// <summary>
/// 未完了のキューを保存し、次回起動時に復元できるようにする。
/// 完了したものは履歴側が持つのでここには入れない。
/// </summary>
public sealed class QueueStore : IDisposable
{
    private readonly object _gate = new();
    private readonly DebouncedWriter<QueueData> _writer;
    private List<QueueRecord> _records;

    public QueueStore()
    {
        var loaded = Json.Read<QueueData>(AppPaths.QueuePath)?.Items ?? new List<QueueRecord>();
        // 最低限これが無いと作品を特定できない。このファイルはセッションを
        // またいで残るので、壊れた1件で起動できなくなると手で消すまで復旧できない。
        _records = loaded
            .Where(r => r is not null
                        && !string.IsNullOrEmpty(r.Site)
                        && !string.IsNullOrEmpty(r.Url))
            .ToList();
        _writer = new DebouncedWriter<QueueData>(AppPaths.QueuePath);
    }

    public List<QueueRecord> Records()
    {
        lock (_gate) return new List<QueueRecord>(_records);
    }

    public void Replace(List<QueueRecord> records)
    {
        lock (_gate)
        {
            if (SameAs(records)) return;        // 変化がなければ書かない
            _records = records;
            _writer.Schedule(new QueueData { Items = records });
        }
    }

    private bool SameAs(List<QueueRecord> other)
    {
        if (_records.Count != other.Count) return false;
        for (int i = 0; i < other.Count; i++)
        {
            var a = _records[i];
            var b = other[i];
            if (a.Site != b.Site || a.Gid != b.Gid || a.Url != b.Url || a.Kind != b.Kind
                || a.Status != b.Status || a.Title != b.Title || a.Subtitle != b.Subtitle
                || a.Done != b.Done || a.Total != b.Total || a.OutPath != b.OutPath
                || a.Message != b.Message)
                return false;
        }
        return true;
    }

    public void Flush() => _writer.Flush();
    public void Dispose() => _writer.Dispose();
}
