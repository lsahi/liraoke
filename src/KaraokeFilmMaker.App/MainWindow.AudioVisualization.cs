using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Rendering;
using KaraokeFilmMaker.Domain.Projects;
using KaraokeFilmMaker.Domain.Time;
using KaraokeFilmMaker.Infrastructure.Audio;

namespace KaraokeFilmMaker.App;

public partial class MainWindow
{
    private AudioVisualizationData? _audioVisualizationData;
    private string? _audioVisualizationKey;
    private string? _audioVisualizationFailedKey;
    private bool _refreshingAudioVisualizationFields;
    private Func<string, string, CancellationToken, Task<AudioVisualizationData>>? _audioVisualizationLoader = null;

    private async void AudioVisualization_Click(object sender, RoutedEventArgs e) =>
        await SetAudioVisualizationAsync(AudioVisualizationCheck.IsChecked == true);

    private async void AudioVisualizationStyle_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingAudioVisualizationFields || AudioVisualizationStyleCombo?.SelectedItem is not ComboBoxItem item
            || !Enum.TryParse<AudioVisualizationStyle>(item.Tag?.ToString(), out var style)) { return; }
        await SetAudioVisualizationStyleAsync(style);
    }

    private async Task SetAudioVisualizationStyleAsync(AudioVisualizationStyle style)
    {
        if (_workspace is null || _isBusy || !Enum.IsDefined(style) || !CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit())
        { RefreshAudioVisualizationFields(); return; }
        if (_workspace.Project.AudioVisualizationStyle == style) { return; }
        await RunUiOperationAsync(() =>
        {
            _workspace = _workspace with { Project = _workspace.Project with
            { AudioVisualizationStyle = style, ModifiedAtUtc = DateTimeOffset.UtcNow } };
            RefreshAudioVisualizationFields();
            RefreshAudioVisualizationPreview(_playback.Position);
            StatusText.Text = $"已选择{AudioVisualizationStyleName(style)}；不改歌曲、歌词打点或节拍器。";
            return Task.CompletedTask;
        });
    }

    private static string AudioVisualizationStyleName(AudioVisualizationStyle style) => style switch
    {
        AudioVisualizationStyle.BottomDense => "B · 底部密集频谱",
        AudioVisualizationStyle.CenterWhite => "C · 居中白色频谱",
        AudioVisualizationStyle.RotatingRing => "D · 自旋环形频谱",
        AudioVisualizationStyle.Fireworks => "E · 中低频烟花",
        _ => "A · 经典左下频谱",
    };

    private async Task SetAudioVisualizationAsync(bool enabled)
    {
        if (_workspace is null || _isBusy || !CanLeaveSourceLyricEdit() || !CanLeaveQuickEdit())
        { RefreshAudioVisualizationFields(); return; }
        await RunUiOperationAsync(async () =>
        {
            var candidate = _workspace.Project with { AudioVisualizationEnabled = enabled };
            if (enabled) { await PrepareAudioVisualizationAsync(candidate, explicitRequest: true); }
            _workspace = _workspace with { Project = candidate with { ModifiedAtUtc = DateTimeOffset.UtcNow } };
            if (!enabled) { ClearAudioVisualization(); }
            RefreshAudioVisualizationFields();
            RefreshAudioVisualizationPreview(_playback.Position);
            StatusText.Text = enabled ? "音频可视化已开启，仅显示在预览和最终视频中；歌词打轴页保持原媒体。" : "音频可视化已关闭。";
        });
    }

    private async Task PrepareAudioVisualizationAsync(KaraokeProject project, bool explicitRequest = false)
    {
        if (_workspace is null || !project.AudioVisualizationEnabled) { ClearAudioVisualization(); return; }
        var master = project.MediaAssets.FirstOrDefault(asset => asset.Id == project.MasterAudioAssetId);
        if (master is null) { ClearAudioVisualization(); return; }
        string source = GetAssetAbsolutePath(master);
        var sourceInfo = new System.IO.FileInfo(source);
        string revision = sourceInfo.Exists ? $"{sourceInfo.Length}|{sourceInfo.LastWriteTimeUtc.Ticks}" : "missing";
        string key = _workspace.RootPath + "|" + master.Id + "|" + source + "|" + revision;
        if (_audioVisualizationKey == key && _audioVisualizationData is not null) { return; }
        if (!explicitRequest && _audioVisualizationFailedKey == key) { return; }
        _audioVisualizationData = null; _audioVisualizationKey = null;
        AudioVisualizationStatusText.Text = "正在本地准备频谱缓存，可取消…";
        try
        {
            var token = _operationCancellation?.Token ?? CancellationToken.None;
            var load = _audioVisualizationLoader ?? new AudioVisualizationCache(_mediaTools).GetAsync;
            var data = await load(source, _workspace.RootPath, token);
            token.ThrowIfCancellationRequested();
            _audioVisualizationData = data; _audioVisualizationKey = key; _audioVisualizationFailedKey = null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            _audioVisualizationFailedKey = key;
            AudioVisualizationStatusText.Text = $"频谱暂不可用：{error.Message} 可关闭后重试，歌曲与打轴不受影响。";
            if (explicitRequest) { throw; }
        }
    }

    private void ClearAudioVisualization()
    {
        _audioVisualizationData = null; _audioVisualizationKey = null; _audioVisualizationFailedKey = null;
        AudioVisualizationPreview?.Update(null, ProjectTime.Zero);
    }

    private void RefreshAudioVisualizationFields()
    {
        if (AudioVisualizationCheck is null) { return; }
        bool enabled = _workspace?.Project.AudioVisualizationEnabled == true;
        AudioVisualizationCheck.IsChecked = enabled;
        AudioVisualizationCheck.IsEnabled = _workspace is not null;
        if (AudioVisualizationStyleCombo is not null)
        {
            _refreshingAudioVisualizationFields = true;
            try
            {
                var style = _workspace?.Project.AudioVisualizationStyle ?? AudioVisualizationStyle.Classic;
                AudioVisualizationStyleCombo.SelectedItem = AudioVisualizationStyleCombo.Items.Cast<ComboBoxItem>()
                    .FirstOrDefault(item => item.Tag?.ToString() == style.ToString());
                AudioVisualizationStyleCombo.IsEnabled = _workspace is not null && !_isBusy;
            }
            finally { _refreshingAudioVisualizationFields = false; }
        }
        if (!enabled) { AudioVisualizationStatusText.Text = "默认关闭 · 可配合图片背景"; }
        else if (_workspace?.Project.MasterAudioAssetId is null) { AudioVisualizationStatusText.Text = "已开启 · 导入歌曲后生成频谱"; }
        else if (_audioVisualizationData is not null)
        {
            AudioVisualizationStatusText.Text = _workspace?.Project.AudioVisualizationStyle switch
            {
                AudioVisualizationStyle.RotatingRing => "白色半透明 · 每 100 秒顺时针一圈 · 原歌曲时间",
                AudioVisualizationStyle.Fireworks => "白色半透明短柱 · 约 40–818 Hz 驱动 · 跳转 / 导出一致",
                AudioVisualizationStyle.CenterWhite => "白色半透明 · 中心线向上下展开 · 预览 / 导出同步",
                _ => "浅蓝 / 浅黄 · 原歌曲时间 · 预览 / 导出同步",
            };
        }
        else if (_audioVisualizationFailedKey is null) { AudioVisualizationStatusText.Text = "频谱缓存尚未准备；关闭后重新开启可重试。"; }
    }

    private void RefreshAudioVisualizationPreview(ProjectTime position)
    {
        if (AudioVisualizationPreview is null) { return; }
        bool show = IsPreviewMode && _workspace?.Project.AudioVisualizationEnabled == true;
        AudioVisualizationPreview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AudioVisualizationPreview.Update(show ? _audioVisualizationData : null, position,
            _workspace?.Project.AudioVisualizationStyle ?? AudioVisualizationStyle.Classic);
    }
}
