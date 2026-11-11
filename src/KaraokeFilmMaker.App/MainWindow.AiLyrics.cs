using System.Text.Json;
using KaraokeFilmMaker.Application.Ai;
namespace KaraokeFilmMaker.App;
public partial class MainWindow
{
    private async Task<object> ExecuteAiLyricsAsync(string name,JsonElement args,CancellationToken token)
    {
        if(_workspace is null)throw new InvalidOperationException("请先打开工作区。");
        if(_aiActionPending)throw new InvalidOperationException("请先完成待批准操作。");
        CheckAiDrafts();_aiActionPending=true;
        try
        {
            var original=_workspace.Project;string expected=ProjectContent(original);
            string action=name=="import_lyrics"?TextArg(args,"format")!:TextArg(args,"action")!;
            Guid? id=TextArg(args,"lineId") is {} raw?Guid.Parse(raw):null;
            int Number(string key,int fallback=0)=>args.TryGetProperty(key,out var v)?v.GetInt32():fallback;
            var candidate=await Task.Run(()=>AiLyricEdits.PrepareAsync(original.Lyrics,_languageAnalysis,action,id,
                TextArg(args,"expectedText"),TextArg(args,"text"),TextArg(args,"reading"),Number("rangeStart"),Number("rangeLength"),Number("destinationLine"),token),token);
            string detail=name=="import_lyrics"?"替换全部歌词与时间点：\n"+TextArg(args,"text"):
                $"操作：{action}\n原文：{TextArg(args,"expectedText")}\n新原文：{TextArg(args,"text")}\n读音：{TextArg(args,"reading")}\n原文片段：{Number("rangeStart")} 起，长度 {Number("rangeLength")}\n目标行：{Number("destinationLine")}";
            detail+="\n受影响单元将重建并清除失效打点；其他单元保留。批准后保存整个项目，可撤销。";
            if(!await AssistantPanel.RequestApprovalAsync("修改歌词",detail,token))return new {approved=false};
            CheckAiProject(original.Id);CheckAiDrafts();token.ThrowIfCancellationRequested();
            if(expected!=ProjectContent(_workspace!.Project))throw new InvalidOperationException("等待批准期间项目已变化，请重新发起。");
            bool applied=false,saved=false;string? error=null;
            await RunAiOperationAsync(async()=>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    CommitSourceLyricLines(candidate.Lines,candidate.Lines.FirstOrDefault()?.Id??Guid.Empty);
                    _workspace=_workspace! with {Project=_workspace.Project with {Lyrics=candidate}};
                    applied=true;await SaveAiWorkspaceAsync();saved=true;
                }
                catch(Exception e){error=e.Message;}
            });
            AssistantPanel.ReportActionResult(saved?"歌词已修改并保存，可撤销。":applied?"已修改但保存失败，请手动保存。":"歌词未修改。"+error);
            return new {approved=true,applied,saved,error};
        }
        finally{_aiActionPending=false;}
    }
}
