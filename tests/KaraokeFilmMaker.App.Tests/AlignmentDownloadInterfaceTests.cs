using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KaraokeFilmMaker.App.Dialogs;
using KaraokeFilmMaker.Infrastructure.Alignment;

namespace KaraokeFilmMaker.App.Tests;
public sealed class AlignmentDownloadInterfaceTests
{
    [Fact] public void CacheImportAndDownloadExplanationFitPluginWindow()
    {
        Exception? failure=null;
        var thread=new Thread(()=>
        {
            string root=Path.Combine(Path.GetTempPath(),"alignment-ui-"+Guid.NewGuid().ToString("N"));
            AlignmentPluginsWindow? window=null;
            try
            {
                window=new(new LocalAlignmentService(root));window.Show();window.UpdateLayout();
                IEnumerable<DependencyObject> Descendants(DependencyObject parent)
                {
                    for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
                    {
                        var child=VisualTreeHelper.GetChild(parent,i);yield return child;
                        foreach(var nested in Descendants(child))yield return nested;
                    }
                }
                var import=Assert.Single(Descendants(window).OfType<Button>(), b=>b.Content?.ToString()=="导入本地下载文件…");
                Assert.True(import.IsEnabled);Assert.True(import.IsVisible);
                Assert.Contains(Descendants(window).OfType<TextBlock>(),t=>t.Text.Contains("8 路分块"));
                foreach(var size in new[]{(660d,650d),(540d,450d)})
                {
                    window.Width=size.Item1;window.Height=size.Item2;window.UpdateLayout();
                    Assert.True(import.TranslatePoint(new Point(),window).Y+import.ActualHeight<window.ActualHeight);
                    string? snapshots=Environment.GetEnvironmentVariable("KFM_RENDER_SNAPSHOT_DIR");
                    if(snapshots is not null)
                    {
                        Directory.CreateDirectory(snapshots);
                        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
                        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var file=File.Create(Path.Combine(snapshots,$"alignment-download-{size.Item1}.png"));encoder.Save(file);
                    }
                }
                Assert.False(Directory.Exists(root));
            }
            catch(Exception error){failure=error;}
            finally{window?.Close();}
        });
        thread.SetApartmentState(ApartmentState.STA);thread.Start();Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if(failure is not null)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
