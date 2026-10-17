using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Japanese;
using KaraokeFilmMaker.Domain.Lyrics;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Application.Languages;

namespace KaraokeFilmMaker.App;

public partial class LyricUnitEditorWindow : Window
{
    private readonly LyricLine _line;
    private readonly LyricLanguage _language;
    private readonly ObservableCollection<UnitRow> _rows = [];

    public LyricUnitEditorWindow(LyricLine line, LyricLanguage language = LyricLanguage.Japanese)
    {
        InitializeComponent();
        _line = line;
        _language = language;
        LineText.Text = line.PrimaryText;
        foreach (LyricUnit unit in line.Units)
        {
            _rows.Add(CreateRow(unit));
        }

        UnitsGrid.ItemsSource = _rows;
    }

    public LyricLine? EditedLine { get; private set; }

    private UnitRow CreateRow(LyricUnit unit) => new(unit, _line.PrimaryText)
    {
        Reading = unit.Reading,
        Romanization = unit.Romanization,
        StartSeconds = unit.Timing.Start.TotalSeconds,
        EndSeconds = unit.Timing.End.TotalSeconds,
    };

    private void CommitGrid()
    {
        if (!UnitsGrid.CommitEdit(DataGridEditingUnit.Cell, true)
            || !UnitsGrid.CommitEdit(DataGridEditingUnit.Row, true))
        {
            throw new InvalidOperationException("请修正单元表格中的无效数值。 ");
        }
    }

    private void MergeButton_Click(object sender, RoutedEventArgs e) => RunEdit(() =>
    {
        CommitGrid();
        int[] indexes = UnitsGrid.SelectedItems.Cast<UnitRow>()
            .Select(row => _rows.IndexOf(row)).Order().ToArray();
        if (indexes.Length < 2 || indexes[^1] - indexes[0] + 1 != indexes.Length)
        {
            throw new InvalidOperationException("请选中至少两个相邻的演唱单元。");
        }

        LyricUnit[] units = indexes.Select(index => _rows[index].ToUnit()).ToArray();
        if (units.Select(unit => unit.LanguageId ?? LanguageIds.FromLegacy(_language)).Distinct().Count() > 1)
        { throw new InvalidOperationException("不同语言的单元不能直接合并。请先用“语言 / 片段”指定统一演唱语言。"); }
        var merged = new LyricUnit(
            Guid.NewGuid(), units.SelectMany(unit => unit.DisplayRanges).Distinct().OrderBy(range => range.Start).ToArray(),
            string.Concat(units.Select(unit => unit.Reading)),
            string.Concat(units.Select(unit => unit.Romanization)),
            new TimeRange(units[0].Timing.Start, units[^1].Timing.End),
            units[0].RawInputTime, IsConfirmed: false, LanguageId: units[0].LanguageId);
        for (int index = indexes[^1]; index >= indexes[0]; index--)
        {
            _rows.RemoveAt(index);
        }

        _rows.Insert(indexes[0], CreateRow(merged));
    });

    private void SplitButton_Click(object sender, RoutedEventArgs e) => RunEdit(() =>
    {
        CommitGrid();
        if (UnitsGrid.SelectedItems.Count != 1 || UnitsGrid.SelectedItem is not UnitRow row)
        {
            throw new InvalidOperationException("请选中一个需要拆分的演唱单元。");
        }

        LyricUnit unit = row.ToUnit();
        string? languageId = unit.LanguageId ?? LanguageIds.FromLegacy(_language);
        if (languageId is not (LanguageIds.Japanese or LanguageIds.Chinese or LanguageIds.English))
        { throw new InvalidOperationException("此语言的重新分组请使用“语言 / 片段”，由对应语言插件处理。现有单元的读音和时间仍可直接编辑。"); }
        LyricLanguage language = LanguageIds.ToLegacy(languageId);
        IReadOnlyList<string> mora = language == LyricLanguage.Japanese
            ? KanaRomanizer.SplitSingingUnits(unit.Reading)
            : LyricImportService.CreateUnits(unit.Reading, unit.Timing, language).Select(item => item.Reading).ToArray();
        if (mora.Count < 2)
        {
            throw new InvalidOperationException("此单元已经是一个演唱音节 / 单词，无需进一步拆分。");
        }

        int position = _rows.IndexOf(row);
        _rows.RemoveAt(position);
        for (var index = 0; index < mora.Count; index++)
        {
            long start = unit.Timing.Start.Microseconds + unit.Timing.Duration.Microseconds * index / mora.Count;
            long end = unit.Timing.Start.Microseconds + unit.Timing.Duration.Microseconds * (index + 1) / mora.Count;
            LyricUnit split = unit with
            {
                Id = Guid.NewGuid(), Reading = mora[index], Romanization = language == LyricLanguage.Japanese
                    ? KanaRomanizer.RomanizeSingingUnit(mora[index], index + 1 < mora.Count ? mora[index + 1] : null) : null,
                Timing = new TimeRange(new ProjectTime(start), new ProjectTime(end)),
                RawInputTime = index == 0 ? unit.RawInputTime : null,
                IsConfirmed = false,
            };
            _rows.Insert(position + index, CreateRow(split));
        }
    });

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => RunEdit(() =>
    {
        CommitGrid();
        EditedLine = BuildEditedLine();
        DialogResult = true;
    });

