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

    // 재생 커서 동기화: 렌더한 블록마다 (출력 샘플 위치, 시작/끝 틱)을 기록해 두고,
    // 장치가 실제로 재생 중인 샘플 위치로 틱을 찾는다 (출력 버퍼만큼 앞서 가는 렌더 위치 대신).
    private struct Mark { public long Start; public int Count; public double T0, T1; public bool Playing; public int Gen; }
    private readonly Mark[] marks = new Mark[128];
    private int markNext;
    private long renderedSamples;
    private long deviceBase;
    private int generation;
    private double startTick;

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

    /// <summary>지금 스피커에서 들리는 위치(틱). 장치가 없으면 시퀀서 위치.</summary>
    public double PositionTick
    {
        get
        {
            long played = player != null ? player.PlayedSamples : -1;
            lock (sync)
            {
                if (played < 0) return sequencer.PositionTick;
                long at = deviceBase + played;
                for (int k = 1; k <= marks.Length; k++)
                {
                    var m = marks[(markNext - k + marks.Length) % marks.Length];
                    if (m.Count == 0) break;
                    if (at < m.Start) continue;
                    if (at >= m.Start + m.Count) return sequencer.PositionTick;   // 기록보다 앞: 거의 없음
                    if (m.Gen != generation || !m.Playing) return startTick;       // 아직 이전 버퍼(무음)가 나오는 중
                    if (m.T1 < m.T0) return m.T0;                                  // 루프 되감기 블록
                    return m.T0 + (m.T1 - m.T0) * (at - m.Start) / m.Count;
                }
                return startTick;
            }
        }
    }

    /// <summary>출력 장치를 연다 (Windows 전용). 실패하면 예외.</summary>
    public void OpenDevice()
    {
        if (player != null) return;
        lock (sync) deviceBase = renderedSamples;
        player = new WaveOutPlayer(OutputRate, 512, Render);   // 6 x 512 샘플 ≈ 70ms
        player.Start();
    }

    // ---------------------------------------------------------------- 곡 재생

    public void Play(Song song, InstrumentBank bank, int startTick = 0, int loopStart = -1, int loopEnd = -1, int onlyVoice = -1, PlaybackOptions options = null)
    {
        lock (sync)
        {
            Func<string, AdlibInstrument> resolver = name => bank != null ? bank.Find(name) : null;
            sequencer.Start(song, resolver, startTick, loopStart, loopEnd, onlyVoice, options);
            generation++;
            this.startTick = Math.Max(0, startTick);
        }
    }

    public void Stop()
    {
        lock (sync) { sequencer.Stop(); }
    }

    // ---------------------------------------------------------------- 재생 조절 (Jukebox)

    /// <summary>지금 재생의 조절값 (Play에 넘긴 것). 값을 바꾼 뒤에는 아래 Set...을 부른다.</summary>
    public PlaybackOptions Options { get { lock (sync) { return sequencer.Options; } } }

    public void SetSpeed(double speed) { lock (sync) { sequencer.Options.Speed = speed; sequencer.ApplySpeed(); } }
    public void SetTranspose(int semitones) { lock (sync) { sequencer.Options.Transpose = semitones; } }
    public void SetVoiceTranspose(int voice, int semitones) { lock (sync) { sequencer.Options.VoiceTranspose[voice] = semitones; } }
    public void SetVoiceMute(int voice, bool mute) { lock (sync) { sequencer.Options.VoiceMute[voice] = mute; sequencer.ApplyMute(voice); } }
    public void SetGain(double gain) { lock (sync) { sequencer.Options.Gain = gain; } }

    /// <summary>지금 템포 배수 (곡의 템포 이벤트).</summary>
    public double TempoMultiplier { get { lock (sync) { return sequencer.TempoMultiplier; } } }

    /// <summary>보이스별 상태 (울리는 음, 볼륨, 악기). dst 길이는 11.</summary>
    public void GetVoiceStates(VoiceState[] dst) { lock (sync) { sequencer.CopyStates(dst); } }

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
            var mark = new Mark { Start = renderedSamples, Count = count, T0 = sequencer.PositionTick, Playing = sequencer.IsPlaying, Gen = generation };
            RenderLocked(buffer, count);
            mark.T1 = sequencer.PositionTick;
            renderedSamples += count;
            marks[markNext] = mark;
            markNext = (markNext + 1) % marks.Length;
        }
    }

    private void RenderLocked(short[] buffer, int count)
    {
        {
            double step = (double)synth.NativeRate / OutputRate;
            double gain = MasterVolume * sequencer.Options.Gain;
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
                v = SoftClip(v);
                buffer[i] = (short)v;
            }
        }
    }

    private const double Knee = 24000, Headroom = 32767 - Knee;

    /// <summary>
    /// 부드러운 제한: 24000까지는 그대로, 그 위는 32767에 점점 가까워지게 눌러 딱딱한 클리핑(찌그러짐)을 막는다.
    /// 리듬 모드 11채널이 한꺼번에 크게 울리는 곡(예: JAM-FIVE.IMS)에서 0.05% 정도 생기던 클리핑을 없앤다.
    /// </summary>
    public static double SoftClip(double v)
    {
        double a = Math.Abs(v);
        if (a <= Knee) return v;
        double over = a - Knee;
        double c = Knee + over / (1 + over / Headroom);
        return v < 0 ? -c : c;
    }

    public void Dispose()
    {
        if (player != null) { player.Dispose(); player = null; }
    }
}
