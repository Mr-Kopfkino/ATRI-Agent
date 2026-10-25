using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Threading;
using Atri.Core;

namespace Atri.Desktop;

public sealed record GameAudioSnapshot(string Page, string? Bgm, bool Enabled, double RequestedVolume,
    double ActualVolume, bool Recording, bool Speaking, bool Stopped, int OpenPlayers,
    int BgmOpened, int BgmLoops, int EffectsStarted, int EffectsCompleted, int PromptsStarted,
    int PromptsCompleted, int Failures, double Position, double Duration, string DesiredBgm, int ChatSelections);

/// <summary>Three bounded WPF channels for audited original BGM, interface SE and contextual cues.</summary>
public sealed class GameAudio : IDisposable
{
    private sealed record Clip(string Path, string Kind, double Duration);
    private readonly Dictionary<string, Clip> clips = new(StringComparer.Ordinal);
    private readonly MediaPlayer bgm = new(), effect = new(), prompt = new();
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer fade, promptDelay;
    private string page = "", desiredBgm = "title";
    private string[] chatBgm = ["chat"];
    private string? currentBgm, pendingPrompt;
    private bool enabled = true, recording, speaking, stopped, disposed, bgmOpen, effectOpen, promptOpen;
    private double volume = .25, effectsVolume = .65, promptVolume = .8, fadeFrom, fadeTarget;
    private long fadeStart, lastHover;
    private int bgmOpened, loops, effectsStarted, effectsCompleted, promptsStarted, promptsCompleted, failures, chatSelections;
    public string Status { get; private set; } = "游戏音频就绪";

