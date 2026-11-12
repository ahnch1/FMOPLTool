using System;
using AdlibComposer.Core;

namespace AdlibComposer.Audio;

/// <summary>
/// 오디오 엔진: OplSynth + Sequencer + 출력 장치. UI 스레드와 오디오 스레드가 같은 락을 쓴다.
/// Render()만 단독으로 호출하면 장치 없이 오프라인 렌더링도 가능 (테스트용).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int OutputRate = 44100;

    private readonly object sync = new object();
    private readonly OplSynth synth;
    private readonly Sequencer sequencer;
    private WaveOutPlayer player;
    private double resamplePos;
    private double prev, cur;

    public AudioEngine() : this(new OplSynth()) { }

    public AudioEngine(OplSynth synth)
    {
        this.synth = synth;
        sequencer = new Sequencer(synth);
    }

    public OplMapping Mapping { get { return synth.Mapping; } }
    public double MasterVolume { get; set; } = 1.5;
    public bool IsDeviceOpen { get { return player != null && player.IsOpen; } }
    public bool IsPlaying { get { lock (sync) { return sequencer.IsPlaying; } } }
    public double PositionTick { get { lock (sync) { return sequencer.PositionTick; } } }

    /// <summary>출력 장치를 연다 (Windows 전용). 실패하면 예외.</summary>
    public void OpenDevice()
    {
        if (player != null) return;
        player = new WaveOutPlayer(OutputRate, 2048, Render);
        player.Start();
    }

    // ---------------------------------------------------------------- 곡 재생

    public void Play(Song song, InstrumentBank bank, int startTick = 0, int loopStart = -1, int loopEnd = -1)
    {
        lock (sync)
        {
            Func<string, AdlibInstrument> resolver = name => bank != null ? bank.Find(name) : null;
            sequencer.Start(song, resolver, startTick, loopStart, loopEnd);
        }
    }

    public void Stop()
    {
        lock (sync) { sequencer.Stop(); }
    }

    // ---------------------------------------------------------------- 시음 (Instrument Maker / 건반 클릭)

    public void PreviewSetMode(bool rhythm) { lock (sync) { synth.SetRhythmMode(rhythm); } }
    public void PreviewSetInstrument(int voice, AdlibInstrument ins) { lock (sync) { synth.SetInstrument(voice, ins); } }
    public void PreviewNoteOn(int voice, int note) { lock (sync) { if (!sequencer.IsPlaying) synth.NoteOn(voice, note); } }
    public void PreviewNoteOff(int voice) { lock (sync) { if (!sequencer.IsPlaying) synth.NoteOff(voice); } }
    public void PreviewAllOff() { lock (sync) { if (!sequencer.IsPlaying) synth.AllNotesOff(); } }

    // ---------------------------------------------------------------- 렌더

    /// <summary>44.1kHz 모노 16bit 샘플을 count개 만든다.</summary>
    public void Render(short[] buffer, int count)
    {
        lock (sync)
        {
            double step = (double)synth.NativeRate / OutputRate;
            double gain = MasterVolume;
            for (int i = 0; i < count; i++)
            {
                resamplePos += step;
                while (resamplePos >= 1.0)
                {
                    resamplePos -= 1.0;
                    prev = cur;
                    sequencer.Step();
                    cur = synth.Generate();
                }
                double v = (prev + (cur - prev) * (resamplePos)) * gain;
                if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
                buffer[i] = (short)v;
            }
        }
    }

    public void Dispose()
    {
        if (player != null) { player.Dispose(); player = null; }
    }
}
