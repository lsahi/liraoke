using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Infrastructure.Alignment;

namespace KaraokeFilmMaker.App.Dialogs;

public sealed class AlignmentPluginsWindow : Window
{
    private readonly LocalAlignmentService _service;
    private readonly StackPanel _items = new();
    private readonly TextBlock _status = new() { TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,12,0,0) };
    private CancellationTokenSource _cancel = new();
    private bool _installing;
    private readonly Button _importCache=new(){Content="导入本地下载文件…",Margin=new Thickness(0,12,0,0),Padding=new Thickness(8)};
    public AlignmentPluginsWindow(LocalAlignmentService service)
    {
        _service=service;Title="本地打轴插件";Width=660;Height=650;MinWidth=540;MinHeight=450;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel { Margin=new Thickness(20) };
        panel.Children.Add(new TextBlock { Text="本地模型 · 按需安装",FontSize=22,Margin=new Thickness(0,0,0,12) });
        panel.Children.Add(new TextBlock { Text="日语默认优先 NextFire，其他语言默认优先 Qwen；可同时安装并在打轴页手动选择。模型不需要 API Key。硬件检查是保守门槛，最终以真实推理自检为准。",TextWrapping=TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text="大文件默认 8 路分块下载，取消后保留完整分块。已有安装包或模型文件可先导入缓存，通过校验后复用。",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0) });
        _importCache.Click+=async(_,_)=>await ImportCacheAsync();panel.Children.Add(_importCache);
        panel.Children.Add(_items);panel.Children.Add(_status);
        var cancel=new Button { Content="取消当前操作",Margin=new Thickness(0,12,0,0),Padding=new Thickness(8) };cancel.Click+=(_,_)=>_cancel.Cancel();panel.Children.Add(cancel);
        Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        Closed+=(_,_)=>_cancel.Cancel();Refresh();
    }
    private async Task ImportCacheAsync()
    {
        if(_installing)return;
        var picker=new OpenFileDialog {Title="选择已有安装包或模型文件（必须与当前版本清单匹配）",Multiselect=true,Filter="安装包和模型|*.whl;*.safetensors;*.zip|所有文件|*.*"};
        if(picker.ShowDialog(this)!=true)return;
        _cancel.Dispose();_cancel=new();_installing=true;Refresh();
        try {await _service.ImportDownloadsAsync(picker.FileNames,new Progress<string>(m=>_status.Text=m),_cancel.Token);}
        catch(OperationCanceledException){_status.Text="导入已取消，已验证的缓存保留。";}
        catch(Exception error){_status.Text=error.Message;}
        finally{_installing=false;if(IsVisible)Refresh();}
    }
    private void Refresh()
    {
        _importCache.IsEnabled=!_installing;
        _items.Children.Clear();
        foreach(var s in _service.Status())
        {
            var block=new StackPanel { Margin=new Thickness(12) };
            block.Children.Add(new TextBlock { Text=s.Plugin.Name+(s.Installed?$" · 已安装 {s.Device}":" · 未安装"),FontSize=17 });
            block.Children.Add(new TextBlock { Text=s.Plugin.License+"\n"+s.Plugin.ModelUrl,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,5) });
            block.Children.Add(new TextBlock { Text=s.Message,TextWrapping=TextWrapping.Wrap });
            var buttons=new WrapPanel();
            foreach(string device in new[]{"cpu","gpu"})
            {
                var button=new Button { Content=(s.Installed?"重装 / 切换 ":"安装 ")+device.ToUpperInvariant(),Padding=new Thickness(10,5,10,5),Margin=new Thickness(0,8,8,0),IsEnabled=!_installing&&(device=="cpu"?s.CanInstallCpu:s.CanInstallGpu) };
                button.Click+=async(_,_)=>
                {
                    if(MessageBox.Show(this,$"{s.Plugin.Name}\n许可：{s.Plugin.License}\n将从官方来源下载独立运行环境及模型（模型约1.3–1.9GB，GPU环境还需数GB），在本机安装并运行自检。\n选择安装表示你已了解模型的适用范围。是否继续？","安装本地打轴插件",MessageBoxButton.OKCancel)!=MessageBoxResult.OK)return;
                    _cancel.Dispose();_cancel=new CancellationTokenSource();_installing=true;Refresh();
                    try { await _service.InstallAsync(s.Plugin.Id,device,new Progress<string>(m=>_status.Text=m),_cancel.Token); }
                    catch(OperationCanceledException){_status.Text="安装已取消，已有插件及完整下载分块保留；再次安装会校验并复用。";}
                    catch(Exception e){_status.Text=e.Message;}
                    finally{_installing=false;if(IsVisible)Refresh();}
                };buttons.Children.Add(button);
            }
            block.Children.Add(buttons);_items.Children.Add(new Border { Child=block,BorderBrush=System.Windows.Media.Brushes.LightSteelBlue,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(6),Margin=new Thickness(0,12,0,0) });
        }
    }
}
