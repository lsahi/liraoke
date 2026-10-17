using System.Diagnostics;
using System.Globalization;
using System.Windows;
using KaraokeFilmMaker.Application.Abstractions;
using KaraokeFilmMaker.Infrastructure.Audio;
using KaraokeFilmMaker.Domain.Time;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private ITempoAnalysisService? _tempoAnalysisService;

    private async void AutoInitializeMetronome_Click(object sender, RoutedEventArgs e) => await AutoInitializeMetronomeAsync();

    private async Task AutoInitializeMetronomeAsync()
    {
        if (_workspace is null || _isBusy) { return; }
        if (!CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit()) { return; }
        var master = _workspace.Project.MediaAssets.FirstOrDefault(asset => asset.Id == _workspace.Project.MasterAudioAssetId);
        if (master is null)
        {
            MetronomeAnalysisText.Text = "请先导入歌曲或作为歌曲使用的视频，再自动初始化。";
            StatusText.Text = MetronomeAnalysisText.Text;
            return;
        }

        await RunUiOperationAsync(async () =>
        {
            StatusText.Text = "正在本地分析歌曲节拍（最多读取开头 90 秒），可点击上方“取消任务”…";
            MetronomeAnalysisText.Text = "分析中：只读取歌曲原音频，不受视频原声、播放倍速或试听节拍声影响。";
            var elapsed = Stopwatch.StartNew();
            try
            {
                var service = _tempoAnalysisService ??= new OfflineTempoAnalysisService(_mediaTools);
                var result = await service.AnalyzeAsync(GetAssetAbsolutePath(master), _operationCancellation!.Token);
                _operationCancellation.Token.ThrowIfCancellationRequested();
                bool valid = result.IsReliable && result.BeatsPerMinute is { } bpm && double.IsFinite(bpm)
                    && bpm >= MetronomeSettings.MinimumBeatsPerMinute && bpm <= MetronomeSettings.MaximumBeatsPerMinute
                    && result.OffsetMilliseconds is { } offset && Math.Abs((long)offset) <= MetronomeSettings.MaximumAbsoluteOffsetMilliseconds;
                if (!valid)
                {
                    MetronomeAnalysisText.Text = $"未取得可靠的初始化建议，原数值保持不变。{result.Message}";
                    StatusText.Text = $"节拍分析结束（{elapsed.Elapsed.TotalSeconds:0.0} 秒），请保留手动设置并试听。";
                    return;
                }

                // Results are only a draft. Never enable the metronome or rewrite saved taps automatically.
                MetronomeBpmText.Text = result.BeatsPerMinute!.Value.ToString("0.###", CultureInfo.CurrentCulture);
                MetronomeOffsetText.Text = result.OffsetMilliseconds!.Value.ToString(CultureInfo.CurrentCulture);
                RefreshMetronomeTuningFields();
                string alternatives = string.Join(" / ", result.AlternativeBeatsPerMinute.Where(double.IsFinite)
                    .Where(value => value >= MetronomeSettings.MinimumBeatsPerMinute && value <= MetronomeSettings.MaximumBeatsPerMinute)
                    .Distinct().Take(3).Select(value => value.ToString("0.##", CultureInfo.CurrentCulture)));
                double confidence = double.IsFinite(result.Confidence) ? Math.Clamp(result.Confidence, 0, 1) : 0;
                MetronomeAnalysisText.Text = $"建议已填入，点“应用”后试听。稳定性 {confidence:P0}（非准确率）。“÷2 / ×2”可修正半拍／倍拍。"
                    + (alternatives.Length > 0 ? $"候选 BPM：{alternatives}。" : "")
                    + $"{result.Message} 拍点偏移不等于小节第一拍。";
                StatusText.Text = $"本地节拍分析完成（{elapsed.Elapsed.TotalSeconds:0.0} 秒）；仅更新数值草稿，项目设置与已打时间未改变。";
            }
            catch (OperationCanceledException)
            {
                MetronomeAnalysisText.Text = "自动初始化已取消，原数值与项目设置保持不变。";
                throw;
            }
            catch
            {
                MetronomeAnalysisText.Text = "自动初始化失败，原数值与项目设置保持不变，可继续手动调整。";
                throw;
            }
        });
    }
}
