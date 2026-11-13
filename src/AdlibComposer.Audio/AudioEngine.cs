using System;
using System.Collections.Generic;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// 오디오 엔진: OplSynth + Sequencer (ROL/IMS, OPL2), SopPlayer (SOP, OPL3), MusicBoxPlayer (오르골 시험 연주) 중 하나 + 출력 장치(스테레오).
/// UI 스레드와 오디오 스레드가 같은 락을 쓴다.
/// Render()만 단독으로 호출하면 장치 없이 오프라인 렌더링도 가능 (테스트용).
/// </summary>
public sealed class AudioEngine : IDisposable
{
    public const int OutputRate = 44100;

    private readonly object sync = new object();
    private readonly OplSynth synth;
    private readonly Sequencer sequencer;
    /// <summary>SOP는 최대 20트랙이 함께 울려 OPL2 곡보다 크다: 칩 출력을 줄여 SoftClip에 덜 걸리게 한다.</summary>
    private readonly Opl3Emulator opl3 = new Opl3Emulator { MasterGain = 0.6 };
    private readonly SopPlayer sop;
    private readonly MusicBoxPlayer musicBox = new MusicBoxPlayer();
    /// <summary>소리를 내는 쪽. 시음이나 ROL/IMS 재생을 시작하면 Opl2.</summary>
    private enum Source { Opl2, Sop, MusicBox }
    private Source source = Source.Opl2;
    private bool sopMode { get { return source == Source.Sop; } }
    /// <summary>오르골 소리(−1~1)를 16bit 쪽 크기로.</summary>
    private const double MusicBoxScale = 7000;
    private WaveOutPlayer player;
    private double resamplePos;
    private double prevL, curL, prevR, curR;

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
        sop = new SopPlayer(opl3);
    }

    public OplMapping Mapping { get { return synth.Mapping; } }
    public double MasterVolume { get; set; } = 1.5;
    public bool IsDeviceOpen { get { return player != null && player.IsOpen; } }
    public bool IsPlaying { get { lock (sync) { return SourcePlaying; } } }

    private bool SourcePlaying
    {
        get { return source == Source.Sop ? sop.IsPlaying : source == Source.MusicBox ? musicBox.IsPlaying : sequencer.IsPlaying; }
    }

    private double SourceTick
    {
        get { return source == Source.Sop ? sop.PositionTick : source == Source.MusicBox ? musicBox.PositionTick : sequencer.PositionTick; }
    }

    /// <summary>지금 스피커에서 들리는 위치(틱). 장치가 없으면 시퀀서 위치.</summary>
    public double PositionTick
    {
        get
        {
            long played = player != null ? player.PlayedSamples : -1;
            lock (sync)
            {
                if (played < 0) return SourceTick;
                long at = deviceBase + played;
                for (int k = 1; k <= marks.Length; k++)
                {
                    var m = marks[(markNext - k + marks.Length) % marks.Length];
                    if (m.Count == 0) break;
                    if (at < m.Start) continue;
                    if (at >= m.Start + m.Count) return SourceTick;                // 기록보다 앞: 거의 없음
                    if (m.Gen != generation || !m.Playing) return startTick;       // 아직 이전 버퍼(무음)가 나오는 중
                    if (m.T1 < m.T0) return m.T0;                                  // 루프 되감기 블록
                    return m.T0 + (m.T1 - m.T0) * (at - m.Start) / m.Count;
                }
                return startTick;
            }
        }
    }

    /// <summary>출력 장치를 연다 (Windows 전용, 스테레오). 실패하면 예외.</summary>
    public void OpenDevice()
    {
        if (player != null) return;
        lock (sync) deviceBase = renderedSamples;
        player = new WaveOutPlayer(OutputRate, 512, 2, RenderStereo);   // 6 x 512 프레임 ≈ 70ms
        player.Start();
    }

    // ---------------------------------------------------------------- 곡 재생

    public void Play(Song song, InstrumentBank bank, int startTick = 0, int loopStart = -1, int loopEnd = -1, int onlyVoice = -1, PlaybackOptions options = null)
    {
        lock (sync)
        {
            if (source == Source.Sop) sop.Stop();
            if (source == Source.MusicBox) musicBox.Stop();
            source = Source.Opl2;
            Func<string, AdlibInstrument> resolver = name => bank != null ? bank.Find(name) : null;
            sequencer.Start(song, resolver, startTick, loopStart, loopEnd, onlyVoice, options);
            generation++;
            this.startTick = Math.Max(0, startTick);
        }
    }

    /// <summary>SOP 곡 재생 (OPL3). 조절값은 ROL/IMS와 같은 PlaybackOptions (보이스 = 트랙 0~19).</summary>
    public void PlaySop(SopSong song, int startTick = 0, PlaybackOptions options = null)
    {
        lock (sync)
        {
            if (sequencer.IsPlaying) sequencer.Stop();
            musicBox.Stop();
            source = Source.Sop;
            sop.Start(song, Math.Max(0, startTick), options);
            generation++;
            this.startTick = Math.Max(0, startTick);
        }
    }

    /// <summary>오르골 시험 연주: (틱, 음 번호) 목록을 빗살 소리로. ticksPerSecond = 돌리는 속도.</summary>
    public void PlayMusicBox(IEnumerable<KeyValuePair<int, int>> hits, double ticksPerSecond, int startTick, int endTick)
    {
        lock (sync)
        {
            if (sequencer.IsPlaying) sequencer.Stop();
            sop.Stop();
            source = Source.MusicBox;
            musicBox.Start(hits, ticksPerSecond, Math.Max(0, startTick), endTick);
            generation++;
            this.startTick = Math.Max(0, startTick);
        }
    }

    /// <summary>오르골 시험 연주 속도 (틱/초)를 재생 중에 바꾼다.</summary>
    public void SetMusicBoxSpeed(double ticksPerSecond) { lock (sync) { musicBox.SetSpeed(ticksPerSecond); } }

    public void Stop()
    {
        lock (sync) { sequencer.Stop(); sop.Stop(); musicBox.Stop(); }
    }

    // ---------------------------------------------------------------- 재생 조절 (Jukebox)

    /// <summary>지금 재생의 조절값 (Play에 넘긴 것). 값을 바꾼 뒤에는 아래 Set...을 부른다.</summary>
    public PlaybackOptions Options { get { lock (sync) { return CurrentOptions; } } }

    private PlaybackOptions CurrentOptions { get { return sopMode ? sop.Options : sequencer.Options; } }

    public void SetSpeed(double speed) { lock (sync) { CurrentOptions.Speed = speed; if (sopMode) sop.ApplySpeed(); else sequencer.ApplySpeed(); } }
    public void SetTranspose(int semitones) { lock (sync) { CurrentOptions.Transpose = semitones; } }
    public void SetVoiceTranspose(int voice, int semitones) { lock (sync) { CurrentOptions.VoiceTranspose[voice] = semitones; } }
    public void SetVoiceMute(int voice, bool mute) { lock (sync) { CurrentOptions.VoiceMute[voice] = mute; if (sopMode) sop.ApplyMute(voice); else sequencer.ApplyMute(voice); } }
    public void SetGain(double gain) { lock (sync) { CurrentOptions.Gain = gain; } }

    /// <summary>지금 템포 배수 (곡의 템포 이벤트). SOP는 지금 템포 / 기본 템포.</summary>
    public double TempoMultiplier
    {
        get
        {
            lock (sync)
            {
                if (sopMode) return sop.Song != null && sop.Song.BasicTempo > 0 ? (double)sop.Tempo / sop.Song.BasicTempo : 1.0;
                return sequencer.TempoMultiplier;
            }
        }
    }

    /// <summary>보이스별 상태 (울리는 음, 볼륨, 악기). ROL/IMS는 11개, SOP는 트랙 20개.</summary>
    public void GetVoiceStates(VoiceState[] dst) { lock (sync) { if (sopMode) sop.CopyStates(dst); else sequencer.CopyStates(dst); } }

    // ---------------------------------------------------------------- 시음 (Instrument Maker / 건반 클릭)

    public void PreviewSetMode(bool rhythm) { lock (sync) { LeaveSop(); synth.SetRhythmMode(rhythm); } }
    public void PreviewSetInstrument(int voice, AdlibInstrument ins) { lock (sync) { LeaveSop(); synth.SetInstrument(voice, ins); } }
    public void PreviewNoteOn(int voice, int note) { lock (sync) { LeaveSop(); if (!SourcePlaying) synth.NoteOn(voice, note); } }
    public void PreviewNoteOff(int voice) { lock (sync) { if (!SourcePlaying) synth.NoteOff(voice); } }
    public void PreviewAllOff() { lock (sync) { if (!SourcePlaying) synth.AllNotesOff(); } }

    /// <summary>SOP 재생이 끝났으면 OPL2 쪽(시음)으로 돌아간다.</summary>
    private void LeaveSop() { if (source != Source.Opl2 && !SourcePlaying) source = Source.Opl2; }

    // ---------------------------------------------------------------- 렌더

    /// <summary>44.1kHz 모노 16bit 샘플을 count개 만든다 (SOP처럼 스테레오인 곡은 좌우 평균).</summary>
    public void Render(short[] buffer, int count) { RenderFrames(buffer, count, false); }

    /// <summary>44.1kHz 스테레오 16bit 프레임을 frames개 만든다 (buffer = 왼쪽, 오른쪽 번갈아, 길이 2 × frames).</summary>
    public void RenderStereo(short[] buffer, int frames) { RenderFrames(buffer, frames, true); }

    private void RenderFrames(short[] buffer, int count, bool stereo)
    {
        lock (sync)
        {
            var mark = new Mark { Start = renderedSamples, Count = count, T0 = SourceTick, Playing = SourcePlaying, Gen = generation };
            RenderLocked(buffer, count, stereo);
            mark.T1 = SourceTick;
            renderedSamples += count;
            marks[markNext] = mark;
            markNext = (markNext + 1) % marks.Length;
        }
    }

    private void RenderLocked(short[] buffer, int count, bool stereo)
    {
        var src = source;
        bool useSop = src == Source.Sop;
        double step = (double)(useSop ? opl3.NativeRate : src == Source.MusicBox ? MusicBoxPlayer.Rate : synth.NativeRate) / OutputRate;
        double gain = MasterVolume * CurrentOptions.Gain;
        for (int i = 0; i < count; i++)
        {
            resamplePos += step;
            while (resamplePos >= 1.0)
            {
                resamplePos -= 1.0;
                prevL = curL; prevR = curR;
                if (src == Source.MusicBox)
                {
                    musicBox.Step();
                    curL = curR = musicBox.Generate() * MusicBoxScale;
                }
                else if (useSop)
                {
                    int l, r;
                    sop.Step();
                    opl3.GenerateStereo(out l, out r);
                    curL = l; curR = r;
                }
                else
                {
                    sequencer.Step();
                    curL = curR = synth.Generate();
                }
            }
            double vl = SoftClip((prevL + (curL - prevL) * resamplePos) * gain);
            double vr = SoftClip((prevR + (curR - prevR) * resamplePos) * gain);
            if (stereo) { buffer[2 * i] = (short)vl; buffer[2 * i + 1] = (short)vr; }
            else buffer[i] = (short)((vl + vr) / 2);
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
