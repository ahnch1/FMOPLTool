using System;

namespace AdlibComposer.Audio;

/// <summary>출력 버퍼 채우기: 16bit 샘플 (스테레오면 왼쪽 · 오른쪽 번갈아), frames = 프레임 수.</summary>
public delegate void AudioFill(short[] buffer, int frames);

/// <summary>오디오 출력 장치 (Windows = waveOut, 안드로이드 = AudioTrack …).</summary>
public interface IAudioOutput : IDisposable
{
    bool IsOpen { get; }
    /// <summary>장치가 실제로 재생을 마친 프레임 수 (Start 이후 누적). 모르면 -1.</summary>
    long PlayedSamples { get; }
    void Start();
    void Stop();
}

/// <summary>바깥 MIDI 장치 (Windows = midiOut). 메시지는 오디오 스레드에서 보낸다.</summary>
public interface IMidiOutDevice : IDisposable
{
    /// <summary>장치 열기 (이미 열려 있으면 true). 실패하면 false.</summary>
    bool Open();
    bool IsOpen { get; }
    /// <summary>짧은 메시지 (status | d1 &lt;&lt; 8 | d2 &lt;&lt; 16).</summary>
    void ShortMessage(int message);
    /// <summary>SysEx (F0 … F7). 보냈으면 true.</summary>
    bool SysEx(byte[] data);
    /// <summary>장치가 아직 붙잡고 있는 SysEx 버퍼 수.</summary>
    int SysExPending { get; }
}

/// <summary>
/// 플랫폼별 구현을 끼우는 곳. 코어(칩 · 시퀀서 · 포맷 · 엔진)는 플랫폼 코드 없이 이것만 본다.
/// 앱이 시작할 때 채운다 (Windows: <c>AdlibComposer.Audio.Windows.WindowsAudio.Register()</c>).
/// 비어 있으면 장치 없이 동작한다 (오프라인 렌더 · 시험).
/// </summary>
public static class AudioPlatform
{
    /// <summary>(샘플레이트, 버퍼 프레임 수, 채널 수, 채우기) → 출력 장치.</summary>
    public static Func<int, int, int, AudioFill, IAudioOutput> OutputFactory { get; set; }

    /// <summary>바깥 MIDI 장치 만들기 (Windows MIDI 신디사이저).</summary>
    public static Func<IMidiOutDevice> MidiOutFactory { get; set; }

    /// <summary>WAV가 아닌 오디오 파일(MP3 …)을 모노 float로 푸는 것: (경로, out 샘플레이트) → 샘플.</summary>
    public delegate float[] Decoder(string path, out int sampleRate);
    public static Decoder CompressedDecoder { get; set; }

    public static IAudioOutput CreateOutput(int sampleRate, int frames, int channels, AudioFill fill)
    {
        var f = OutputFactory;
        if (f == null) throw new InvalidOperationException("오디오 출력 장치가 없습니다 (AudioPlatform.OutputFactory가 비어 있음).");
        return f(sampleRate, frames, channels, fill);
    }
}
