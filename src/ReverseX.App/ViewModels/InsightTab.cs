using System.Data;
using System.Globalization;
using ReverseX.App.Services;
using ReverseX.Core;

namespace ReverseX.App.ViewModels;

/// <summary>
/// One tab of the Insight page: a searchable, sortable table (or a block of text such as the manifest XML).
/// Columns are named c0..cN in the DataTable so header text can contain any character; the view sets headers from Columns.
/// </summary>
public sealed class InsightTab : ObservableObject
{
    public const int MaxRows = 5000;
    public const string IndexColumn = "__i";

    private readonly IReadOnlyList<object?[]> _rows;
    private readonly Func<int, string>? _detail;
    private readonly Func<int, IProgress<string>, CancellationToken, Task<(string Text, string Note)>>? _code;
    private readonly string _codeHint = "";
    private CancellationTokenSource? _codeCts;
    private int _codeRun;
    private bool _detailBusy;
    private string _detailNote = "";
    private readonly Type[] _types;
    private string _search = "";
    private DataView? _view;
    private object? _selectedRow;
    private string _detailText = "";
    private string _note = "";
    private bool _isSelected;

    public InsightTab(string key, string[] columns, IReadOnlyList<object?[]> rows, Func<int, string>? detail = null)
    {
        Key = key;
        Columns = columns;
        _rows = rows;
        _detail = detail;
        _types = Enumerable.Range(0, columns.Length).Select(InferType).ToArray();
        Title = Loc.Get("Ins.Tab." + key) + "  " + rows.Count.ToString("N0", CultureInfo.CurrentCulture);
        Rebuild();
    }

    /// <summary>
    /// A table whose selected row opens code fetched in the background (e.g. an APK class as Java). <paramref name="code"/> turns its own
    /// failures into text; a newer selection cancels the older one, whose result is then ignored.
    /// </summary>
    public InsightTab(string key, string[] columns, IReadOnlyList<object?[]> rows,
        Func<int, IProgress<string>, CancellationToken, Task<(string Text, string Note)>> code, string hint)
        : this(key, columns, rows)
    {
        _code = code;
        _codeHint = hint;
        DetailNote = hint;
    }

    public InsightTab(string key, string text)
    {
        Key = key;
        Columns = Array.Empty<string>();
        _rows = Array.Empty<object?[]>();
        _types = Array.Empty<Type>();
        Text = text;
        Title = Loc.Get("Ins.Tab." + key);
    }

    public string Key { get; }
    public string Title { get; }
    public string[] Columns { get; }
    public int Count => _rows.Count;
    public string? Text { get; }
    public bool IsTable => Text is null;
    public bool IsText => Text is not null;
    public bool HasDetail => _detail is not null || _code is not null;
    /// <summary>The selected row opens code beside the list (with a note line and a busy bar) instead of in the small box below it.</summary>
    public bool IsCode => _code is not null;
    public bool DetailBusy { get => _detailBusy; private set => Set(ref _detailBusy, value); }
    public string DetailNote { get => _detailNote; private set => Set(ref _detailNote, value); }

    public bool IsSelected { get => _isSelected; set => Set(ref _isSelected, value); }
    public DataView? View { get => _view; private set => Set(ref _view, value); }
    public string Note { get => _note; private set => Set(ref _note, value); }
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }

    public string Search
    {
        get => _search;
        set { if (Set(ref _search, value)) Rebuild(); }
    }

    public object? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (!Set(ref _selectedRow, value)) return;
            if (_code is not null) { _ = LoadCodeAsync(value as DataRowView); return; }
            DetailText = value is DataRowView rv && _detail is not null ? _detail((int)rv[IndexColumn]) : "";
        }
    }

    private async Task LoadCodeAsync(DataRowView? row)
    {
        _codeCts?.Cancel();
        var id = ++_codeRun;
        if (row is null || _code is null)
        {
            DetailText = "";
            DetailNote = _codeHint;
            DetailBusy = false;
            return;
        }
        var cts = _codeCts = new CancellationTokenSource();
        DetailText = "";
        DetailBusy = true;
        var progress = new Progress<string>(note => { if (id == _codeRun) DetailNote = note; });
        try
        {
            var (text, note) = await _code((int)row[IndexColumn], progress, cts.Token).ConfigureAwait(true);
            if (id != _codeRun) return;
            DetailText = text;
            DetailNote = note;
        }
        catch (OperationCanceledException) { /* a newer row was picked */ }
        catch (Exception ex)
        {
            AppPaths.Log("Code view failed: " + Key, ex);
            if (id == _codeRun) { DetailText = ""; DetailNote = Loc.Get("Code.Err.Failed"); }
        }
        finally
        {
            if (id == _codeRun) DetailBusy = false;
            if (ReferenceEquals(_codeCts, cts)) _codeCts = null;
            cts.Dispose();
        }
    }

    /// <summary>The page dropped this tab (new file, language change): stop its background fetch.</summary>
    public void Cancel() => _codeCts?.Cancel();

    public string HeaderFor(string columnName) =>
        columnName.Length > 1 && columnName[0] == 'c' && int.TryParse(columnName.AsSpan(1), out var i) && i < Columns.Length ? Columns[i] : columnName;

    private Type InferType(int col)
    {
        Type? t = null;
        foreach (var r in _rows)
        {
            var v = col < r.Length ? r[col] : null;
            if (v is null) continue;
            var vt = v is int or long ? typeof(long) : v is double or float ? typeof(double) : typeof(string);
            if (t is null) t = vt;
            else if (t != vt) return typeof(string);
        }
        return t ?? typeof(string);
    }

    private void Rebuild()
    {
        if (!IsTable) return;
        var q = _search.Trim();
        var table = new DataTable { Locale = CultureInfo.CurrentCulture };
        table.Columns.Add(IndexColumn, typeof(int));
        for (var c = 0; c < Columns.Length; c++) table.Columns.Add("c" + c, _types[c]);

        var matched = 0;
        table.BeginLoadData();
        for (var i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            if (q.Length > 0 && !r.Any(v => v is not null && Cell(v).Contains(q, StringComparison.OrdinalIgnoreCase))) continue;
            matched++;
            if (table.Rows.Count >= MaxRows) continue;
            var values = new object?[Columns.Length + 1];
            values[0] = i;
            for (var c = 0; c < Columns.Length; c++)
            {
                var v = c < r.Length ? r[c] : null;
                values[c + 1] = v is null ? DBNull.Value : _types[c] == typeof(string) ? Cell(v) : v is int n ? (long)n : v;
            }
            table.Rows.Add(values);
        }
        table.EndLoadData();

        SelectedRow = null;
        View = table.DefaultView;
        Note = matched > MaxRows ? Loc.Format("Ins.Capped", MaxRows.ToString("N0", CultureInfo.CurrentCulture), matched.ToString("N0", CultureInfo.CurrentCulture))
            : q.Length > 0 ? Loc.Format("Ins.Matched", matched.ToString("N0", CultureInfo.CurrentCulture), _rows.Count.ToString("N0", CultureInfo.CurrentCulture))
            : "";
    }

    private static string Cell(object v) => v switch
    {
        bool b => b ? "✓" : "",
        IFormattable f => f.ToString(null, CultureInfo.CurrentCulture),
        _ => v.ToString() ?? "",
    };
}
