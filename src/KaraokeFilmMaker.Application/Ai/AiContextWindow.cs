namespace KaraokeFilmMaker.Application.Ai;

/// <summary>Bound the model input; full transcripts remain in the local session store.</summary>
public static class AiContextWindow
{
    public static IReadOnlyList<AiChatMessage> Select(IReadOnlyList<AiChatMessage> history,int budget=90000)
    {
        var recent=new List<AiChatMessage>();int size=0;
        foreach(var item in history.Reverse())
        {
            var text=item.Text.Length>20000?item.Text[..20000]+"\n[长记录已截取，需重新读取具体项目数据]":item.Text;
            if(size+text.Length>budget||recent.Count>=80)break;
            recent.Add(item with {Text=text});size+=text.Length;
        }
        recent.Reverse();
        if(recent.Count<history.Count)
        {
            // Keep a bounded reminder of early requests, without claiming an LLM summary is a fact.
            var goals=string.Join("\n",history.Take(history.Count-recent.Count).Where(x=>x.Role=="user").Take(8).Select(x=>x.Text[..Math.Min(x.Text.Length,500)]));
            recent.Insert(0,new("context","较早记录仍保存在本地；当前只携带近期上下文。早期用户请求摘录（可能已被后续要求更新）：\n"+goals));
        }
        return recent;
    }
}
