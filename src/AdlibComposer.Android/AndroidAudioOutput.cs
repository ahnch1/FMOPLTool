using System;
using System.Threading;
using AdlibComposer.Audio;
using Android.Media;

namespace AdlibComposer.Droid;

/// <summary>
/// 안드로이드 출력 장치 (<see cref="IAudioOutput"/>): AudioTrack 스트리밍, 16bit PCM.
/// 오디오 스레드가 <see cref="AudioFill"/>로 한 블록을 채워 Write (막히는 쓰기)로 넘긴다 = Windows WaveOutPlayer와 같은 '당겨 채우기'.
/// <see cref="PlayedSamples"/> = 재생 머리 위치 (실제로 소리 난 프레임 수) → 엔진의 재생 커서가 들리는 소리를 따라간다.
/// </summary>
public sealed class AndroidAudioOutput : IAudioOutput
{
    private readonly int sampleRate, frames, channels;
    private readonly AudioFill fill;
    private AudioTrack track;
    private Thread thread;
    private volatile bool running;
    private long headBase;
    private uint lastHead;
    private long headWraps;

    public AndroidAudioOutput(int sampleRate, int frames, int channels, AudioFill fill)
    {
        this.sampleRate = sampleRate;
        this.frames = Math.Max(256, frames);
        this.channels = channels == 1 ? 1 : 2;
        this.fill = fill ?? throw new ArgumentNullException(nameof(fill));
    }

    public bool IsOpen { get { return track != null; } }

    public void Start()
    {
        if (track != null) return;
        var mask = channels == 1 ? ChannelOut.Mono : ChannelOut.Stereo;
        int minBytes = AudioTrack.GetMinBufferSize(sampleRate, mask, Encoding.Pcm16bit);
        // 장치 버퍼: 최소값의 2배 또는 블록 4개 중 큰 쪽 (짧을수록 지연이 적고, 길수록 끊김에 강하다)
        int bytes = Math.Max(minBytes * 2, frames * channels * 2 * 4);
        track = new AudioTrack.Builder()
            .SetAudioAttributes(new AudioAttributes.Builder().SetUsage(AudioUsageKind.Media).SetContentType(AudioContentType.Music).Build())
            .SetAudioFormat(new AudioFormat.Builder().SetSampleRate(sampleRate).SetEncoding(Encoding.Pcm16bit).SetChannelMask(mask).Build())
            .SetBufferSizeInBytes(bytes)
            .SetTransferMode(AudioTrackMode.Stream)
            .Build();
        headBase = 0; lastHead = 0; headWraps = 0;
        running = true;
        track.Play();
        thread = new Thread(Loop) { IsBackground = true, Name = "AudioTrack" };
        thread.Start();
    }

    private void Loop()
    {
        try { Android.OS.Process.SetThreadPriority(Android.OS.ThreadPriority.UrgentAudio); } catch (Exception) { }
        var t = track;
        if (t == null) return;
        var buf = new short[frames * channels];
        while (running)
        {
            try { fill(buf, frames); }
            catch (Exception) { Array.Clear(buf, 0, buf.Length); }
            int off = 0;
            while (running && off < buf.Length)
            {
                int n;
                try { n = t.Write(buf, off, buf.Length - off); } catch (Exception) { return; }
                if (n <= 0) { Thread.Sleep(5); if (n < 0) break; }
                else off += n;
            }
        }
    }

    /// <summary>재생 머리 위치 (프레임). AudioTrack은 32bit로 돌아 넘어가므로 넘어간 횟수를 센다.</summary>
    public long PlayedSamples
    {
        get
        {
            var t = track;
            if (t == null) return -1;
            uint h;
            try { h = unchecked((uint)t.PlaybackHeadPosition); } catch (Exception) { return -1; }
            lock (this)
            {
                if (h < lastHead && lastHead - h > 0x80000000u) headWraps++;
                lastHead = h;
                return headBase + (headWraps << 32) + h;
            }
        }
    }

    public void Stop()
    {
        running = false;
        var t = track;
        track = null;
        if (t == null) return;
        // Stop이 막힌 Write를 깨운다 → 스레드가 끝난 뒤에 놓는다
        try { t.Stop(); } catch (Exception) { }
        try { thread?.Join(500); } catch (Exception) { }
        try { t.Flush(); } catch (Exception) { }
        t.Release();
        t.Dispose();
        thread = null;
    }

    public void Dispose() { Stop(); }
}

/// <summary>앱이 시작할 때: 엔진의 출력 장치를 AudioTrack으로 (MIDI 장치 · MP3 디코더는 없음).</summary>
public static class AndroidAudio
{
    public static void Register()
    {
        AudioPlatform.OutputFactory = (rate, frames, channels, fill) => new AndroidAudioOutput(rate, frames, channels, fill);
    }
}
