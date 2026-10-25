using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Atri.Core;

internal sealed class MemoryEmbedding : IDisposable
{
    public const string ModelId = "intfloat/multilingual-e5-small@614241f622f53c4eeff9890bdc4f31cfecc418b3:mean-l2-v1";
    // Measured independent first launch took 91 seconds with bytecode disabled.
    // Controlled bytecode reduced cold/warm startup to 51/25 seconds; retain a
    // bounded first-launch margin for slower disks without changing query waits.
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(180);
    private static readonly object SharedGate = new();
    private static readonly Dictionary<string, MemoryEmbedding> Instances = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private int _foregroundWaiting;
    private readonly string _root;
    private int _references = 1;
    private Process? _process;
    private KillJob? _job;
    private Task? _errorReader;
    private volatile bool _ready;
    private DateTimeOffset _retryAfter;
    private readonly Queue<string> _errors = new();
    public string? Error { get; private set; }
    public bool Ready => _ready;

    private MemoryEmbedding(string root) { _root = root; }

    public static MemoryEmbedding? Find(string dataDirectory)
    {
        for (string? root = Path.GetFullPath(dataDirectory); root is not null; root = Path.GetDirectoryName(root))
        {
            if (!File.Exists(Path.Combine(root, "runtime", "memory", "embed_worker.py")) ||
                !File.Exists(Path.Combine(root, "data", "models", "multilingual-e5-small", "model.safetensors")) ||
                ResolvePython(root) is null)
            {
                // An independent deployment is a discovery boundary. A missing
                // local model must report fallback, never borrow a parent checkout.
                if (File.Exists(Path.Combine(root, "release-manifest.json"))) return null;
                continue;
            }
            lock (SharedGate)
            {
                if (Instances.TryGetValue(root, out MemoryEmbedding? existing)) { existing._references++; return existing; }
                var created = new MemoryEmbedding(root);
                Instances.Add(root, created);
                return created;
            }
        }
        return null;
    }

