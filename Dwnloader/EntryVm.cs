using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Dwnloader.Core;

namespace Dwnloader;

/// <summary>
/// 一覧に出す1件分。UI に直接束ねるので、変更通知は必ず UI スレッドから出す。
/// </summary>
public sealed class EntryVm : INotifyPropertyChanged
{
    public EntryVm(string jobId, SourceRef reference)
    {
        JobId = jobId;
        _reference = reference;
        _title = reference.Url;
        var head = MediaKind.IsMedia(reference.Kind) ? MediaKind.Label(reference.Kind) + " · " : "";
        _subtitle = head + "情報を取得しています…";
    }

    public string JobId { get; }

    private SourceRef _reference;
    public SourceRef Reference
    {
        get => _reference;
        set
        {
            _reference = value;
            Raise(nameof(Reference));
            Raise(nameof(SiteLabel));
            Raise(nameof(Url));
            Raise(nameof(CanSwitchKind));
            Raise(nameof(KindSwapLabel));
        }
    }

    public string Url => _reference.Url;
    public string SiteLabel => Sites.SiteRegistry.LabelFor(_reference.Site);

    private JobStatus _status = JobStatus.Queued;
    public JobStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise(nameof(Status));
            Raise(nameof(StatusLabel));
            Raise(nameof(IsActive));
            Raise(nameof(StatusBrush));
            Raise(nameof(CanSwitchKind));
            Raise(nameof(CanOpen));
            Raise(nameof(CanRetry));
            Raise(nameof(CanCancel));
            Raise(nameof(ShowProgress));
            Raise(nameof(ShowMessage));
        }
    }

    public string StatusLabel => StatusText.Label(_status);
    public bool IsActive => StatusText.IsActive(_status);

    /// <summary>
    /// 状態を色で見分けられるようにする。文字だけだと一覧で追いにくい。
    ///
    /// 固定色ではなく、Windows 標準の状態色（設定アプリ等が使っているのと同じ
    /// テーマリソース）を引く。ライト/ダークどちらでも見た目が浮かない。
    /// </summary>
    public Brush? StatusBrush => _status switch
    {
        JobStatus.Done => ThemeBrush("SystemFillColorSuccessBrush"),
        JobStatus.Skipped => ThemeBrush("TextFillColorSecondaryBrush"),
        JobStatus.Partial => ThemeBrush("SystemFillColorCautionBrush"),
        JobStatus.Failed => ThemeBrush("SystemFillColorCriticalBrush"),
        JobStatus.Canceled => ThemeBrush("TextFillColorTertiaryBrush"),
        _ => ThemeBrush("AccentTextFillColorPrimaryBrush"),
    };

    /// <summary>
    /// アプリのテーマ辞書からブラシを引く。<c>Application.Resources</c>の
    /// 素朴なインデクサ参照は <c>ThemeDictionaries</c> 内を見てくれないため、
    /// 現在の実効テーマ（<see cref="Application.RequestedTheme"/>）に応じて
    /// Light/Dark いずれかの辞書を自分で辿る。
    /// </summary>
    private static Brush? ThemeBrush(string key)
    {
        var app = Application.Current;
        if (app is null) return null;
        var themeKey = app.RequestedTheme == ApplicationTheme.Dark ? "Dark" : "Light";

        if (TryFind(app.Resources, themeKey, key, out var found)) return found;
        return app.Resources.TryGetValue(key, out var plain) ? plain as Brush : null;
    }

    private static bool TryFind(ResourceDictionary dict, string themeKey, string key, out Brush? brush)
    {
        if (dict.ThemeDictionaries.TryGetValue(themeKey, out var themedObj)
            && themedObj is ResourceDictionary themed)
        {
            if (themed.TryGetValue(key, out var value) && value is Brush b)
            {
                brush = b;
                return true;
            }
        }

        foreach (var merged in dict.MergedDictionaries)
        {
            if (TryFind(merged, themeKey, key, out brush)) return true;
        }

        brush = null;
        return false;
    }

    private string _title;
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Raise(); } }
    }

    private string _subtitle;
    public string Subtitle
    {
        get => _subtitle;
        set { if (_subtitle != value) { _subtitle = value; Raise(); } }
    }

    private string _detail = "待機中";
    public string Detail
    {
        get => _detail;
        set { if (_detail != value) { _detail = value; Raise(); } }
    }

    private string _message = "";
    public string Message
    {
        get => _message;
        set
        {
            if (_message == value) return;
            _message = value;
            Raise();
            Raise(nameof(HasMessage));
            Raise(nameof(ShowMessage));
        }
    }

    public bool HasMessage => _message.Length > 0;

    /// <summary>
    /// メッセージ行を表示するか。WinUI3にはWPFのMultiDataTriggerに相当する
    /// ものが無いため、進捗バーとの排他条件をここで計算プロパティにしてある。
    /// </summary>
    public bool ShowMessage => HasMessage && !ShowProgress;

    private string _outPath = "";
    public string OutPath
    {
        get => _outPath;
        set { if (_outPath != value) { _outPath = value; Raise(); } }
    }

    private long _done;
    public long Done
    {
        get => _done;
        set { if (_done != value) { _done = value; Raise(); Raise(nameof(Percent)); } }
    }

    private long _total;
    public long Total
    {
        get => _total;
        set
        {
            if (_total == value) return;
            _total = value;
            Raise();
            Raise(nameof(Percent));
            Raise(nameof(IsIndeterminate));
        }
    }

    public double Percent => _total > 0 ? Math.Clamp(_done * 100.0 / _total, 0, 100) : 0;
    public bool IsIndeterminate => IsActive && _total <= 0;
    public bool ShowProgress => IsActive;

    private BitmapSource? _thumb;
    public BitmapSource? Thumb
    {
        get => _thumb;
        set { _thumb = value; Raise(); }
    }

    /// <summary>すでに走り出したか。中止後の再開判定に使う。</summary>
    public bool Started { get; set; }

    /// <summary>自動再試行を何回使ったか。手動の再試行では 0 に戻す。</summary>
    public int AutoRetryCount { get; set; }

    public IReadOnlyList<string> Tags { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 動画↔音声を切り替えられるか。
    /// 走り出したものと保存済みは触らない。前者は落としている最中に行き先が
    /// 変わることになり、後者は既にあるファイルと表示が食い違う。
    /// </summary>
    public bool CanSwitchKind =>
        MediaKind.IsMedia(_reference.Kind) && !Started
        && _status is JobStatus.Queued or JobStatus.Failed or JobStatus.Canceled;

    public string KindSwapLabel =>
        _reference.Kind == MediaKind.Audio ? "動画にする" : "音声にする";

    public bool CanOpen => _status is JobStatus.Done or JobStatus.Skipped or JobStatus.Partial;
    public bool CanRetry => StatusText.IsUnfinished(_status);
    public bool CanCancel => IsActive;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// 大量追加のための一覧。1件ずつ通知すると件数分のレイアウトが走るので、
/// 一括で足すときは通知を1回にまとめる（300件の追加で描画が固まるのを防ぐ）。
/// </summary>
public sealed class BulkObservableCollection<T> : ObservableCollection<T>
{
    private bool _suppress;

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs e)
    {
        if (!_suppress) base.OnCollectionChanged(e);
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        if (!_suppress) base.OnPropertyChanged(e);
    }

    public void AddRange(IEnumerable<T> items)
    {
        var list = items as IList<T> ?? items.ToList();
        if (list.Count == 0) return;

        if (list.Count == 1)
        {
            Add(list[0]);
            return;
        }

        _suppress = true;
        try
        {
            foreach (var item in list) Add(item);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    public void RemoveRange(IEnumerable<T> items)
    {
        var set = items as ICollection<T> ?? items.ToList();
        if (set.Count == 0) return;

        if (set.Count == 1)
        {
            Remove(set.First());
            return;
        }

        _suppress = true;
        try
        {
            foreach (var item in set) Remove(item);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    public void ResetTo(IEnumerable<T> items)
    {
        _suppress = true;
        try
        {
            Clear();
            foreach (var item in items) Add(item);
        }
        finally
        {
            _suppress = false;
        }
        RaiseReset();
    }

    private void RaiseReset()
    {
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        base.OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        base.OnCollectionChanged(
            new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
