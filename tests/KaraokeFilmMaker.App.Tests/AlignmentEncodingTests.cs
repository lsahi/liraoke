using System.Reflection;
using System.Text;
using KaraokeFilmMaker.Infrastructure.Alignment;
namespace KaraokeFilmMaker.App.Tests;
public class AlignmentEncodingTests
{
    private sealed class Capture : IProgress<string> {public readonly List<string> Messages=[];public void Report(string value){lock(Messages)Messages.Add(value);}}
    [Fact]public async Task ChineseProgressAndPipByteProgressSurviveLegacyConsoleEncoding()
    {
        string root=Path.Combine(Path.GetTempPath(),Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);var original=Console.OutputEncoding;
        try
        {
            Console.OutputEncoding=Encoding.GetEncoding(936);var progress=new Capture();string log=Path.Combine(root,"install.log");
            string node=Path.Combine(AppContext.BaseDirectory,"tools/ai-runtime/node.exe");
            var method=typeof(LocalAlignmentService).GetMethod("RunAsync",BindingFlags.Static|BindingFlags.NonPublic)!;
            await (Task)method.Invoke(null,[node,new[]{"-e","console.log(JSON.stringify({message:'正在下载模型，安装完成'}));console.log('Progress 1048576 of 2097152');"},log,progress,CancellationToken.None,null])!;
            Assert.Contains("正在下载模型，安装完成",progress.Messages);Assert.Contains(progress.Messages,x=>x.Contains("依赖下载：1.0 / 2.0 MiB"));Assert.Contains("正在下载模型",File.ReadAllText(log,Encoding.UTF8));
        }
        finally{Console.OutputEncoding=original;Directory.Delete(root,true);}
    }
}