    private LyricLine BuildEditedLine()
    {
        LyricUnit[] units = _rows.Select(row => row.ToUnit()).ToArray();
            units = units.Select((unit, index) => (unit.LanguageId ?? LanguageIds.FromLegacy(_language)) == LanguageIds.Japanese
                && _rows[index].Reading != _rows[index].Source.Reading
                && _rows[index].Romanization == _rows[index].Source.Romanization
                    ? unit with { Romanization = KanaRomanizer.RomanizeSingingUnit(unit.Reading, index + 1 < units.Length ? units[index + 1].Reading : null) }
                    : unit).ToArray();
        for (var index = 0; index < units.Length; index++)
        {
            LyricUnit unit = units[index];
            if (string.IsNullOrWhiteSpace(unit.Reading)
                || unit.DisplayRanges.Count == 0 || unit.DisplayRanges.Any(range => range.EndExclusive > _line.PrimaryText.Length)
                || unit.Timing.Start < _line.Timing.Start || unit.Timing.End > _line.Timing.End
                || (index > 0 && unit.Timing.Start < units[index - 1].Timing.End))
            {
                throw new InvalidOperationException("单元必须有读音，时间需位于歌词行内，且按顺序排列、不重叠。");
            }
            if (_line.LanguageSpans is { Count: > 0 } spans && !spans.Any(span =>
                unit.DisplayRanges.All(range => range.Start >= span.Range.Start && range.EndExclusive <= span.Range.EndExclusive)
                && (unit.LanguageId is null || unit.LanguageId == span.LanguageId)))
            { throw new InvalidOperationException("对应歌词不能跨越语言片段；请先在“语言 / 片段”中调整语言范围。"); }
        }

        string reading = string.Concat(units.Select(unit => unit.Reading));
        bool readingsChanged = !units.Select(unit => (unit.Reading, unit.Romanization))
            .SequenceEqual(_line.Units.Select(unit => (unit.Reading, unit.Romanization)));
        var edited = _line with
        {
            Units = units,
            ReadingText = readingsChanged ? reading : _line.ReadingText,
            AnnotationText = readingsChanged ? reading : _line.AnnotationText,
            RomanizationText = readingsChanged
                ? string.Join(" ", units.Select(unit => unit.Romanization).Where(value => !string.IsNullOrWhiteSpace(value))) : _line.RomanizationText,
            ReadingSource = readingsChanged ? ReadingSource.Manual : _line.ReadingSource,
        };
        // Mixed-language snapshots are refreshed by the caller using each language policy.
        // Older Japanese lines without spans must not retain a stale saved mapping either.
        return edited.LanguageSpans is null && _language == LyricLanguage.Japanese && edited.Presentation is not null
            ? edited with { Presentation = LanguagePresentation.BuildJapanese(edited) } : edited;
    }

    private void MoveUnitUpButton_Click(object sender, RoutedEventArgs e) => MoveUnit(-1);
    private void MoveUnitDownButton_Click(object sender, RoutedEventArgs e) => MoveUnit(1);

    private void MoveUnit(int offset) => RunEdit(() =>
    {
        CommitGrid();
        int index = UnitsGrid.SelectedIndex, target = index + offset;
        if (index < 0 || target < 0 || target >= _rows.Count) { return; }
        TimeRange[] slots = _rows.Select(row => row.ToUnit().Timing).ToArray();
        _rows.Move(index, target);
        LyricUnit[] units = _rows.Select((row, position) => row.ToUnit() with { Timing = slots[position], IsConfirmed = false }).ToArray();
        _rows.Clear();
        foreach (var unit in units) { _rows.Add(CreateRow(unit)); }
        UnitsGrid.SelectedIndex = target;
    });

    private void RunEdit(Action edit)
    {
        try
        {
            edit();
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "演唱单元编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed class UnitRow(LyricUnit source, string sourceText) : INotifyPropertyChanged
    {
        private IReadOnlyList<TextRange> _ranges = source.DisplayRanges;
        public event PropertyChangedEventHandler? PropertyChanged;
        public LyricUnit Source { get; } = source;
        public string DisplayText
        {
            get => string.Join(" / ", _ranges.Select(range => sourceText.Substring(range.Start, range.Length)));
            set
            {
                if (value == DisplayText) { return; }
                SetRanges(LyricTextMapping.Resolve(sourceText, value, _ranges));
            }
        }
        public required string Reading { get; set; }
        public string MappingText
        {
            get => LyricTextMapping.Format(_ranges);
            set => SetRanges(LyricTextMapping.Parse(sourceText, value));
        }
        private void SetRanges(IReadOnlyList<TextRange> ranges)
        {
            _ranges = ranges;
            PropertyChanged?.Invoke(this, new(nameof(DisplayText)));
            PropertyChanged?.Invoke(this, new(nameof(MappingText)));
        }
        public string? Romanization { get; set; }
        public double StartSeconds { get; set; }
        public double EndSeconds { get; set; }

        public LyricUnit ToUnit() => Source with
        {
            Reading = Reading,
            Romanization = Romanization,
            DisplayRanges = _ranges,
            Timing = new TimeRange(ProjectTime.FromSeconds(StartSeconds), ProjectTime.FromSeconds(EndSeconds)),
            IsConfirmed = Source.IsConfirmed || ProjectTime.FromSeconds(StartSeconds) != Source.Timing.Start
                || ProjectTime.FromSeconds(EndSeconds) != Source.Timing.End,
        };
    }
}