    public double[][]? Encode(IReadOnlyList<string> texts, string mode, CancellationToken cancellationToken, bool background = false)
    {
        if (texts.Count is < 1 or > 16 || mode is not ("query" or "passage")) throw new ArgumentException("嵌入请求越界。");
        cancellationToken.ThrowIfCancellationRequested();
        bool entered = false;
        if (!background) Interlocked.Increment(ref _foregroundWaiting);
        try
        {
            if (background)
            {
                if (Volatile.Read(ref _foregroundWaiting) > 0 || !Monitor.TryEnter(_gate)) return null;
                entered = true;
                if (Volatile.Read(ref _foregroundWaiting) > 0) return null;
            }
            else
            {
                while (!(entered = Monitor.TryEnter(_gate, 25))) cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Decrement(ref _foregroundWaiting);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow < _retryAfter) return null;
            try
            {
                EnsureStarted(cancellationToken);
                string line = JsonSerializer.Serialize(new { mode, texts });
                _process!.StandardInput.WriteLine(line);
                _process.StandardInput.Flush();
                // AnonymousPipeStream on Windows may perform synchronous IO under
                // ReadLineAsync. Do not cancel that underlying pipe read: cancel
                // the managed wait, then kill the worker so EOF releases the read.
                string? response = _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(45), cancellationToken).GetAwaiter().GetResult();
                if (response is null || response.Length > 500000) throw new IOException("本地 E5 worker 未返回有界响应。");
                using JsonDocument doc = JsonDocument.Parse(response);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new IOException("本地 E5 worker 返回的响应不是对象。");
                if (doc.RootElement.TryGetProperty("error", out _)) throw new IOException("本地 E5 worker 拒绝当前请求。");
                if (!doc.RootElement.TryGetProperty("vectors", out JsonElement payload) || payload.ValueKind != JsonValueKind.Array)
                    throw new IOException("本地 E5 worker 返回的响应缺少合法 vectors 数组。");
                double[][] vectors = payload.Deserialize<double[][]>() ?? [];
                if (vectors.Length != texts.Count || vectors.Any(vector => vector is null || vector.Length != 384 || vector.Any(value => !double.IsFinite(value))))
                    throw new IOException("本地 E5 返回的向量维度或数值不合法。");
                Error = null;
                return vectors;
            }
            catch (OperationCanceledException) { Stop(); throw; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException or TimeoutException or JsonException or System.ComponentModel.Win32Exception)
            {
                Error = "本地 E5 不可用：" + ex.Message;
                _retryAfter = DateTimeOffset.UtcNow.AddMinutes(1);
                Stop();
                return null;
            }
        }
        finally
        {
            if (!background && !entered) Interlocked.Decrement(ref _foregroundWaiting);
            if (entered) Monitor.Exit(_gate);
        }
    }

    private void EnsureStarted(CancellationToken cancellationToken)
    {
        if (_process is { HasExited: false }) return;
        Stop();
        string python = ResolvePython(_root) ?? throw new FileNotFoundException("本地 E5 Python 运行时不存在。");
        string worker = Path.Combine(_root, "runtime", "memory", "embed_worker.py");
        var start = new ProcessStartInfo(python)
        {
            WorkingDirectory = _root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        start.ArgumentList.Add("-u"); start.ArgumentList.Add(worker); start.ArgumentList.Add("--root"); start.ArgumentList.Add(_root);
        start.Environment["CUDA_VISIBLE_DEVICES"] = "";
        start.Environment["HF_HUB_OFFLINE"] = "1"; start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_IMPLICIT_TOKEN"] = "1";
        start.Environment["PYTHONIOENCODING"] = "utf-8";
        start.Environment["HF_HOME"] = PrepareControlledDirectory(Path.Combine("data", "cache", "huggingface"));
        start.Environment["TORCH_HOME"] = PrepareControlledDirectory(Path.Combine("data", "cache", "torch"));
        start.Environment["PYTHONPYCACHEPREFIX"] = PrepareControlledDirectory(Path.Combine("data", "cache", "pycache"));
        // Override a developer shell's no-bytecode setting only for this worker.
        // Imported code is cached under its own validated, writable data prefix.
        start.Environment["PYTHONDONTWRITEBYTECODE"] = "0";
        string temp = PrepareControlledDirectory(Path.Combine("data", "temp", "embedding"));
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp;
        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        Process startedProcess = _process;
        _process.Exited += (_, _) => { if (ReferenceEquals(_process, startedProcess)) _ready = false; };
        if (!_process.Start()) throw new IOException("无法启动本地 E5 CPU worker。");
        _job = new KillJob(_process);
        _errorReader = Task.Run(async () =>
        {
            try
            {
                while (await startedProcess.StandardError.ReadLineAsync() is { } line)
                    lock (_errors) { _errors.Enqueue(line.Length > 500 ? line[..500] : line); while (_errors.Count > 8) _errors.Dequeue(); }
            }
            catch (IOException) { } catch (InvalidOperationException) { }
        });
        string? response = WaitForStartupResponse(_process.StandardOutput.ReadLineAsync(), StartupTimeout, cancellationToken);
        if (response is null || response.Length > 500000) throw new IOException("本地 E5 CPU worker 启动未返回有界 ready 响应。");
        using var doc = JsonDocument.Parse(response);
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("ready", out JsonElement ready) || ready.ValueKind != JsonValueKind.True ||
            !doc.RootElement.TryGetProperty("dimension", out JsonElement dimension) || dimension.ValueKind != JsonValueKind.Number ||
            !dimension.TryGetInt32(out int count) || count != 384)
            throw new IOException("本地 E5 CPU worker 健康检查失败。");
        _ready = true;
    }

    private static string? WaitForStartupResponse(Task<string?> response, TimeSpan timeout, CancellationToken cancellationToken)
    {
        try { return response.WaitAsync(timeout, cancellationToken).GetAwaiter().GetResult(); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"本地 E5 CPU worker 启动超时（{timeout.TotalSeconds:0.###} 秒），尚未收到 ready；请检查本地模型与 data/cache/pycache 可写状态。", error);
        }
    }

    private string PrepareControlledDirectory(string relative)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_root));
        string directory = Path.GetFullPath(Path.Combine(root, relative));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本地 E5 受控目录超出项目范围。");
        try
        {
            RejectLinkAncestors(directory);
            Directory.CreateDirectory(directory);
            RejectLinkAncestors(directory);
            // Never overwrite a user's file. DeleteOnClose also handles failures
            // while checking that Python can actually create its bytecode cache.
            using var probe = new FileStream(Path.Combine(directory, ".e5-write-" + Guid.NewGuid().ToString("N") + ".tmp"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            probe.WriteByte(0);
            return directory;
        }
        catch (UnauthorizedAccessException error)
        {
            throw new IOException("本地 E5 受控目录没有写入权限：" + relative, error);
        }
        catch (IOException error)
        {
            throw new IOException("本地 E5 受控目录创建或写入失败：" + relative, error);
        }
    }

    private static void RejectLinkAncestors(string directory)
    {
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("本地 E5 受控目录及祖先不得使用链接或联接。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private void Stop()
    {
        Process? process = _process; _process = null;
        Task? errorReader = _errorReader; _errorReader = null;
        KillJob? ownedJob = _job; _job = null;
        _ready = false;
        if (process is null) { ownedJob?.Dispose(); return; }
        try
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
            ownedJob?.Dispose();
            try { process.WaitForExit(1000); } catch (InvalidOperationException) { }
            if (errorReader is not null) errorReader.Wait(1000);
        }
        finally
        {
            // The obtained redirected streams outlive Process.Dispose unless
            // explicitly closed after the child and stderr reader reach EOF.
            try { process.StandardInput.Dispose(); } catch (IOException) { } catch (InvalidOperationException) { }
            try { process.StandardOutput.Dispose(); } catch (IOException) { } catch (InvalidOperationException) { }
            try { process.StandardError.Dispose(); } catch (IOException) { } catch (InvalidOperationException) { }
            process.Dispose();
            ownedJob?.Dispose();
        }
    }

    private static string? ResolvePython(string root)
    {
        string packaged = Path.Combine(root, "runtime", "voice", "python", "python.exe");
        if (File.Exists(packaged)) return packaged;
        string development = Path.Combine(root, "GPT-SoVITS-v2pro-20261006", "runtime", "python.exe");
        return File.Exists(development) ? development : null;
    }

    public void Dispose()
    {
        lock (SharedGate)
        {
            if (--_references > 0) return;
            Instances.Remove(_root);
            lock (_gate) Stop();
        }
    }
}
