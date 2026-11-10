using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using KaraokeFilmMaker.Application.Music;
using KaraokeFilmMaker.Infrastructure.Music;
using KaraokeFilmMaker.Infrastructure.Media;
using KaraokeFilmMaker.Infrastructure.Persistence;

namespace KaraokeFilmMaker.App.Tests;
public sealed class MusicSearchInterfaceTests
{
    [Fact] public Task SearchSelectionSettingsAndResponsiveLayout() => Sta(() =>
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));var store=new UserPreferenceStore(root);
        using var http=PublicMediaHttp.CreateClient();var fake=new Fake();
        var window=new MusicSearchWindow(new MusicSourceRegistry([fake,new QishuiMusicSourceAdapter(new MusicHostClient())]),
            new MusicAudioTransfer(MediaToolchain.Find(),new MusicHostClient(),http),store);
        try
        {
            window.Show();((ComboBox)window.FindName("SourceFilter")).SelectedValue="netease";
            ((TextBox)window.FindName("QueryInput")).Text="歌曲";
            var task=(Task)typeof(MusicSearchWindow).GetMethod("SearchAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
            Pump(()=>task.IsCompleted);task.GetAwaiter().GetResult();
            var grid=(DataGrid)window.FindName("ResultsGrid");Assert.Equal(2,grid.Items.Count);grid.SelectedIndex=0;
            Assert.True(((Button)window.FindName("PreviewButton")).IsEnabled);Assert.True(((Button)window.FindName("ImportButton")).IsEnabled);
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle); window.UpdateLayout();
            Assert.True(grid.Columns[0].ActualWidth >= 180);
            Shot(window,"music-search-wide.png");
            ((Expander)window.FindName("SettingsPanel")).IsExpanded=true;
            ((ComboBox)window.FindName("AccountSource")).SelectedValue="qishui";
            Assert.Equal(Visibility.Visible,((StackPanel)window.FindName("SignerPanel")).Visibility);
            ((TextBox)window.FindName("SignerInput")).Text="http://remote.invalid/sign";
            ((Button)window.FindName("CheckSignerButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>!((Button)window.FindName("CancelTaskButton")).IsEnabled);
            Assert.Contains("HTTPS",((TextBlock)window.FindName("StatusText")).Text);
            Assert.Empty(store.Load().MusicAccounts);
            ((TextBox)window.FindName("SignerInput")).Text="";
            window.Width=800;window.Height=620;window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);window.UpdateLayout();Shot(window,"music-search-settings-small.png");
            ((ComboBox)window.FindName("AccountSource")).SelectedValue="netease";
            Assert.Equal(Visibility.Collapsed,((StackPanel)window.FindName("SignerPanel")).Visibility);
            fake.CatalogOnly=true; grid.SelectedIndex=1;
            Assert.False(((Button)window.FindName("PreviewButton")).IsEnabled); Assert.False(((Button)window.FindName("ImportButton")).IsEnabled);
            fake.Block=true;
            task=(Task)typeof(MusicSearchWindow).GetMethod("SearchAsync",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(window,null)!;
            ((Button)window.FindName("CancelTaskButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>task.IsCompleted);task.GetAwaiter().GetResult();
            Assert.Contains("已取消",((TextBlock)window.FindName("StatusText")).Text);
        }
        finally {window.Close();if(Directory.Exists(root))Directory.Delete(root,true);}
    });
    [Fact] public async Task MusicProcessReportsMissingSetupWithoutExposingInput()
    {
        var host=new MusicHostClient();
        var exception=await Assert.ThrowsAsync<InvalidOperationException>(()=>host.ExecuteAsync<bool>(new {action="resolve",sourceId="qishui",id="123",cookie="private-test-cookie"}));
        Assert.Contains("签名",exception.Message);Assert.DoesNotContain("private-test-cookie",exception.Message);
    }
    [Fact] public async Task SignerProbeTraversesWorkerAndUsesOnlySyntheticPayload()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
        try
        {
            int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
            var server=Task.Run(async()=>
            {
                using var client=await listener.AcceptTcpClientAsync(timeout.Token);using var stream=client.GetStream();
                var header=new List<byte>();var single=new byte[1];
                while(header.Count<16384)
                {
                    await stream.ReadExactlyAsync(single,timeout.Token);header.Add(single[0]);
                    if(header.Count>=4 && header.TakeLast(4).SequenceEqual(new byte[]{13,10,13,10}))break;
                }
                string headers=System.Text.Encoding.ASCII.GetString(header.ToArray());
                Assert.StartsWith("POST /sign HTTP/1.1",headers);
                int length=int.Parse(headers.Split("\r\n").Single(x=>x.StartsWith("content-length:",StringComparison.OrdinalIgnoreCase)).Split(':')[1]);
                Assert.InRange(length,1,4096);var body=new byte[length];await stream.ReadExactlyAsync(body,timeout.Token);
                string json=System.Text.Encoding.UTF8.GetString(body);Assert.DoesNotContain("Cookie",json,StringComparison.OrdinalIgnoreCase);
                Assert.Contains("1000000000000000",json);
                const string result="{\"ok\":true,\"X-Helios\":\"test\",\"X-Medusa\":\"test\"}";
                byte[] response=System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {result.Length}\r\nConnection: close\r\n\r\n{result}");
                await stream.WriteAsync(response,timeout.Token);
            },timeout.Token);
            Assert.True(await new MusicHostClient().ExecuteAsync<bool>(new{action="checkSigner",sourceId="qishui",signerEndpoint=$"http://127.0.0.1:{port}/sign"},timeout.Token));
            await server;
        }
        finally {listener.Stop();}
    }

    private sealed class Fake : IMusicSourceAdapter
    {
        public bool Block; public bool CatalogOnly;
        public MusicSourceDescriptor Descriptor=>new("netease","网易云音乐",CatalogOnly ? MusicSourceCapabilities.Search : MusicSourceCapabilities.Search | MusicSourceCapabilities.Preview | MusicSourceCapabilities.ImportAudio,MediaAuthenticationKind.Cookie);
        public async Task<IReadOnlyList<MusicTrack>> SearchAsync(string query,MusicSourceCredentials credentials,CancellationToken cancellationToken=default)
        {
            if(Block)await Task.Delay(Timeout.Infinite,cancellationToken);
            return [new("netease","1","おかえりなさい (Remix)","示例歌手","同名版本请手动核对",215.4),new("netease","2","Song (Live)","Another Artist","Concert",198)];
        }
        public Task<ResolvedMusicAudio> ResolveAsync(MusicTrack track,MusicSourceCredentials credentials,CancellationToken cancellationToken=default)=>throw new NotSupportedException();
    }
    private static void Pump(Func<bool> done)
    {
        var until=DateTime.UtcNow.AddSeconds(15);
        while(!done()){Assert.True(DateTime.UtcNow<until);var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
            timer.Tick+=(_,_)=>{timer.Stop();frame.Continue=false;};timer.Start();Dispatcher.PushFrame(frame);}
    }
    private static Task Sta(Action action)
    {
        var result=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>{try{SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));action();result.SetResult();}catch(Exception e){result.SetException(e);}}){IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();return result.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }
    private static void Shot(Window window,string name)
    {
        string? dir=Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");if(dir is null)return;
        window.UpdateLayout();var content=(FrameworkElement)window.Content;
        var bitmap=new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth),(int)Math.Ceiling(window.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(content);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));Directory.CreateDirectory(dir);using var file=File.Create(Path.Combine(dir,name));encoder.Save(file);
    }
}
