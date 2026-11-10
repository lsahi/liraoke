using System.IO;
using System.Windows;
using System.Windows.Controls;
using KaraokeFilmMaker.Application.Ai;
using KaraokeFilmMaker.Infrastructure.Ai;

namespace KaraokeFilmMaker.App.Controls;
public partial class AiChatPanel
{
    private AiStateStore? _state;
    private bool _sessionReadable=true;
    private string SessionKey => _active?.Id.ToString()??"unconfigured";
    private void RestoreConversation()
    {
        _history.Clear();ClearInlineContent();Messages.Children.Clear();_sessionReadable=true;
        if(_active is null){AddNotice("请先添加并选择 AI 配置。");return;}
        try
        {
            var saved=_state?.LoadSession(SessionKey)??new AiSessionState([]);
            _history.AddRange(saved.Messages);
            AddNotice($"{_active.ProviderName} · 已恢复 {_history.Count} 条会话记录\n工具读取的项目数据会发送给所选 AI 服务。");
            foreach(var item in _history.TakeLast(60))AddNotice((item.Role=="user"?"你：":item.Role=="context"?"任务记录：":"AI：")+(item.Text.Length>2000?item.Text[..2000]+"…":item.Text));
            if(saved.InFlight)
            {
                _history.Add(new("context","上次客户端在任务或回复完成前结束。状态未知；继续前先查询项目/插件状态，不要把请求记录当成成功或自动重复操作。"));
                AddNotice("上次会话中断，记录已保留；继续时将先核对任务状态。");PersistConversation(false);
            }
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException) {_sessionReadable=false;AddNotice(e.Message+" 可备份后点击“新对话”。");}
    }
    private string SafeRecord(string text)
    {
        try {return _store?.ContainsCredential(text)==true?"[已过滤可能包含凭据的内容]":text;}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException){return "[凭据检查不可用，未保存这段内容]";}
    }
    private void PersistConversation(bool inFlight,string? partial=null)
    {
        if(_state is null||_active is null)return;
        var records=_history.ToList();
        if(!string.IsNullOrWhiteSpace(partial))records.Add(new("assistant",SafeRecord(partial)+"\n[生成尚未结束]"));
        _state.SaveSession(SessionKey,records,inFlight);
    }
    private void RememberContext(string text)
    {
        text=SafeRecord(text);
        _history.Add(new("context",text));PersistConversation(true);
    }
    private void Memory_Click(object sender,RoutedEventArgs e)
    {
        if(_state is null)return;
        try
        {
            var input=new TextBox {Text=string.Join("\n",_state.Memories().Select(m=>m.Text)),AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,MinHeight=220,MaxLength=8000,Margin=new Thickness(0,8,0,8)};
            var panel=new StackPanel {Margin=new Thickness(18)};
            panel.Children.Add(new TextBlock {Text="用户长期记忆",FontSize=20});
            panel.Children.Add(new TextBlock {Text="每行一条常用偏好；会提供给所选模型。删除对应行即可遗忘。聊天记录与长期记忆分别保存。",TextWrapping=TextWrapping.Wrap});panel.Children.Add(input);
            var save=new Button {Content="保存记忆",Padding=new Thickness(8)};panel.Children.Add(save);
            var window=new Window {Owner=Window.GetWindow(this),Title="AI 用户记忆",Width=510,Height=410,WindowStartupLocation=WindowStartupLocation.CenterOwner,Content=panel};
            save.Click+=(_,_)=>
            {
                try
                {
                    if(_store?.ContainsCredential(input.Text)==true)throw new InvalidOperationException("记忆不能包含密钥，请使用 AI 配置窗口。");
                    _state.ReplaceMemories(input.Text.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(t=>new AiUserMemory(Guid.NewGuid(),t)).ToArray());window.Close();
                }
                catch(Exception error)when(error is IOException or InvalidOperationException or UnauthorizedAccessException){MessageBox.Show(window,error.Message);}
            };
            window.ShowDialog();
        }
        catch(Exception error)when(error is IOException or UnauthorizedAccessException){AddNotice(error.Message);}
    }
}
