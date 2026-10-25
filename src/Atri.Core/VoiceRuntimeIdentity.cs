using System.Security.Cryptography;
using System.Text.Json;

namespace Atri.Core;

/// <summary>The loaded desktop and its private worker must agree on one runtime.</summary>
internal sealed class VoiceRuntimeIdentity
{
    private static readonly string[] RequiredFiles =
    [
        "runtime/voice/engine.py", "runtime/voice/completeness.py", "runtime/voice/worker.py",
        "runtime/voice/vendor/AR/models/t2s_model.py"
    ];
    private readonly string root;
    private readonly Dictionary<string,string> files;
    private readonly string? manifestHash;
    private VoiceRuntimeIdentity(string root,Dictionary<string,string> files,string? manifestHash){this.root=root;this.files=files;this.manifestHash=manifestHash;}

    internal static VoiceRuntimeIdentity Capture(AppPaths paths)
    {
        try
        {
            var files=RequiredFiles.ToDictionary(file=>file,file=>Hash(paths.WithinRoot(file)),StringComparer.Ordinal);
            var manifest=paths.WithinRoot("release-manifest.json");
            return new(paths.Root,files,ReadManifest(manifest,files));
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidDataException)
        {throw new VoiceRuntimeMismatchException();}
    }
    internal void VerifyFiles(AppPaths paths)
    {
        try
        {
            if(!string.Equals(root,paths.Root,StringComparison.OrdinalIgnoreCase)
                ||files.Any(pair=>!pair.Value.Equals(Hash(paths.WithinRoot(pair.Key)),StringComparison.OrdinalIgnoreCase)))
                throw new VoiceRuntimeMismatchException();
            var manifest=paths.WithinRoot("release-manifest.json");
            if(manifestHash is not null)
            {if(!File.Exists(manifest)||!manifestHash.Equals(Hash(manifest),StringComparison.OrdinalIgnoreCase))throw new VoiceRuntimeMismatchException();}
            else ReadManifest(manifest,files);
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or InvalidDataException)
        {throw new VoiceRuntimeMismatchException();}
    }
    internal void VerifyHealth(JsonElement health,int pid)
    {
        try
        {
            if(health.ValueKind!=JsonValueKind.Object||health.GetProperty("status").GetString()!="ready"
                ||health.GetProperty("pid").GetInt32()!=pid)throw new VoiceRuntimeMismatchException();
            var policy=health.GetProperty("voicePolicy");
            if(policy.GetProperty("id").GetString()!="ja-voice-r4"||policy.GetProperty("maxAttempts").GetInt32()!=8)
                throw new VoiceRuntimeMismatchException();
            var identity=health.GetProperty("runtimeIdentity");
            var reportedRoot=identity.GetProperty("root").GetString();
            if(string.IsNullOrWhiteSpace(reportedRoot)||!Path.IsPathFullyQualified(reportedRoot)
                ||!Path.TrimEndingDirectorySeparator(Path.GetFullPath(reportedRoot)).Equals(root,StringComparison.OrdinalIgnoreCase))
                throw new VoiceRuntimeMismatchException();
            var reportedFiles=identity.GetProperty("files");
            if(reportedFiles.ValueKind!=JsonValueKind.Object)throw new VoiceRuntimeMismatchException();
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var file in reportedFiles.EnumerateObject())
                if(!seen.Add(file.Name)||!files.TryGetValue(file.Name,out var expected)||file.Value.ValueKind!=JsonValueKind.String
                    ||file.Value.GetString() is not {} value||!value.Equals(expected,StringComparison.OrdinalIgnoreCase))
                    throw new VoiceRuntimeMismatchException();
            if(seen.Count!=files.Count)throw new VoiceRuntimeMismatchException();
        }
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException
            or OverflowException or ArgumentException or NotSupportedException)
        {throw new VoiceRuntimeMismatchException();}
    }
    private static string Hash(string file)
    {using var stream=File.OpenRead(file);return Convert.ToHexString(SHA256.HashData(stream));}
    private static string? ReadManifest(string file,IReadOnlyDictionary<string,string> expected)
    {
        if(Directory.Exists(file))throw new VoiceRuntimeMismatchException();
        if(!File.Exists(file))return null; // The developer checkout has no release manifest.
        try
        {
            var info=new FileInfo(file);if(info.Length is <=0 or >32*1024*1024)throw new VoiceRuntimeMismatchException();
            var bytes=File.ReadAllBytes(file);using var document=JsonDocument.Parse(bytes);
            if(document.RootElement.GetProperty("schemaVersion").GetInt32()!=1)throw new VoiceRuntimeMismatchException();
            var entries=document.RootElement.GetProperty("files");
            if(entries.ValueKind!=JsonValueKind.Array)throw new VoiceRuntimeMismatchException();
            var seen=new HashSet<string>(StringComparer.Ordinal);
            foreach(var entry in entries.EnumerateArray())
            {
                var name=entry.GetProperty("path").GetString();if(name is null||!expected.TryGetValue(name,out var hash))continue;
                if(!seen.Add(name)||entry.GetProperty("sha256").GetString() is not {} recorded
                    ||!recorded.Equals(hash,StringComparison.OrdinalIgnoreCase))throw new VoiceRuntimeMismatchException();
            }
            if(seen.Count!=expected.Count)throw new VoiceRuntimeMismatchException();
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException
            or InvalidOperationException or FormatException or OverflowException)
        {throw new VoiceRuntimeMismatchException();}
    }
}

public sealed class VoiceRuntimeMismatchException():IOException("本地语音组件版本不匹配；已阻止使用旧版或不一致的组件。请修复当前发布版后重试。")
{
}
