using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Atri.Core;

public sealed record LocalVadUtterance(byte[] Wav, double Duration, double StartSeconds);
public sealed record LocalVadFrame(bool SpeechDetected, double EnergyRms, IReadOnlyList<LocalVadUtterance> Utterances);

/// <summary>Parent-owned offline SenseVoice recognizer; microphone WAV bytes stay in memory.</summary>
public sealed class LocalAsrProcess(AppPaths paths) : IAsyncDisposable
{
    private Process? process;
    private Process? outputOwner;
    private KillJob? job;
    private Task? reader;
    private Task? errors;
    private readonly SemaphoreSlim lifecycle = new(1, 1), writer = new(1, 1), recognition = new(1, 1);
    private sealed record PendingRequest(Process Owner, TaskCompletionSource<JsonElement> Completion);
    private readonly ConcurrentDictionary<string, PendingRequest> pending = new();
    private long sequence;
    private bool disposed;
    public string State { get; private set; } = "未启动";
    public bool ModelExists => File.Exists(paths.WithinRoot("data/models/asr/SenseVoiceSmall/model.int8.onnx"));

    public async Task EnsureStartedAsync(CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Process? started = null;
        await lifecycle.WaitAsync(token);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (process is { HasExited: false } && reader is { IsCompleted: false }
                && ReferenceEquals(Volatile.Read(ref outputOwner), process)) return;
            // A live child with a completed output reader cannot answer new
            // requests. Retire its process handle and pipes before replacement.
            var retired = process; process = null;
            var retiredReader = reader; reader = null;
            var retiredErrors = errors; errors = null;
            var retiredJob = job; job = null;
            if (retired is not null) await RetireAsync(retired, retiredReader, retiredErrors, retiredJob);
            else retiredJob?.Dispose();
            if (!ModelExists) throw new FileNotFoundException("本地 SenseVoiceSmall 模型尚未部署，请运行环境检查。");
            var python = paths.WithinRoot("runtime/voice/python/python.exe");
            if (!File.Exists(python)) throw new FileNotFoundException("未找到受管理的 Python 运行环境。");
            State = "启动中";
            var start = new ProcessStartInfo(python)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = paths.Root,
                StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            start.ArgumentList.Add(paths.WithinRoot("runtime/asr/worker.py"));
            start.ArgumentList.Add("--root"); start.ArgumentList.Add(paths.Root); paths.Apply(start);
            process = Process.Start(start) ?? throw new IOException("本地语音识别进程启动失败。");
            started = process;
            job?.Dispose();
            try { job = new KillJob(process); }
            catch { try { process.Kill(true); } finally { CloseRedirectedStreams(process); process.Dispose(); process = null; } throw; }
            var active = process;
            Volatile.Write(ref outputOwner, active);
            reader = ReadLoop(active);
            errors = Task.Run(async () =>
            {
                try { while (await active.StandardError.ReadLineAsync() is { } line)
                    if (line.Contains("Traceback", StringComparison.Ordinal)) paths.Log("local-asr", "本地识别运行异常；原始录音未保存。"); }
                catch (IOException) { } catch (InvalidOperationException) { }
            });
        }
        finally { lifecycle.Release(); }
        try
        {
            var health = await RequestAsync("health", new { }, token, ensure: false, owner: started);
            if (health.GetProperty("status").GetString() != "ready") throw new IOException("本地识别模型尚未就绪。");
            State = "就绪";
        }
        catch { await StopOwnedAsync(started); throw; }
    }

    private async Task ReadLoop(Process active)
    {
        try
        {
            while (await active.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.Length > 4_000_000) throw new InvalidDataException("本地识别响应超出大小限制。");
                using var document = JsonDocument.Parse(line);
                var envelope = document.RootElement;
                if (!envelope.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String) continue;
                if (!pending.TryGetValue(id.GetString()!, out var request) || !ReferenceEquals(request.Owner, active)) continue;
                if (!pending.TryRemove(id.GetString()!, out _)) continue;
                if (envelope.TryGetProperty("ok", out var ok) && ok.GetBoolean()) request.Completion.TrySetResult(envelope.GetProperty("result").Clone());
                else
                {
                    var message = envelope.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var m) ? m.GetString() : "未知错误";
                    request.Completion.TrySetException(new IOException("本地语音识别失败：" + (message?.Length <= 300 ? message : "请检查运行诊断。")));
                }
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ObjectDisposedException or InvalidOperationException or KeyNotFoundException)
        { paths.Log("local-asr", error.GetType().Name); }
        finally
        {
            // Mark transport loss before draining pending requests. A request
            // registered after that drain must fail its send check, rather than
            // wait 120 seconds for a reader which has already stopped. An old
            // reader must never clear a replacement generation's marker.
            Interlocked.CompareExchange(ref outputOwner, null, active);
            if (ReferenceEquals(process, active)) State = "已停止";
            foreach (var entry in pending)
                if (ReferenceEquals(entry.Value.Owner, active) && pending.TryRemove(entry.Key, out var request)) request.Completion.TrySetException(new IOException("本地语音识别进程已退出；下一次请求将重新启动。"));
        }
    }

    private async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken token, bool ensure = true, Process? owner = null)
    {
        if (ensure) await EnsureStartedAsync(token);
        owner ??= process;
        if (owner is null) throw new IOException("本地识别进程不可用。");
        var id = Interlocked.Increment(ref sequence).ToString();
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = new(owner, completion);
        try
        {
            await writer.WaitAsync(token);
            try
            {
                if (!ReferenceEquals(process, owner) || owner.HasExited
                    || !ReferenceEquals(Volatile.Read(ref outputOwner), owner)) throw new IOException("本地识别进程不可用。");
                await owner.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { id, method, @params = parameters }).AsMemory(), token);
                await owner.StandardInput.FlushAsync(token);
            }
            finally { writer.Release(); }
            var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(120), token);
            if (!ReferenceEquals(process, owner)) throw new OperationCanceledException("本地识别进程已切换，旧结果已丢弃。", token);
            return result;
        }
        finally { pending.TryRemove(id, out _); }
    }

    public async Task<string> TranscribeAsync(byte[] wav, string language, CancellationToken token)
    {
        if (wav.Length is < 44 or > 4_000_000) throw new InvalidDataException("录音大小须为 44 字节到 4 MB。");
        if (language is not ("zh" or "ja" or "auto")) throw new InvalidDataException("本地识别语言须为中文、日语或自动。");
        await recognition.WaitAsync(token);
        Process? owner = null;
        try
        {
            await EnsureStartedAsync(token);
            owner = process;
            State = "识别中";
            var result = await RequestAsync("transcribe", new { audioBase64 = Convert.ToBase64String(wav), language }, token, ensure: false, owner: owner);
            var text = result.GetProperty("text").GetString()?.Trim() ?? "";
            if (text.Length == 0) throw new InvalidDataException("未识别到有效语音，请靠近麦克风后重试。");
            if (text.Length > 12000) throw new InvalidDataException("识别结果超出大小限制。");
            State = "就绪";
            return text;
        }
        catch (OperationCanceledException) { await StopOwnedAsync(owner); throw; }
        catch (TimeoutException) { await StopOwnedAsync(owner); throw; }
        catch { if (ReferenceEquals(process, owner)) State = "识别失败"; throw; }
        finally { recognition.Release(); }
    }

    public Task<JsonElement> HealthAsync(CancellationToken token) => RequestAsync("health", new { }, token);

    /// <summary>Feeds visible opt-in standby capture: mono 16 kHz PCM16, 512–16000 samples.</summary>
    public async Task<LocalVadFrame> PushVadFrameAsync(byte[] pcm16, CancellationToken token)
    {
        if (pcm16.Length is < 1024 or > 32000 || pcm16.Length % 2 != 0) throw new InvalidDataException("VAD 输入须为 16 kHz 单声道 PCM16。");
        await recognition.WaitAsync(token);
        Process? owner = null;
        try
        {
            await EnsureStartedAsync(token);
            owner = process;
            var response = await RequestAsync("vad_frame", new { pcm16Base64 = Convert.ToBase64String(pcm16) }, token, ensure: false, owner: owner);
            var utterances = new List<LocalVadUtterance>();
            foreach (var entry in response.GetProperty("utterances").EnumerateArray())
            {
                var encoded = entry.GetProperty("audioBase64").GetString() ?? "";
                if (encoded.Length > 2_000_000) throw new InvalidDataException("VAD 端点超过录音大小边界。");
                utterances.Add(new(Convert.FromBase64String(encoded), entry.GetProperty("duration").GetDouble(), entry.GetProperty("startSeconds").GetDouble()));
            }
            return new(response.GetProperty("speechDetected").GetBoolean(), response.GetProperty("energyRms").GetDouble(), utterances);
        }
        catch (OperationCanceledException) { await StopOwnedAsync(owner); throw; }
        catch (TimeoutException) { await StopOwnedAsync(owner); throw; }
        finally { recognition.Release(); }
    }

    public async Task ResetVadAsync(CancellationToken token)
    {
        if (process is not { HasExited: false }) return;
        await recognition.WaitAsync(token);
        try { await RequestAsync("vad_reset", new { }, token, ensure: false); }
        finally { recognition.Release(); }
    }

    private Task StopOwnedAsync(Process? owner) => owner is null ? Task.CompletedTask : StopCoreAsync(owner);
    public Task StopAsync() => StopCoreAsync(null);

    private async Task StopCoreAsync(Process? expected)
    {
        await lifecycle.WaitAsync();
        try
        {
            if (expected is not null && !ReferenceEquals(process, expected)) return;
            var active = process; process = null;
            var activeReader = reader; reader = null;
            var activeErrors = errors; errors = null;
            var ownedJob = job; job = null;
            if (active is not null) await RetireAsync(active, activeReader, activeErrors, ownedJob);
            else ownedJob?.Dispose();
            State = "已停止";
        }
        finally { lifecycle.Release(); }
    }

    private static async Task RetireAsync(Process active, Task? outputReader, Task? errorReader, KillJob? ownedJob)
    {
        try
        {
            try
            {
                if (!active.HasExited) active.Kill(entireProcessTree: true);
                await active.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
            finally { ownedJob?.Dispose(); } // Descendants must release inherited pipes before readers drain.
            try { await Task.WhenAll(outputReader ?? Task.CompletedTask, errorReader ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (TimeoutException) { }
        }
        finally
        {
            // Process.Dispose closes its process handle, not already obtained
            // redirected streams. Readers have seen EOF before closing them.
            CloseRedirectedStreams(active);
            active.Dispose();
            ownedJob?.Dispose();
        }
    }

    private static void CloseRedirectedStreams(Process active)
    {
        try { active.StandardInput.Dispose(); } catch (IOException) { } catch (InvalidOperationException) { }
        try { active.StandardOutput.Dispose(); } catch (IOException) { } catch (InvalidOperationException) { }
        try { active.StandardError.Dispose(); } catch (IOException) { } catch (InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync() { disposed = true; await StopAsync(); }
}
