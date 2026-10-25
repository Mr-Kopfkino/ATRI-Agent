using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Atri.Desktop;
public sealed class AudioPlayer:IDisposable
{
    private sealed class Playback(double[] envelope)
    {
        internal readonly MediaPlayer Player=new();
        internal readonly TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly double[] Envelope=envelope;
        internal readonly double Threshold=Math.Max(.005,envelope.DefaultIfEmpty(.04).Max()*.20);
        internal EventHandler? Ended;
        internal EventHandler<ExceptionEventArgs>? Failed;
        private bool closed;
        internal void Close()
        {
            if(closed)return;closed=true;
            Player.MediaEnded-=Ended;Player.MediaFailed-=Failed;
            Player.Stop();Player.Close();
        }
    }
    private Playback? active;private readonly DispatcherTimer mouthTimer=new(){Interval=TimeSpan.FromMilliseconds(60)};private bool mouthOpen;
    public event Action<bool>? MouthChanged;
    public int CompletedCount {get;private set;}public int MouthTransitions {get;private set;}
    public AudioPlayer(){mouthTimer.Tick+=(_,_)=>{if(active is not {} current)return;var index=(int)(current.Player.Position.TotalSeconds/.04);var open=index>=0&&index<current.Envelope.Length&&current.Envelope[index]>current.Threshold;if(open!=mouthOpen){mouthOpen=open;MouthTransitions++;MouthChanged?.Invoke(open);}};}
    public async Task PlayAsync(string path,double volume,CancellationToken token)
    {
        Stop();token.ThrowIfCancellationRequested();var envelope=Envelope(path,out var duration);var current=new Playback(envelope);active=current;
        // Each callback owns exactly one player and completion. A queued event
        // from a stopped player must never finish a replacement playback.
        current.Ended=(_,_)=>{if(ReferenceEquals(active,current)&&current.Completion.TrySetResult())CompletedCount++;};
        current.Failed=(_,_)=>{if(ReferenceEquals(active,current))current.Completion.TrySetException(new IOException("音频设备无法播放合成语音。"));};
        current.Player.MediaEnded+=current.Ended;current.Player.MediaFailed+=current.Failed;
        using var registration=token.Register(()=>current.Completion.TrySetCanceled(token));
        try
        {
            token.ThrowIfCancellationRequested();current.Player.Volume=volume;current.Player.Open(new Uri(path));current.Player.Play();mouthTimer.Start();
            await current.Completion.Task.WaitAsync(PlaybackTimeout(duration),token);
        }
        finally
        {
            // An older PlayAsync can resume its finally after a new one starts.
            // It closes only its own player and cannot reset the new mouth timer.
            if(ReferenceEquals(active,current)){active=null;mouthTimer.Stop();MouthChanged?.Invoke(false);mouthOpen=false;}
            current.Close();
        }
    }
    public void Stop(){var current=active;active=null;mouthTimer.Stop();MouthChanged?.Invoke(false);mouthOpen=false;if(current is not null){current.Completion.TrySetCanceled();current.Close();}}
    public void Dispose(){Stop();}
    private static TimeSpan PlaybackTimeout(TimeSpan duration)=>TimeSpan.FromSeconds(Math.Clamp(duration.TotalSeconds*1.15+30,30,30*60));
    private static double[] Envelope(string path,out TimeSpan duration)
    {
        // Join() emits canonical PCM16 RIFF. This small RMS timeline follows
        // actual playback position, rather than a timer that toggles blindly.
        var data=File.ReadAllBytes(path);using var stream=new MemoryStream(data,false);using var reader=new BinaryReader(stream);
        if(data.Length<44||Encoding.ASCII.GetString(reader.ReadBytes(4))!="RIFF")throw new InvalidDataException("合成输出非 WAV。");
        reader.ReadInt32();if(Encoding.ASCII.GetString(reader.ReadBytes(4))!="WAVE")throw new InvalidDataException("WAV 标头错误。");
        byte[]? format=null;int sampleStart=0,sampleLength=0;
        while(stream.Position+8<=stream.Length)
        {
            var id=Encoding.ASCII.GetString(reader.ReadBytes(4));var size=reader.ReadInt32();if(size<0||size>stream.Length-stream.Position)throw new InvalidDataException("WAV 块长度无效。");
            if(id=="fmt ")format=reader.ReadBytes(size);else if(id=="data"){sampleStart=(int)stream.Position;sampleLength=size;stream.Position+=size;}else stream.Position+=size;
            if((size&1)==1&&stream.Position<stream.Length)stream.Position++;
        }
        if(format is null||format.Length<16||BitConverter.ToUInt16(format,0)!=1||BitConverter.ToUInt16(format,14)!=16)throw new InvalidDataException("仅支持 PCM16 合成输出。");
        var channels=BitConverter.ToUInt16(format,2);var rate=BitConverter.ToInt32(format,4);var alignment=channels*2;
        if(channels==0||rate<=0||BitConverter.ToUInt16(format,12)!=alignment||sampleLength==0||sampleLength%alignment!=0)throw new InvalidDataException("WAV 采样参数无效。");
        duration=TimeSpan.FromSeconds((double)sampleLength/rate/alignment);var block=(int)Math.Min(int.MaxValue,Math.Max(alignment,(long)(rate*.04)*alignment));var frames=new List<double>();
        for(int start=sampleStart;start<sampleStart+sampleLength;start+=(int)Math.Min(block,sampleStart+sampleLength-start)){double energy=0;int count=0;for(int p=start;p+1<Math.Min((long)sampleStart+sampleLength,(long)start+block);p+=2){double sample=BitConverter.ToInt16(data,p)/32768d;energy+=sample*sample;count++;}frames.Add(Math.Sqrt(energy/Math.Max(1,count)));}return frames.ToArray();
    }
    public static string Join(IReadOnlyList<string> paths,string output)
    {
        if(paths.Count==0)throw new InvalidDataException("推理未产生音频。");using var samples=new MemoryStream();byte[]? format=null;
        foreach(var path in paths)
        {using var f=File.OpenRead(path);using var r=new BinaryReader(f);if(Encoding.ASCII.GetString(r.ReadBytes(4))!="RIFF")throw new InvalidDataException("合成输出非 WAV。");r.ReadInt32();if(Encoding.ASCII.GetString(r.ReadBytes(4))!="WAVE")throw new InvalidDataException("WAV 标头错误。");byte[]? pcm=null;byte[]? fmt=null;
        while(f.Position+8<=f.Length){var id=Encoding.ASCII.GetString(r.ReadBytes(4));int size=r.ReadInt32();if(size<0||size>f.Length-f.Position)throw new InvalidDataException("WAV 块长度无效。");var b=r.ReadBytes(size);if(id=="fmt ")fmt=b;if(id=="data")pcm=b;if((size&1)==1&&f.Position<f.Length)f.Position++;}
        if(fmt==null||fmt.Length<16||pcm==null||BitConverter.ToUInt16(fmt,0)!=1||BitConverter.ToUInt16(fmt,14)!=16)throw new InvalidDataException("仅支持 PCM16 合成输出。");format??=fmt;if(!fmt.AsSpan(0,16).SequenceEqual(format.AsSpan(0,16)))throw new InvalidDataException("片段音频参数不一致。");if(samples.Length+pcm.Length>50_000_000)throw new InvalidDataException("音频超出容量上限。");samples.Write(pcm);}
        using var target=File.Create(output);using var w=new BinaryWriter(target);w.Write(Encoding.ASCII.GetBytes("RIFF"));w.Write((int)samples.Length+36);w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));w.Write(16);w.Write(format!,0,16);w.Write(Encoding.ASCII.GetBytes("data"));w.Write((int)samples.Length);samples.Position=0;samples.CopyTo(target);return output;
    }
}
