using KaraokeFilmMaker.Application.Languages;
using KaraokeFilmMaker.Application.Lyrics;
using KaraokeFilmMaker.Domain.Lyrics;

namespace KaraokeFilmMaker.Application.Ai;
public static class AiLyricEdits
{
    public static async Task<LyricDocument> PrepareAsync(LyricDocument source, LanguageAnalysisService analysis,
        string action, Guid? lineId, string? expectedText, string? text, string? reading, int start, int length,
        int destination, CancellationToken token)
    {
        if(action is "txt" or "lrc")
        {
            if(string.IsNullOrWhiteSpace(text)||text.Length>50000)throw new InvalidOperationException("歌词为空或超过 50000 字符。");
            return await analysis.AnalyzeDocumentAsync(new LyricImportService().Parse(text,"."+action,source.Language), cancellationToken:token);
        }
        var lines=source.Lines.ToList();int index=lines.FindIndex(l=>l.Id==lineId);
        if(index<0||lines[index].PrimaryText!=expectedText)throw new InvalidOperationException("歌词行已变化，请重新读取歌词。");
        var line=lines[index];
        if(action=="delete")lines.RemoveAt(index);
        else if(action=="move")
        {
            if(destination<1||destination>lines.Count)throw new InvalidOperationException("目标行号无效。");
            lines.RemoveAt(index);lines.Insert(destination-1,line);
        }
        else if(action=="reading")
        {
            if(string.IsNullOrWhiteSpace(reading)||reading.Any(c=>c is '\r' or '\n'))throw new InvalidOperationException("请输入单行读音。");
            if(start<0||length<1||start>line.PrimaryText.Length-length)throw new InvalidOperationException("原文片段范围无效。");
            lines[index]=await analysis.SetLanguageAsync(line,new(start,length),"ja",reading,cancellationToken:token);
        }
        else if(action is "replace" or "insert_before" or "insert_after")
        {
            if(string.IsNullOrWhiteSpace(text)||text.Length>2000||text.Any(c=>c is '\r' or '\n'))throw new InvalidOperationException("请输入非空单句原文。");
            var fresh=new LyricImportService().Parse(text,".txt",source.Language).Lines.Single();
            if(action=="replace")fresh=fresh with {Id=line.Id,Timing=line.Timing,RoleIds=line.RoleIds,TranslationText=line.TranslationText};
            fresh=await analysis.AnalyzeLineAsync(fresh,LanguageIds.FromLegacy(source.Language),cancellationToken:token);
            if(action=="replace")lines[index]=fresh;else lines.Insert(index+(action=="insert_after"?1:0),fresh);
        }
        else throw new InvalidOperationException("未知歌词操作。");
        token.ThrowIfCancellationRequested();return source with {Lines=lines,Source=null};
    }
}
