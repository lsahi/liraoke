using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using KaraokeFilmMaker.Application.Separation;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Separation;

namespace KaraokeFilmMaker.App;

public partial class VocalSeparationWindow : Window
{
    private readonly DemucsRuntimeManager _runtime;
    private readonly string _source;
    private readonly string _outputDirectory;
    private readonly MediaToolchain _tools;
    private GpuAvailability _gpu = new(false, "正在检测显卡…");
    private CancellationTokenSource? _cancellation;
    private bool _busy, _closeRequested;
    public string? GeneratedPath { get; private set; }

    public VocalSeparationWindow(string source, string songName, string outputDirectory, MediaToolchain tools,
        DemucsRuntimeManager? runtime = null)
    {
        InitializeComponent();
        _source = source; _outputDirectory = outputDirectory; _tools = tools;
        _runtime = runtime ?? new DemucsRuntimeManager();
        SongText.Text = songName;
        RefreshState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await DetectAsync();
    private async void Detect_Click(object sender, RoutedEventArgs e) => await DetectAsync();
    private async Task DetectAsync()
    {
        await RunAsync(async token =>
        {
            _gpu = await Task.Run(NvidiaGpuDetector.Detect, token);
            TaskText.Text = _gpu.Reason;
            EngineCombo.SelectedIndex = _gpu.Supported && _runtime.InstalledGpuPath is not null ? 1 : 0;
        });
    }

    private void RefreshState()
    {
        bool installed = _runtime.InstalledGpuPath is not null;
        GpuText.Text = _gpu.Reason;
        InstallGpuButton.IsEnabled = !_busy && _gpu.Supported && _runtime.CpuInstalled;
        InstallGpuButton.ToolTip = !_gpu.Supported ? _gpu.Reason : "从 PyTorch 官方源下载，校验后安装到用户目录，不修改系统 Python。";
        InstallGpuButton.Content = installed ? "修复 GPU 组件" : "下载并启用 GPU 加速";
        RuntimeText.Text = !_runtime.CpuInstalled ? "CPU 基础组件缺失，请使用完整 Liraoke 发布包。"
            : installed ? "CPU 已就绪 · GPU 组件已安装；GPU 异常时可自动回退 CPU。"
            : "CPU 已就绪 · GPU 官方下载约 3.22 GiB，安装需至少 12 GiB 临时空间。";
        GpuEngineItem.IsEnabled = _gpu.Supported && installed;
        if (!GpuEngineItem.IsEnabled) EngineCombo.SelectedIndex = 0;
        DetectButton.IsEnabled = !_busy;
        UninstallButton.IsEnabled = !_busy && installed;
        OptionsPanel.IsEnabled = !_busy;
        GenerateButton.IsEnabled = !_busy && _runtime.CpuInstalled && File.Exists(_source);
        CancelTaskButton.IsEnabled = _busy;
    }

    private IProgress<SeparationProgress> Progress() => new Progress<SeparationProgress>(p =>
    {
        if (!_busy) return;
        TaskText.Text = p.Message;
        TaskProgress.IsIndeterminate = p.Fraction is null;
        if (p.Fraction is { } fraction) TaskProgress.Value = Math.Clamp(fraction, 0, 1);
    });

    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_busy) return;
        _busy = true;
        _cancellation = new CancellationTokenSource();
        RefreshState();
        try { await action(_cancellation.Token); }
        catch (OperationCanceledException) { TaskText.Text = "已取消。下载进度保留，可稍后重试；未完成的分离结果已清理。"; }
        catch (Exception ex)
        {
            TaskText.Text = "操作失败；CPU 基础组件和原歌曲未被替换。";
            string logDirectory = Path.Combine(_runtime.ComponentPath, "logs");
            try { Directory.CreateDirectory(logDirectory); await File.WriteAllTextAsync(Path.Combine(logDirectory, "last-error.txt"), ex.ToString()); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            MessageBox.Show(this, ex.Message.Length > 1500 ? ex.Message[..1500] : ex.Message, "人声分离", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false; _cancellation.Dispose(); _cancellation = null;
            TaskProgress.IsIndeterminate = false;
            RefreshState();
            if (_closeRequested) _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    private async void InstallGpu_Click(object sender, RoutedEventArgs e)
    {
        if (!_gpu.Supported || _busy) return;
        if (MessageBox.Show(this,
            $"将从 PyTorch 官方源下载约 3.22 GiB，并安装到：\n{_runtime.ComponentPath}\n\n需至少 12 GiB 可用空间；不会安装或更新系统显卡驱动。\n请先通过 CUDA / cuDNN 许可按钮查看适用条款。确认接受这些条款并开始下载？",
            "安装 GPU 加速", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await RunAsync(async token =>
        {
            _gpu = await Task.Run(NvidiaGpuDetector.Detect, token);
            await _runtime.InstallGpuAsync(_gpu, Progress(), token);
            EngineCombo.SelectedIndex = 1;
            TaskText.Text = "GPU 加速已启用，自检通过。";
        });
    }

    private async void Generate_Click(object sender, RoutedEventArgs e)
    {
        bool generated = false;
        var engine = EngineCombo.SelectedIndex == 1 ? SeparationEngine.Gpu : SeparationEngine.Cpu;
        var output = (SeparationOutput)OutputCombo.SelectedIndex;
        await RunAsync(async token =>
        {
            GeneratedPath = await new DemucsSeparationService(_runtime, _tools).SeparateAsync(
                new(_source, _outputDirectory, engine, output, _gpu.Device?.Index ?? 0), Progress(), token);
            generated = true;
        });
        if (generated && !_closeRequested) { DialogResult = true; }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "仅卸载 Liraoke 的 GPU 加速组件。CPU、模型、系统驱动和已生成音频保持不变。继续？",
            "卸载 GPU 加速", MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        await RunAsync(async token => { await Task.Run(_runtime.UninstallGpu, token); TaskText.Text = "GPU 组件已卸载，继续使用 CPU。"; });
    }

    private void CancelTask_Click(object sender, RoutedEventArgs e) => _cancellation?.Cancel();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy) return;
        e.Cancel = true; _closeRequested = true; _cancellation?.Cancel();
        TaskText.Text = "正在取消并清理，请稍候…";
    }
    private void Licenses_Click(object sender, RoutedEventArgs e) => OpenWebsite("https://docs.nvidia.com/cuda/eula/");
    private void CudnnLicense_Click(object sender, RoutedEventArgs e) => OpenWebsite("https://docs.nvidia.com/deeplearning/cudnn/backend/latest/reference/eula.html");
    private void Driver_Click(object sender, RoutedEventArgs e) => OpenWebsite("https://www.nvidia.com/Download/index.aspx");
    private void OpenWebsite(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Win32Exception) { MessageBox.Show(this, "无法打开浏览器，请访问：\n" + url); }
    }
}