    public GameAudio(AppPaths paths)
    {
        fade = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(30) };
        promptDelay = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(140) };
        fade.Tick += FadeTick; promptDelay.Tick += PromptTick;
        bgm.MediaOpened += BgmOpened; bgm.MediaEnded += BgmEnded; bgm.MediaFailed += BgmFailed;
        effect.MediaEnded += EffectEnded; effect.MediaFailed += EffectFailed;
        prompt.MediaEnded += PromptEnded; prompt.MediaFailed += PromptFailed;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(paths.WithinRoot("assets/derived/game-audio.json")));
            if (document.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("游戏音频目录版本不支持。");
            foreach (var entry in document.RootElement.GetProperty("clips").EnumerateObject())
            {
                var value = entry.Value;
                string relative = value.GetProperty("cache").GetString()!;
                if (!relative.StartsWith("data/cache/game-audio/", StringComparison.Ordinal) || !relative.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("游戏音频未位于受控 WAV 缓存。");
                var file = paths.WithinRoot(relative);
                using var stream = File.OpenRead(file);
                if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(value.GetProperty("cacheSha256").GetString(), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("游戏音频缓存校验失败。");
                clips.Add(entry.Name, new(file, value.GetProperty("kind").GetString()!, value.GetProperty("duration").GetDouble()));
            }
            if (document.RootElement.TryGetProperty("chatBgm", out var selection))
            {
                var pool = new List<string>();
                foreach (var entry in selection.EnumerateArray())
                {
                    var id = entry.GetString();
                    if (string.IsNullOrEmpty(id) || id == "title" || pool.Contains(id) || !clips.TryGetValue(id, out var clip) || clip.Kind != "bgm")
                        throw new InvalidDataException("聊天背景音乐曲库无效。");
                    pool.Add(id);
                }
                if (pool.Count == 0) throw new InvalidDataException("聊天背景音乐曲库为空。");
                chatBgm = pool.ToArray();
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or KeyNotFoundException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            clips.Clear(); Status = "游戏音频缓存不可用，请运行 prepare-game-audio.py。";
            paths.Log("game-audio", error.GetType().Name);
        }
    }

    private bool Active()
    {
        if (stopped || disposed) return false;
        dispatcher.VerifyAccess();
        return true;
    }

    public void Configure(bool bgmEnabled, double bgmVolume, double effectsVolume = .65, double promptVolume = .8)
    {
        if (!Active()) return;
        enabled = bgmEnabled; volume = Clamp(bgmVolume); this.effectsVolume = Clamp(effectsVolume); this.promptVolume = Clamp(promptVolume);
        effect.Volume = this.effectsVolume; prompt.Volume = this.promptVolume;
        if (!enabled) CloseBgm();
        else if (page.Length > 0 && !bgmOpen) SwitchBgm(desiredBgm);
        UpdateVolume(immediate: recording);
    }

    public void Navigate(string page, bool announce = true)
    {
        if (!Active() || page is not ("title" or "chat" or "system")) return;
        var enteringChat = page == "chat" && this.page != "chat";
        this.page = page;
        ClosePrompt();
        if (page == "title") desiredBgm = "title";
        else if (enteringChat)
        {
            desiredBgm = chatBgm[Random.Shared.Next(chatBgm.Length)];
            chatSelections++;
        }
        if (enabled) SwitchBgm(desiredBgm);
        if (announce && page == "system") Announce("settings");
    }

    public void Hover()
    {
        if (!Active() || recording || speaking) return;
        var now = Stopwatch.GetTimestamp();
        if (Stopwatch.GetElapsedTime(lastHover, now).TotalMilliseconds < 120) return;
        lastHover = now; PlayEffect("hover");
    }
    public void Select() { if (Active() && !recording && !speaking) PlayEffect("select"); }
    public void Back() { if (Active() && !recording && !speaking) { ClosePrompt(); PlayEffect("back"); } }

    public void Announce(string cue)
    {
        if (!Active() || recording || speaking || !clips.TryGetValue(cue, out var clip) || clip.Kind != "prompt") return;
        // Keep only the newest contextual cue when page and tab change in the same UI operation.
        pendingPrompt = cue; promptDelay.Stop(); promptDelay.Start();
    }

    public void DismissPrompt(){if(Active())ClosePrompt();}

    public void SetVoiceActivity(bool recording, bool speaking)
    {
        if (!Active()) return;
        this.recording = recording; this.speaking = speaking;
        if (recording || speaking) { ClosePrompt(); CloseEffect(); }
        // Capture must never wait for a fade: silence the BGM before the microphone is opened.
        UpdateVolume(immediate: recording);
    }

    private void SwitchBgm(string id)
    {
        if (!enabled || (bgmOpen && currentBgm == id) || !clips.TryGetValue(id, out var clip) || clip.Kind != "bgm") return;
        CloseBgm(); currentBgm = id;
        try { bgm.Volume = 0; bgm.Open(new Uri(clip.Path)); bgmOpen = true; bgm.Play(); UpdateVolume(immediate: recording); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException) { Failure("背景音乐无法播放。", ref bgmOpen, bgm); }
    }

    private void PlayEffect(string id)
    {
        if (!clips.TryGetValue(id, out var clip) || clip.Kind != "effect") return;
        CloseEffect();
        try { effect.Volume = effectsVolume; effect.Open(new Uri(clip.Path)); effectOpen = true; effectsStarted++; effect.Play(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException) { Failure("操作音效无法播放。", ref effectOpen, effect); }
    }

    private void PromptTick(object? sender, EventArgs args)
    {
        promptDelay.Stop(); var cue = pendingPrompt; pendingPrompt = null;
        if (stopped || disposed || recording || speaking || cue is null || !clips.TryGetValue(cue, out var clip)) return;
        prompt.Stop(); prompt.Close(); promptOpen = false;
        try { prompt.Volume = promptVolume; prompt.Open(new Uri(clip.Path)); promptOpen = true; promptsStarted++; prompt.Play(); }
        catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException) { Failure("系统提示无法播放。", ref promptOpen, prompt); }
    }

    private double TargetVolume => enabled && !recording ? volume * (speaking ? .14 : 1) : 0;
    private void UpdateVolume(bool immediate)
    {
        fade.Stop(); fadeTarget = TargetVolume;
        if (immediate || !bgmOpen) { bgm.Volume = fadeTarget; return; }
        fadeFrom = bgm.Volume; fadeStart = Stopwatch.GetTimestamp(); fade.Start();
    }
    private void FadeTick(object? sender, EventArgs args)
    {
        if (stopped || disposed) { fade.Stop(); return; }
        var progress = Math.Clamp(Stopwatch.GetElapsedTime(fadeStart).TotalMilliseconds / 240, 0, 1);
        bgm.Volume = fadeFrom + (fadeTarget - fadeFrom) * progress;
        if (progress >= 1) fade.Stop();
    }

    private void BgmOpened(object? sender, EventArgs args) { if (!stopped && bgmOpen) bgmOpened++; }
    private void BgmEnded(object? sender, EventArgs args)
    {
        if (stopped || disposed || !enabled || !bgmOpen) return;
        loops++; bgm.Position = TimeSpan.Zero; bgm.Play();
    }
    private void EffectEnded(object? sender, EventArgs args) { if (effectOpen) effectsCompleted++; CloseEffect(); }
    private void PromptEnded(object? sender, EventArgs args) { if (promptOpen) promptsCompleted++; ClosePrompt(discardPending: false); }
    private void BgmFailed(object? sender, ExceptionEventArgs args) => Failure("背景音乐无法播放。", ref bgmOpen, bgm);
    private void EffectFailed(object? sender, ExceptionEventArgs args) => Failure("操作音效无法播放。", ref effectOpen, effect);
    private void PromptFailed(object? sender, ExceptionEventArgs args) => Failure("系统提示无法播放。", ref promptOpen, prompt);
    private void Failure(string message, ref bool open, MediaPlayer channel) { failures++; Status = message; open = false; channel.Stop(); channel.Close(); }
    private void CloseBgm() { fade.Stop(); bgm.Volume = 0; bgm.Stop(); bgm.Close(); bgmOpen = false; currentBgm = null; }
    private void CloseEffect() { effect.Volume = 0; effect.Stop(); effect.Close(); effectOpen = false; }
    private void ClosePrompt(bool discardPending = true) { if (discardPending) { promptDelay.Stop(); pendingPrompt = null; } prompt.Volume = 0; prompt.Stop(); prompt.Close(); promptOpen = false; }
    private static double Clamp(double value) => double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;

    public GameAudioSnapshot Inspect()
    {
        dispatcher.VerifyAccess();
        return new(page, currentBgm, enabled, volume, bgm.Volume, recording, speaking, stopped,
            (bgmOpen ? 1 : 0) + (effectOpen ? 1 : 0) + (promptOpen ? 1 : 0), bgmOpened, loops,
            effectsStarted, effectsCompleted, promptsStarted, promptsCompleted, failures, bgm.Position.TotalSeconds,
            bgm.NaturalDuration.HasTimeSpan ? bgm.NaturalDuration.TimeSpan.TotalSeconds : 0, desiredBgm, chatSelections);
    }

    internal void SeekBgmForVerification(TimeSpan position) { if (Active() && bgmOpen) bgm.Position = position; }

    /// <summary>Release current playback while keeping this owner reusable.</summary>
    public void StopPlayback()
    {
        if(!Active())return;
        ClosePrompt();CloseEffect();CloseBgm();
    }

    /// <summary>Terminal stop; late UI continuations are harmless. Create a new manager to restart.</summary>
    public void StopAll()
    {
        if (stopped || disposed) return;
        dispatcher.VerifyAccess(); stopped = true;
        ClosePrompt(); CloseEffect(); CloseBgm();
    }
    public void Dispose()
    {
        if (disposed) return;
        StopAll(); disposed = true;
        fade.Tick -= FadeTick; promptDelay.Tick -= PromptTick;
        bgm.MediaOpened -= BgmOpened; bgm.MediaEnded -= BgmEnded; bgm.MediaFailed -= BgmFailed;
        effect.MediaEnded -= EffectEnded; effect.MediaFailed -= EffectFailed;
        prompt.MediaEnded -= PromptEnded; prompt.MediaFailed -= PromptFailed;
    }
}
