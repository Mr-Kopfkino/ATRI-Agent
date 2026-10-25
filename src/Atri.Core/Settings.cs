using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Atri.Core;
public sealed class Settings
{
    public string ApiBaseUrl {get;set;}="";
    public string Model {get;set;}="";
    public bool StructuredOutput {get;set;}=true;
    public double Temperature {get;set;}=.8;
    public int TimeoutSeconds {get;set;}=90;
    public int MaxReplyTokens {get;set;}=1600;
    public bool ModelMemoryEnabled {get;set;}=true;
    public int MemorySummaryTokens {get;set;}=16384;
    public string SubtitleLanguage {get;set;}="zh";
    public double VoiceVolume {get;set;}=.85;
    public double EffectVolume {get;set;}=.22;
    public string DisplayMode {get;set;}="window";
    public bool BgmEnabled {get;set;}=true;
    public double BgmVolume {get;set;}=.25;
    public bool VoiceEnabled {get;set;}=true;
    public string VoiceModel {get;set;}="AtriTurbo";
    public string AsrBaseUrl {get;set;}="";
    public string AsrProvider {get;set;}="local";
    public string AsrModel {get;set;}="";
    public string InputLanguage {get;set;}="zh";
    public int MicrophoneDevice {get;set;}=0;
    public bool VadEnabled {get;set;}=false;
    public bool WakeEnabled {get;set;}=false;
    public bool WakeAutoSend {get;set;}=false;
    public string UserName {get;set;}="";
    public string LastSessionId {get;set;}=Guid.NewGuid().ToString("N");
    public string PythonPath {get;set;}="";
    public void Validate()
    {
        DisplayMode ??="window";
        if(new string?[]{ApiBaseUrl,Model,SubtitleLanguage,VoiceModel,AsrProvider,AsrBaseUrl,AsrModel,InputLanguage,UserName,LastSessionId,PythonPath}.Any(value=>value is null))throw new InvalidDataException("配置包含空字符串字段。");
        if(!double.IsFinite(Temperature)||!double.IsFinite(VoiceVolume)||!double.IsFinite(EffectVolume)||!double.IsFinite(BgmVolume))throw new InvalidDataException("数值配置必须是有限数字。");
        if(LastSessionId.Length is <1 or >128)throw new InvalidDataException("会话标识无效。");
        if(UserName.Length>64||Model.Length>256||AsrModel.Length>256||ApiBaseUrl.Length>2048||AsrBaseUrl.Length>2048)throw new InvalidDataException("称呼、模型名称或 API 地址过长。");
        foreach(var url in new[]{ApiBaseUrl,AsrBaseUrl}) if(url.Length>0 && (!Uri.TryCreate(url,UriKind.Absolute,out var uri)|| !(uri.Scheme=="https" || (uri.Scheme=="http"&&uri.IsLoopback)))) throw new InvalidDataException("API 地址须为 HTTPS；本机服务可使用 HTTP。");
        if(TimeoutSeconds is <10 or >300 || Temperature is <0 or >2)throw new InvalidDataException("超时或温度超出允许范围。");
        if(MaxReplyTokens<=0)throw new InvalidDataException("回复 token 上限必须为正整数；具体可用上限由所选模型决定。");
        if(MemorySummaryTokens<=0)throw new InvalidDataException("记忆整理 token 上限必须为正整数。");
        if(VoiceVolume is <0 or >1 || EffectVolume is <0 or >1 || BgmVolume is <0 or >1)throw new InvalidDataException("音量须为 0–1。");
        if(DisplayMode is not ("window" or "fullscreen"))throw new InvalidDataException("显示模式须为 window 或 fullscreen。");
        if(SubtitleLanguage is not ("zh" or "ja") || InputLanguage is not ("zh" or "ja"))throw new InvalidDataException("语言须为 zh 或 ja。");
        if(AsrProvider is not ("local" or "api"))throw new InvalidDataException("语音识别来源须为 local 或 api。");
    }
}
public sealed class SettingsStore(AppPaths paths)
{
    public Settings Load()
    {
        var f=paths.WithinRoot("data/config/settings.json"); if(!File.Exists(f))return new();
        try
        {
            var json=JsonNode.Parse(File.ReadAllText(f));
            if(json is JsonObject obj)
            {
                // New optional fields in older/hand-edited files can be null.
                // Repair only these fields without losing valid API configuration.
                if(obj.ContainsKey("DisplayMode")&&obj["DisplayMode"] is null)obj["DisplayMode"]="window";
                if(obj.ContainsKey("BgmEnabled")&&obj["BgmEnabled"] is null)obj["BgmEnabled"]=true;
                if(obj.ContainsKey("BgmVolume")&&obj["BgmVolume"] is null)obj["BgmVolume"]=.25;
                if(obj.ContainsKey("ModelMemoryEnabled")&&obj["ModelMemoryEnabled"] is null)obj["ModelMemoryEnabled"]=true;
                if(obj.ContainsKey("MemorySummaryTokens")&&obj["MemorySummaryTokens"] is null)obj["MemorySummaryTokens"]=16384;
            }
            var s=json?.Deserialize<Settings>()??new(); s.Validate(); return s;
        }
        catch(Exception e) when(e is JsonException or InvalidDataException) {paths.RejectLinks(f+".invalid");File.Copy(f,f+".invalid",true); paths.Log("config","配置损坏；保留 invalid 副本，恢复默认。");return new();}
    }
    public void Save(Settings settings)
    {
        settings.Validate(); var target=paths.WithinRoot("data/config/settings.json");var tmp=paths.WithinRoot("data/config/settings.json.tmp");
        File.WriteAllText(tmp,JsonSerializer.Serialize(settings,new JsonSerializerOptions{WriteIndented=true})); File.Move(tmp,target,true);
    }
    public void SaveSecret(string name,string value)
    {
        var target=SecretPath(name); if(value.Length==0){ if(File.Exists(target))File.Delete(target);return; }
        paths.RejectLinks(target+".tmp");var bytes=Encoding.UTF8.GetBytes(value);try{var encrypted=Dpapi.Transform(bytes,true); File.WriteAllBytes(target+".tmp",encrypted);File.Move(target+".tmp",target,true);}finally{Array.Clear(bytes);}
    }
    public string ReadSecret(string name)
    {var f=SecretPath(name);return File.Exists(f)?Encoding.UTF8.GetString(Dpapi.Transform(File.ReadAllBytes(f),false)):"";}
    public bool HasSecret(string name)=>File.Exists(SecretPath(name));
    private string SecretPath(string name)=>paths.WithinRoot("data/config/"+(name is "llm" or "asr"?name+".dpapi":throw new ArgumentException("未知凭据名称")));
}
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)]private struct Blob {public int Size;public IntPtr Data;}
    [DllImport("crypt32.dll",SetLastError=true)]private static extern bool CryptProtectData(ref Blob input,string? desc,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("crypt32.dll",SetLastError=true)]private static extern bool CryptUnprotectData(ref Blob input,IntPtr desc,IntPtr entropy,IntPtr reserved,IntPtr prompt,int flags,out Blob output);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr p);
    public static byte[] Transform(byte[] bytes,bool encrypt)
    {
        var input=new Blob{Size=bytes.Length,Data=Marshal.AllocHGlobal(bytes.Length)};Marshal.Copy(bytes,0,input.Data,bytes.Length);
        try {Blob output;var ok=encrypt?CryptProtectData(ref input,"ATRI local credential",IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output):CryptUnprotectData(ref input,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,IntPtr.Zero,1,out output);if(!ok)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{var result=new byte[output.Size];Marshal.Copy(output.Data,result,0,result.Length);return result;}finally{LocalFree(output.Data);}}
        finally {for(int i=0;i<input.Size;i++)Marshal.WriteByte(input.Data,i,0);Marshal.FreeHGlobal(input.Data);}
    }
}
