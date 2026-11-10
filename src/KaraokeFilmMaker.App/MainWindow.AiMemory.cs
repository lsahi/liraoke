using System.Text.Json;
using KaraokeFilmMaker.Infrastructure.Ai;

namespace KaraokeFilmMaker.App;
public partial class MainWindow
{
    private async Task<object> ExecuteMemoryToolAsync(string name,JsonElement args,CancellationToken token)
    {
        if(name=="get_user_memory")return new{memories=_aiState.Memories()};
        if(_aiActionPending)throw new InvalidOperationException("请先完成当前待批准操作。");
        string text=TextArg(args,"text")?.Trim()??"";
        Guid id=Guid.Empty;
        if(name=="remember_user_preference")
        {
            if(text.Length is 0 or >2000)throw new InvalidOperationException("单条偏好需要1–2000字。");
            if(_userPreferenceStore.ContainsCredential(text))throw new InvalidOperationException("记忆不能保存密钥，请使用配置窗口。");
        }
        else if(!Guid.TryParse(TextArg(args,"id"),out id))throw new InvalidOperationException("记忆 ID 无效。");
        _aiActionPending=true;
        try
        {
            string detail=name=="remember_user_preference"?text:_aiState.Memories().FirstOrDefault(m=>m.Id==id)?.Text??"这条记忆已不存在。";
            if(!await AssistantPanel.RequestApprovalAsync(name=="remember_user_preference"?"保存用户长期偏好":"删除用户长期偏好",detail,token))return new{approved=false};
            token.ThrowIfCancellationRequested();
            if(name=="remember_user_preference")return new{approved=true,memory=_aiState.Remember(text)};
            _aiState.Forget(id);return new{approved=true,deleted=true};
        }
        finally{_aiActionPending=false;}
    }
}
