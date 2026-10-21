using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private MetronomeSettings CurrentMetronome => _workspace?.Project.Metronome ?? new MetronomeSettings();
    // Hidden in 0.5: preserve saved settings, but never silently click or snap without an accessible switch.
    private MetronomeSettings EffectiveMetronome => MetronomeSection.Visibility == Visibility.Visible
        ? CurrentMetronome : CurrentMetronome with { Enabled = false };

    private void RefreshMetronomeFields()
    {
        var settings = CurrentMetronome;
        MetronomeSubdivisionCombo.SelectedItem = MetronomeSubdivisionCombo.Items.Cast<ComboBoxItem>()
            .First(item => item.Tag?.ToString() == settings.SnapDivisions.ToString(CultureInfo.InvariantCulture));
        MetronomeEnabledCheck.IsChecked = settings.Enabled;
        MetronomeBpmText.Text = settings.BeatsPerMinute.ToString("0.########", CultureInfo.CurrentCulture);
        MetronomeOffsetText.Text = settings.OffsetMilliseconds.ToString(CultureInfo.CurrentCulture);
        MetronomeStatusText.Text = settings.Enabled
            ? $"已开启 · {settings.BeatsPerMinute:0.###} BPM · 每拍 {settings.SnapDivisions} 格 · 仅新打点吸附"
            : "已关闭 · 自由打轴";
        RefreshMetronomeTuningFields();
        MetronomeAnalysisText.Text = "自动初始化在本地估计 BPM 和拍点偏移；结果需试听确认，不代表小节重拍。";
    }

    private async void ApplyMetronome_Click(object sender, RoutedEventArgs e) => await ApplyMetronomeSettingsAsync();
    private async void MetronomeEnabled_Click(object sender, RoutedEventArgs e) => await ApplyMetronomeSettingsAsync();

    private async Task ApplyMetronomeSettingsAsync()
    {
        if (_workspace is null || _isBusy) { RefreshMetronomeFields(); return; }
        if (!CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit())
        { MetronomeEnabledCheck.IsChecked = CurrentMetronome.Enabled; return; }
        if (!double.TryParse(MetronomeBpmText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double bpm)
            || !int.TryParse(MetronomeOffsetText.Text, NumberStyles.Integer, CultureInfo.CurrentCulture, out int offset))
        {
            MetronomeEnabledCheck.IsChecked = CurrentMetronome.Enabled;
            StatusText.Text = "BPM 请输入 20–400 的数值；偏移请输入整数毫秒（正数延后、负数提前）。";
            return;
        }
        var settings = new MetronomeSettings(MetronomeEnabledCheck.IsChecked == true, bpm, offset)
        { SnapDivisions = SelectedSnapDivisions };
        try { settings.Validate(); }
        catch (ArgumentException exception)
        {
            MetronomeEnabledCheck.IsChecked = CurrentMetronome.Enabled;
            StatusText.Text = exception.Message;
            return;
        }
        await RunUiOperationAsync(() =>
        {
            _workspace = _workspace with { Project = _workspace.Project with { Metronome = settings } };
            RefreshMetronomeFields();
            StatusText.Text = settings.Enabled
                ? $"节拍器已开启：每拍 {settings.SnapDivisions} 格，仅后续新打点吸附；已有打点与重做记录不变，试听按整拍，导出不含节拍声。"
                : "节拍器已关闭，后续新打点恢复自由打轴；已有打点和重做记录保持原时间。";
            return Task.CompletedTask;
        });
    }

    private async Task SynchronizeMetronomeAsync()
    {
        if (_playback is IMetronomePlaybackSession session)
        { await session.ConfigureMetronomeAsync(EffectiveMetronome, _operationCancellation?.Token ?? default); }
    }
}
