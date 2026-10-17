using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private bool _synchronizingMetronomeTuning;
    private int SelectedSnapDivisions => MetronomeSubdivisionCombo?.SelectedItem is ComboBoxItem item
        && int.TryParse(item.Tag?.ToString(), out int divisions) ? divisions : 1;

    private void MetronomeSubdivision_Changed(object sender, SelectionChangedEventArgs e) => RefreshMetronomeTuningFields();

    // These controls edit a draft only. Audio is reconfigured by the existing Apply action.
    private void RefreshMetronomeTuningFields()
    {
        if (_synchronizingMetronomeTuning || MetronomeBpmWheel is null || MetronomeOffsetWheel is null
            || MetronomeDraftText is null || MetronomeBeatGuide is null) { return; }
        _synchronizingMetronomeTuning = true;
        try
        {
            bool bpmValid = TryReadMetronomeBpm(out double bpm);
            bool offsetValid = int.TryParse(MetronomeOffsetText.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int offset)
                && Math.Abs((long)offset) <= MetronomeSettings.MaximumAbsoluteOffsetMilliseconds;
            if (bpmValid) { MetronomeBpmWheel.Value = bpm; }
            if (offsetValid) { MetronomeOffsetWheel.Value = offset; }
            MetronomeBeatGuide.Update(bpmValid ? bpm : double.NaN, offsetValid ? offset : null, SelectedSnapDivisions);
            bool pending = bpmValid && offsetValid
                && (bpm != CurrentMetronome.BeatsPerMinute || offset != CurrentMetronome.OffsetMilliseconds
                    || SelectedSnapDivisions != CurrentMetronome.SnapDivisions);
            MetronomeDraftText.Text = !bpmValid || !offsetValid ? "请检查参数范围"
                : pending ? "草稿 · 点应用生效" : "参数已同步";
        }
        finally { _synchronizingMetronomeTuning = false; }
    }

    private void MetronomeTuningText_Changed(object sender, TextChangedEventArgs e) => RefreshMetronomeTuningFields();

    private void MetronomeTuningWheel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_synchronizingMetronomeTuning || MetronomeBpmText is null || MetronomeOffsetText is null) { return; }
        if (ReferenceEquals(sender, MetronomeBpmWheel))
        { MetronomeBpmText.Text = e.NewValue.ToString("0.########", CultureInfo.CurrentCulture); }
        else if (ReferenceEquals(sender, MetronomeOffsetWheel))
        { MetronomeOffsetText.Text = Math.Round(e.NewValue, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.CurrentCulture); }
        RefreshMetronomeTuningFields();
    }

    private bool TryReadMetronomeBpm(out double bpm) =>
        double.TryParse(MetronomeBpmText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out bpm)
        && double.IsFinite(bpm) && bpm >= MetronomeSettings.MinimumBeatsPerMinute && bpm <= MetronomeSettings.MaximumBeatsPerMinute;

    private void HalveMetronomeBpm_Click(object sender, RoutedEventArgs e) => ScaleMetronomeBpmDraft(0.5);
    private void DoubleMetronomeBpm_Click(object sender, RoutedEventArgs e) => ScaleMetronomeBpmDraft(2);

    private void ScaleMetronomeBpmDraft(double multiplier)
    {
        if (!TryReadMetronomeBpm(out double bpm)) { return; }
        double next = bpm * multiplier;
        if (next < MetronomeSettings.MinimumBeatsPerMinute || next > MetronomeSettings.MaximumBeatsPerMinute)
        { MetronomeDraftText.Text = "超出 20–400 BPM，未修改"; return; }
        MetronomeBpmText.Text = next.ToString("0.########", CultureInfo.CurrentCulture);
    }
}
