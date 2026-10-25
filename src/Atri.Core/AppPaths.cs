using System.Diagnostics;

namespace Atri.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string Data => Path.Combine(Root, "data");
    public string Temp => Path.Combine(Data, "temp");
    public string Config => Path.Combine(Data, "config");
    public string Memory => Path.Combine(Data, "memory");
    public AppPaths(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(Path.Combine(Root,"assets"))) throw new DirectoryNotFoundException("项目根目录缺少 assets。");
        RejectLinks(Data);
        foreach (var dir in new[] { Data,Temp,Config,Memory,Path.Combine(Data,"logs"),Path.Combine(Data,"cache") }) Directory.CreateDirectory(dir);
        foreach (var p in EnvironmentValues()) { RejectLinks(p.Value); Directory.CreateDirectory(p.Value); Environment.SetEnvironmentVariable(p.Key,p.Value); }
        Environment.SetEnvironmentVariable("DOTNET_CLI_TELEMETRY_OPTOUT","1");
        Environment.SetEnvironmentVariable("DOTNET_SKIP_FIRST_TIME_EXPERIENCE","1");
        Environment.SetEnvironmentVariable("MSBuildEnableWorkloadResolver","false");
    }
    public Dictionary<string,string> EnvironmentValues() => new()
    {
        ["TEMP"]=Temp,["TMP"]=Temp,["TMPDIR"]=Temp,["HF_HOME"]=Path.Combine(Data,"cache","huggingface"),
        ["TORCH_HOME"]=Path.Combine(Data,"cache","torch"),["PIP_CACHE_DIR"]=Path.Combine(Data,"cache","pip"),
        ["NUGET_PACKAGES"]=Path.Combine(Data,"cache","nuget"),["DOTNET_CLI_HOME"]=Path.Combine(Data,"cache","dotnet"),
        ["XDG_CACHE_HOME"]=Path.Combine(Data,"cache"),["MPLCONFIGDIR"]=Path.Combine(Data,"cache","matplotlib"),
        ["NUMBA_CACHE_DIR"]=Path.Combine(Data,"cache","numba"),["GRADIO_TEMP_DIR"]=Path.Combine(Temp,"gradio")
    };
    public void Apply(ProcessStartInfo psi) { foreach(var pair in EnvironmentValues()) psi.Environment[pair.Key]=pair.Value; psi.Environment["PYTHONUTF8"]="1"; psi.Environment["PYTHONDONTWRITEBYTECODE"]="1"; }
    public string WithinRoot(string relative)
    {
        var full=Path.GetFullPath(Path.Combine(Root,relative));
        var prefix=Path.EndsInDirectorySeparator(Root)?Root:Root+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("路径超出项目目录。");
        RejectLinks(full); return full;
    }
    public void RejectLinks(string full)
    {
        for(var current=full; current.Length>=Root.Length; current=Path.GetDirectoryName(current)??"")
            try{if((File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("受控目录不得使用链接或联接。");}catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
    }
    public long CleanTemporary(TimeSpan minimumAge)
    {
        RejectLinks(Temp);long bytes=0;var voice=Path.Combine(Temp,"voice");if(!Directory.Exists(voice))return 0;RejectLinks(voice);
        // Only registered abandoned voice jobs are ours to reclaim. Downloaded
        // archives may preserve old mtimes while still being used by an installer.
        foreach(var dir in Directory.EnumerateDirectories(voice))
        {
            try
            {
                RejectLinks(dir);var marker=Path.Combine(dir,".owner.json");if(!File.Exists(marker))continue;
                using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker));
                if(doc.RootElement.ValueKind!=System.Text.Json.JsonValueKind.Object || !doc.RootElement.TryGetProperty("pid",out var p) || p.ValueKind!=System.Text.Json.JsonValueKind.Number || !p.TryGetInt32(out var pid) || pid<=0)continue;
                try{using var process=Process.GetProcessById(pid);if(!process.HasExited)continue;}catch(ArgumentException){}
                if(File.GetLastWriteTimeUtc(marker)>DateTime.UtcNow-minimumAge)continue;
                var files=EnumerateSafe(dir).ToArray();var locks=new List<FileStream>();
                try{foreach(var f in files)locks.Add(new FileStream(f,FileMode.Open,FileAccess.ReadWrite,FileShare.None));}
                finally{foreach(var held in locks)held.Dispose();}
                foreach(var f in files){bytes+=new FileInfo(f).Length;File.Delete(f);}RemoveEmptyDirectories(dir);
            }
            catch(IOException){}catch(UnauthorizedAccessException){}catch(System.Text.Json.JsonException){}catch(InvalidDataException){}
        }
        return bytes;
    }
    internal static bool IsVoiceRequestId(string value)=>value is {Length:>=1 and <=80} && value.All(c=>char.IsAsciiLetterOrDigit(c)||c is '_' or '-');
    internal bool TryCleanVoiceRequest(string requestId,int ownerPid)
    {
        if(!IsVoiceRequestId(requestId)||ownerPid<=0)return false;
        try
        {
            var dir=WithinRoot(Path.Combine("data","temp","voice",requestId));
            if(!Directory.Exists(dir))return true;
            var marker=Path.Combine(dir,".owner.json");RejectLinks(marker);
            if(!File.Exists(marker)||new FileInfo(marker).Length>16384)return false;
            using var doc=System.Text.Json.JsonDocument.Parse(File.ReadAllText(marker));var owner=doc.RootElement;
            if(owner.ValueKind!=System.Text.Json.JsonValueKind.Object || !owner.TryGetProperty("pid",out var p) || p.ValueKind!=System.Text.Json.JsonValueKind.Number || !p.TryGetInt32(out var pid) || pid!=ownerPid ||
                !owner.TryGetProperty("requestId",out var id) || id.ValueKind!=System.Text.Json.JsonValueKind.String || id.GetString()!=requestId ||
                !owner.TryGetProperty("kind",out var kind) || kind.ValueKind!=System.Text.Json.JsonValueKind.String || kind.GetString()!="voice")return false;
            try{using var active=Process.GetProcessById(pid);if(!active.HasExited)return false;}catch(ArgumentException){}
            var files=Directory.EnumerateFileSystemEntries(dir).ToArray();
            foreach(var file in files)
            {
                RejectLinks(file);if((File.GetAttributes(file)&FileAttributes.Directory)!=0)return false;
                var name=Path.GetFileName(file);
                if(name!=".owner.json"&&name!="joined.wav"&&!(name.Length==8&&name.EndsWith(".wav",StringComparison.Ordinal)&&name[..4].All(char.IsAsciiDigit)))return false;
            }
            var locks=new List<FileStream>();
            try{foreach(var file in files)locks.Add(new FileStream(file,FileMode.Open,FileAccess.ReadWrite,FileShare.None));}
            finally{foreach(var held in locks)held.Dispose();}
            // Keep the recovery marker if a later audio deletion fails.
            foreach(var file in files.Where(file=>!file.Equals(marker,StringComparison.OrdinalIgnoreCase)))File.Delete(file);
            File.Delete(marker);RemoveEmptyDirectories(dir);return !Directory.Exists(dir);
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception){return false;}
    }
    private void RemoveEmptyDirectories(string dir)
    {RejectLinks(dir);foreach(var child in Directory.EnumerateDirectories(dir))RemoveEmptyDirectories(child);if(!Directory.EnumerateFileSystemEntries(dir).Any())Directory.Delete(dir);}
    private IEnumerable<string> EnumerateSafe(string dir)
    {
        foreach(var entry in Directory.EnumerateFileSystemEntries(dir))
        { var a=File.GetAttributes(entry); if((a&FileAttributes.ReparsePoint)!=0) continue; if((a&FileAttributes.Directory)!=0) {foreach(var f in EnumerateSafe(entry)) yield return f;} else yield return entry; }
    }
    public void Log(string component,string message)
    {
        // Only pass application-owned diagnostics; never provider bodies, user text or credentials.
        // A pending factory transaction owns these files. UI/startup still shows
        // errors, but must not recreate a moved log and block crash recovery.
        if(File.Exists(Path.Combine(Data,"factory-reset","transaction.json")))return;
        try
        {
            lock(this) { var file=WithinRoot("data/logs/atri.log"); if(File.Exists(file)&&new FileInfo(file).Length>1_000_000) { for(int i=3;i>=1;i--) {var from=i==1?file:file+"."+(i-1);RejectLinks(file+"."+i);if(File.Exists(from))File.Move(from,file+"."+i,true);} } File.AppendAllText(file,$"{DateTimeOffset.Now:O} [{component}] {message}\n"); }
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            // A diagnostic write must not throw inside startup/UI error handling.
            // The original operation still reports its error to the user.
            Trace.WriteLine("ATRI diagnostic write unavailable: "+error.GetType().Name);
        }
    }
    public static string Discover()
    {
        // A packaged executable owns its adjacent data even when launched from a
        // developer shell that still has ATRI_ROOT set. Never climb out of it.
        foreach(var candidate in new[]{AppContext.BaseDirectory,Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))})
            if(candidate is not null && File.Exists(Path.Combine(candidate,"release-manifest.json")))
            {
                if(!IsRuntimeRoot(candidate))throw new DirectoryNotFoundException("发布目录缺少必要程序、assets 或声音组件；请核对 release-manifest.json。");
                return Path.GetFullPath(candidate);
            }
        var configured=Environment.GetEnvironmentVariable("ATRI_ROOT"); if(!string.IsNullOrWhiteSpace(configured))return configured;
        foreach(var start in new[]{AppContext.BaseDirectory,Environment.CurrentDirectory})
            for(var d=new DirectoryInfo(start);d!=null;d=d.Parent)if(IsRuntimeRoot(d.FullName) || (Directory.Exists(Path.Combine(d.FullName,"assets"))&&Directory.Exists(Path.Combine(d.FullName,"src"))))return d.FullName;
        throw new DirectoryNotFoundException("请通过项目目录中的启动入口运行。");
    }
    private static bool IsRuntimeRoot(string root)=>Directory.Exists(Path.Combine(root,"assets")) && File.Exists(Path.Combine(root,"runtime","voice","worker.py")) && File.Exists(Path.Combine(root,"app","atri_agent.exe")) ;
}
