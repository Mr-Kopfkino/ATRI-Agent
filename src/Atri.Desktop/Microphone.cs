using System.Buffers.Binary;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

[assembly: InternalsVisibleTo("Atri.Tests")]

namespace Atri.Desktop;

/// <summary>Event-driven WinMM capture; a dedicated thread consumes native buffers in submission order.</summary>
public sealed class Microphone : IDisposable
{
    private const int BufferBytes = 3200;
    private const int BufferCount = 4;
    private const uint HeaderDone = 1;
    private static readonly uint HeaderSize = (uint)Marshal.SizeOf<Header>();
    private static readonly byte[] EmptyNativeBuffer = new byte[BufferBytes];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Caps
    {
        public ushort Mid, Pid;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels, Reserved;
    }
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Format
    {
        public ushort Tag, Channels;
        public uint Rate, Bytes;
        public ushort Align, Bits, Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct Header
    {
        public IntPtr Data;
        public uint Length, Recorded;
        public UIntPtr User;
        public uint Flags, Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }
    private sealed class NativeBuffer
    {
        public IntPtr Header, Data;
        public bool Prepared;
    }

    [DllImport("winmm.dll")] private static extern uint waveInGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] private static extern uint waveInGetDevCapsW(UIntPtr id, out Caps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInOpen(out IntPtr handle, uint device, ref Format format, IntPtr eventHandle, UIntPtr user, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveInPrepareHeader(IntPtr h, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInUnprepareHeader(IntPtr h, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInAddBuffer(IntPtr h, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveInStart(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint waveInStop(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint waveInReset(IntPtr h);
    [DllImport("winmm.dll")] private static extern uint waveInClose(IntPtr h);

    private IntPtr handle;
    // Allocation ownership and native submission order are deliberately separate.
    private readonly List<NativeBuffer> buffers = [];
    private WaveInBufferQueue<NativeBuffer>? queuedHeaders;
    private readonly object gate = new(), lifecycle = new();
    // Preallocate once so growth never abandons an uncleared copy of captured PCM.
    private readonly MemoryStream pcm = new(PcmCaptureMemory.MaximumBytes);
    private volatile bool recording;
    private bool disposed;
    private long generation;
    private AutoResetEvent? available;
    private Thread? collector;

    public bool Recording => recording;
    public long Generation => Interlocked.Read(ref generation);
    /// <summary>The recipient owns each PCM array and must clear it after consumption.</summary>
    public event Action<byte[], long>? FrameReady;
    public event Action<string, long>? Failed;

    public static IReadOnlyList<string> Devices()
    {
        var result = new List<string>();
        for (uint i = 0; i < waveInGetNumDevs(); i++)
            result.Add(waveInGetDevCapsW((UIntPtr)i, out var c, (uint)Marshal.SizeOf<Caps>()) == 0 ? c.Name : $"麦克风 {i}");
        return result;
    }

    public void Start(int device)
    {
        lock (lifecycle)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (recording) throw new InvalidOperationException("已经在录音。");
            if (device < 0 || device >= waveInGetNumDevs()) throw new InvalidOperationException("没有可用麦克风，请检查设备和系统麦克风权限。");
            if (handle != IntPtr.Zero) StopInternal();
            ClearBuffer();
            var format = new Format { Tag = 1, Channels = 1, Rate = 16000, Bytes = 32000, Align = 2, Bits = 16 };
            available = new AutoResetEvent(false);
            queuedHeaders = new(BufferCount);
            var cookie = Interlocked.Increment(ref generation);
            try
            {
                lock (gate)
                {
                    // CALLBACK_EVENT: no native waveInProc callback enters the CLR.
                    Check(waveInOpen(out var opened, (uint)device, ref format, available.SafeWaitHandle.DangerousGetHandle(), (UIntPtr)(ulong)cookie, 0x50000));
                    handle = opened;
                    for (int i = 0; i < BufferCount; i++)
                    {
                        // Track each allocation before the next operation can fail.
                        var buffer = new NativeBuffer();
                        buffers.Add(buffer);
                        buffer.Data = Marshal.AllocHGlobal(BufferBytes);
                        buffer.Header = Marshal.AllocHGlobal((int)HeaderSize);
                        Marshal.Copy(EmptyNativeBuffer, 0, buffer.Data, BufferBytes);
                        Marshal.StructureToPtr(new Header { Data = buffer.Data, Length = BufferBytes }, buffer.Header, false);
                        Check(waveInPrepareHeader(handle, buffer.Header, HeaderSize));
                        buffer.Prepared = true;
                        QueueBuffer(handle, buffer);
                    }
                    recording = true;
                    var signal = available;
                    collector = new Thread(() => Collect(opened, cookie, signal)) { IsBackground = true, Name = "ATRI microphone capture" };
                    collector.Start();
                    Check(waveInStart(handle));
                }
            }
            catch
            {
                StopInternal();
                throw;
            }
        }
    }

    // The caller owns gate. Add, Stop and Reset must never overlap.
    private void QueueBuffer(IntPtr opened, NativeBuffer buffer)
    {
        queuedHeaders!.EnsureCanEnqueue(buffer);
        Check(waveInAddBuffer(opened, buffer.Header, HeaderSize));
        queuedHeaders.Enqueue(buffer);
    }
    private static bool IsDone(NativeBuffer buffer) => (Marshal.PtrToStructure<Header>(buffer.Header).Flags & HeaderDone) != 0;

    // The caller owns gate. Removing from the FIFO makes every returned header single-consumption.
    private byte[]? ConsumeBuffer(NativeBuffer buffer, bool forwardFrame)
    {
        var header = Marshal.PtrToStructure<Header>(buffer.Header);
        if (header.Data != buffer.Data || header.Length != BufferBytes || header.Recorded > BufferBytes || (header.Recorded & 1) != 0)
            throw new IOException("录音缓冲区长度无效。");
        if (header.Recorded == 0) return null;
        var data = new byte[header.Recorded];
        try
        {
            Marshal.Copy(buffer.Data, data, 0, data.Length);
            PcmCaptureMemory.Append(pcm, data);
            if (forwardFrame) return data;
        }
        catch
        {
            Array.Clear(data);
            throw;
        }
        Array.Clear(data);
        return null;
    }

    private void Collect(IntPtr opened, long cookie, AutoResetEvent signal)
    {
        try
        {
            while (true)
            {
                signal.WaitOne(250);
                while (true)
                {
                    byte[]? frame = null;
                    try
                    {
                        lock (gate)
                        {
                            if (opened != handle || cookie != generation) return;
                            if (!queuedHeaders!.TryTakeCompleted(IsDone, out var buffer))
                            {
                                if (queuedHeaders.IsDrained) return;
                                break;
                            }
                            frame = ConsumeBuffer(buffer, recording);
                            if (recording) QueueBuffer(opened, buffer);
                        }
                        if (frame != null && FrameReady is { } sink)
                        {
                            sink(frame, cookie);
                            frame = null; // Ownership transferred to the recipient.
                        }
                    }
                    finally
                    {
                        if (frame != null) Array.Clear(frame);
                    }
                }
            }
        }
        catch
        {
            lock (gate) recording = false;
            try { Failed?.Invoke("麦克风设备或采集线程失败，已停止收音。", cookie); } catch { }
        }
    }

    public byte[] Stop()
    {
        lock (lifecycle)
        {
            StopInternal();
            byte[] bytes;
            lock (gate) bytes = pcm.ToArray();
            try
            {
                if (bytes.Length < BufferBytes) throw new InvalidDataException("录音不足 0.1 秒。");
                double energy = 0;
                for (int i = 0; i + 1 < bytes.Length; i += 2)
                {
                    var v = BitConverter.ToInt16(bytes, i);
                    energy += (double)v * v;
                }
                if (Math.Sqrt(energy / (bytes.Length / 2)) < 25) throw new InvalidDataException("没有检测到有效声音。");
                return Wav(bytes);
            }
            finally
            {
                Array.Clear(bytes);
                ClearBuffer();
            }
        }
    }

    public void DiscardBuffer() => ClearBuffer();
    private void ClearBuffer() { lock (gate) PcmCaptureMemory.Clear(pcm); }
    public void Cancel() { lock (lifecycle) { try { StopInternal(); } finally { ClearBuffer(); } } }

    private void StopInternal()
    {
        // Event subscribers must hand off work; joining this thread from its own event would deadlock.
        if (Thread.CurrentThread == collector) throw new InvalidOperationException("请在麦克风采集事件以外的线程停止录音。");
        IntPtr opened;
        lock (gate)
        {
            recording = false;
            opened = handle;
            if (opened == IntPtr.Zero)
            {
                available?.Dispose();
                available = null;
                queuedHeaders = null;
                return;
            }
            queuedHeaders!.BeginDrain();
            _ = waveInStop(opened);
            // Reset returns all submitted buffers, including the final partially filled buffer.
            // A failure retains all native resources for a later stop attempt.
            Check(waveInReset(opened));
            available?.Set();
        }
        // Never release a header while the collector may dereference it. Do not hold gate while joining.
        if (collector is { IsAlive: true } && !collector.Join(1500))
            throw new IOException("麦克风采集线程正在停止，句柄保留以便重试。");
        collector = null;
        lock (gate)
        {
            // Also covers a failed Start or a collector that exited after a device/consumer error.
            while (queuedHeaders!.TryTakeCompleted(IsDone, out var buffer)) ConsumeBuffer(buffer, false);
            queuedHeaders.EnsureDrained();
            Interlocked.Increment(ref generation);
            foreach (var buffer in buffers.ToArray())
            {
                uint code = 0;
                if (buffer.Prepared)
                {
                    for (int i = 0; i < 30; i++)
                    {
                        code = waveInUnprepareHeader(opened, buffer.Header, HeaderSize);
                        if (code != 33) break;
                        Thread.Sleep(5);
                    }
                }
                if (code != 0) continue;
                if (buffer.Header != IntPtr.Zero) Marshal.FreeHGlobal(buffer.Header);
                if (buffer.Data != IntPtr.Zero)
                {
                    Marshal.Copy(EmptyNativeBuffer, 0, buffer.Data, BufferBytes);
                    Marshal.FreeHGlobal(buffer.Data);
                }
                buffers.Remove(buffer);
            }
            if (buffers.Count > 0) throw new IOException("麦克风缓冲区仍在释放，请重试停止。");
            Check(waveInClose(opened));
            handle = IntPtr.Zero;
            available?.Dispose();
            available = null;
            queuedHeaders = null;
        }
    }

    public static byte[] Wav(byte[] bytes)
    {
        // Write directly into the returned array: no extra uncleared MemoryStream copy.
        var wav = new byte[checked(bytes.Length + 44)];
        "RIFF"u8.CopyTo(wav);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), bytes.Length + 36);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), bytes.Length);
        bytes.CopyTo(wav, 44);
        return wav;
    }
    private static void Check(uint code)
    {
        if (code != 0) throw new IOException($"麦克风操作失败（WinMM {code}），请检查设备、格式和权限。");
    }
    public void Dispose()
    {
        lock (lifecycle)
        {
            if (disposed) return;
            Cancel();
            disposed = true;
            pcm.Dispose();
        }
    }
}

/// <summary>Submission-order ownership shared by collector and stop; callers serialize access.</summary>
internal sealed class WaveInBufferQueue<T>(int capacity) where T : notnull
{
    private readonly Queue<T> pending = new();
    private readonly HashSet<T> submitted = new();
    internal int Count => pending.Count;
    internal bool IsDraining { get; private set; }
    internal bool IsDrained => IsDraining && pending.Count == 0;
    internal void EnsureCanEnqueue(T buffer)
    {
        if (IsDraining) throw new InvalidOperationException("录音已停止，不能重新提交缓冲区。");
        if (pending.Count >= capacity || submitted.Contains(buffer)) throw new InvalidOperationException("录音缓冲区重复提交或超出容量。");
    }
    internal void Enqueue(T buffer)
    {
        EnsureCanEnqueue(buffer);
        pending.Enqueue(buffer);
        submitted.Add(buffer);
    }
    internal bool TryTakeCompleted(Func<T, bool> isDone, out T buffer)
    {
        if (pending.TryPeek(out var head) && isDone(head))
        {
            buffer = pending.Dequeue();
            submitted.Remove(buffer);
            return true;
        }
        buffer = default!;
        return false;
    }
    internal void BeginDrain() => IsDraining = true;
    internal void EnsureDrained()
    {
        if (!IsDrained) throw new IOException("麦克风缓冲区尚未全部返回，句柄保留以便重试。");
    }
}

internal static class PcmCaptureMemory
{
    internal const int MaximumBytes = 30 * 32000;
    internal static void Append(MemoryStream pcm, ReadOnlySpan<byte> bytes, int maximumBytes = MaximumBytes)
    {
        if ((bytes.Length & 1) != 0 || maximumBytes < 0 || (maximumBytes & 1) != 0) throw new InvalidDataException("PCM16 长度无效。");
        var remaining = maximumBytes - pcm.Length;
        if (remaining > 0) pcm.Write(bytes[..(int)Math.Min(remaining, bytes.Length)]);
    }
    internal static void Clear(MemoryStream pcm)
    {
        // Clear the allocation before truncation, including capacity beyond the current length.
        if (pcm.TryGetBuffer(out var data)) Array.Clear(data.Array!);
        pcm.SetLength(0);
    }
}
