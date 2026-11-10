using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using KaraokeFilmMaker.Application.Ai;

namespace KaraokeFilmMaker.Infrastructure.Ai;

public sealed record AiSessionState(IReadOnlyList<AiChatMessage> Messages, bool InFlight = false);
public sealed record AiUserMemory(Guid Id, string Text);

/// <summary>User-level state, independent of project files and provider credentials.</summary>
public sealed class AiStateStore(string root)
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KaraokeFilmMaker", "ai-state");
    public string Root { get; } = Path.GetFullPath(root);
    private string SessionFile(string scope) => "session-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scope))) + ".dat";
    private T Read<T>(string name, T fallback)
    {
        var path=Path.Combine(Root,name);
        if(!File.Exists(path))return fallback;
        if(new FileInfo(path).Length>32*1024*1024)throw new InvalidDataException("AI 会话文件过大，请先备份并开始新对话。");
        try { return JsonSerializer.Deserialize<T>(ProtectedData.Unprotect(File.ReadAllBytes(path),null,DataProtectionScope.CurrentUser),Json) ?? fallback; }
        catch(Exception e) when(e is CryptographicException or JsonException) { throw new InvalidDataException("AI 记忆文件无法读取，原文件已保留。",e); }
    }
    private void Write<T>(string name,T state)
    {
        Directory.CreateDirectory(Root);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(state,Json);
        if(bytes.Length>30*1024*1024)throw new InvalidDataException("AI 会话已达到保存上限，请开始新对话；旧记录仍保留。");
        string path=Path.Combine(Root,name),temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try { File.WriteAllBytes(temp,ProtectedData.Protect(bytes,null,DataProtectionScope.CurrentUser));File.Move(temp,path,true); }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
    public AiSessionState LoadSession(string scope) { lock(_gate)return Read(SessionFile(scope),new AiSessionState([])); }
    public void SaveSession(string scope,IReadOnlyList<AiChatMessage> messages,bool inFlight) { lock(_gate)Write(SessionFile(scope),new AiSessionState(messages.ToArray(),inFlight)); }
    public void ArchiveSession(string scope)
    {
        lock(_gate) { string path=Path.Combine(Root,SessionFile(scope));if(File.Exists(path))File.Move(path,path+"."+DateTime.UtcNow.ToString("yyyyMMddHHmmss")+"-"+Guid.NewGuid().ToString("N")+".archive"); }
    }
    public IReadOnlyList<AiUserMemory> Memories() {lock(_gate)return Read<AiUserMemory[]>("user-memory.dat",[]);}
    public void ReplaceMemories(IReadOnlyList<AiUserMemory> memories)
    {
        if(memories.Count>100||memories.Sum(x=>x.Text.Length)>8000||memories.Any(x=>string.IsNullOrWhiteSpace(x.Text)))throw new InvalidOperationException("记忆最多100条、合计8000字。");
        lock(_gate)Write("user-memory.dat",memories.ToArray());
    }
    public AiUserMemory Remember(string text)
    {
        lock(_gate) { var memory=new AiUserMemory(Guid.NewGuid(),text.Trim());ReplaceMemories([..Memories(),memory]);return memory; }
    }
    public void Forget(Guid id) {lock(_gate)ReplaceMemories(Memories().Where(m=>m.Id!=id).ToArray());}
}
