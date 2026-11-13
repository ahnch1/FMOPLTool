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
    private readonly OplChip opl3 = new OplChip(true) { MasterGain = 0.6 };
    private readonly SopPlayer sop;
    /// <summary>MIDI (.mid / .wrk): SOP와 같은 OPL3 칩을 쓴다 (둘이 함께 울리지 않음).</summary>
    private readonly MidiPlayer midi;
    private readonly MusicBoxPlayer musicBox = new MusicBoxPlayer();
    private readonly TrackerPlayer tracker = new TrackerPlayer();
    /// <summary>소리를 내는 쪽. 시음이나 ROL/IMS 재생을 시작하면 Opl2.</summary>
    private enum Source { Opl2, Sop, MusicBox, Tracker, Midi }
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
        midi = new MidiPlayer(opl3);
        toneL = toneR = OnePoleLowPass.Create(StandardCutoffHz, synth.NativeRate);
        spkL = spkR = SmallSpeaker.Create(OutputRate);
    }

    // ---------------------------------------------------------------- 음색 / 스피커 (Jukebox)

    /// <summary>'표준' 음색에서 되먹임 배율과 로우패스 차단 주파수.</summary>
    public const double StandardFeedback = 0.875, StandardCutoffHz = 12000;
    private bool toneStandard;
    private bool vintageSpeaker;
    private OnePoleLowPass toneL, toneR;
    private SmallSpeaker spkL, spkR;
    /// <summary>스피커 소리 비율 (0 = 기본값, 1 = 그 시절). 바꿀 때 30ms에 걸쳐 옮겨 가 '딱' 소리를 막는다.</summary>
    private double speakerMix;
    private static readonly double SpeakerFade = 1 - Math.Exp(-1.0 / (0.03 * OutputRate));

    /// <summary>
    /// 음색 '표준' (true): 오늘 FM 카드를 새로 설계했다면 냈을 소리 — 되먹임을 1/8 줄이고 칩 출력의 12kHz 위를 살짝 깎는다.
    /// false = '거칠게': 칩이 낸 소리 그대로. OPL(ROL/IMS/SOP)에만 해당하고 트래커 · 오르골은 그대로.
    /// </summary>
    public bool ToneStandard
    {
        get { return toneStandard; }
        set
        {
            lock (sync)
            {
                if (toneStandard == value) return;
                toneStandard = value;
                double fb = value ? StandardFeedback : 1.0;
                synth.Chip.FeedbackScale = fb;
                opl3.FeedbackScale = fb;
                toneL.Reset(); toneR.Reset();
            }
        }
    }

    /// <summary>스피커 '그 시절' (true): 모니터 옆 작은 PC 스피커로 듣는 소리 흉내 (모든 곡).</summary>
    public bool VintageSpeaker
    {
        get { return vintageSpeaker; }
        set { lock (sync) vintageSpeaker = value; }
    }

    public OplMapping Mapping { get { return synth.Mapping; } }
    public double MasterVolume { get; set; } = 1.5;
    public bool IsDeviceOpen { get { return player != null && player.IsOpen; } }
    public bool IsPlaying { get { lock (sync) { return SourcePlaying; } } }

    private bool SourcePlaying
    {
        get
        {
            switch (source)
            {
                case Source.Sop: return sop.IsPlaying && !sop.IsJam;
                case Source.MusicBox: return musicBox.IsPlaying;
                case Source.Tracker: return tracker.IsSongPlaying;
                case Source.Midi: return midi.IsPlaying;
            }
            return sequencer.IsPlaying;
        }
    }

    private double SourceTick
    {
        get
        {
            switch (source)
            {
                case Source.Sop: return sop.PositionTick;
                case Source.MusicBox: return musicBox.PositionTick;
                case Source.Tracker: return tracker.PositionMs;   // 트래커는 ms
                case Source.Midi: return midi.PositionTick;
            }
            return sequencer.PositionTick;
        }
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
            if (source == Source.Tracker) tracker.Stop();
            if (source == Source.Midi) midi.Stop();
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
            musicBox.Stop(); tracker.Stop(); midi.Stop();
            source = Source.Sop;
            sop.Start(song, Math.Max(0, startTick), options);
            generation++;
            this.startTick = Math.Max(0, startTick);
        }
    }

    /// <summary>MIDI 곡 재생 (.mid / .wrk). 위치 단위는 MIDI 틱. 조절값의 보이스 = MIDI 채널 0~15.</summary>
    public void PlayMidi(MidiSong song, double startTick = 0, PlaybackOptions options = null)
    {
        lock (sync)
        {
            if (sequencer.IsPlaying) sequencer.Stop();
            sop.Stop(); musicBox.Stop(); tracker.Stop();
            source = Source.Midi;
            midi.Start(song, Math.Max(0, startTick), options);
            generation++;
            this.startTick = Math.Max(0, startTick);
        }
    }

    /// <summary>MIDI FM 재생에 쓸 GM 뱅크 (다음 재생부터).</summary>
    public GmBank MidiBank { get { lock (sync) return midi.Bank; } set { lock (sync) midi.Bank = value; } }
    /// <summary>MIDI를 FM으로 낼지 Windows 신디사이저로 낼지 (다음 재생부터).</summary>
    public MidiSynth MidiSynth { get { lock (sync) return midi.Synth; } set { lock (sync) midi.Synth = value; } }
    /// <summary>지금 MIDI 재생이 실제로 쓰는 신디사이저 (Windows 장치를 못 열면 FM).</summary>
    public MidiSynth ActiveMidiSynth { get { lock (sync) return midi.ActiveSynth; } }

    /// <summary>MT-32 출력을 FM 곡과 비슷한 크기로 (Munt 출력은 16bit 꽉 차게 나온다).</summary>
    private const double Mt32Level = 0.7;
    private Mt32Emu mt32Emu;
    private string mt32Folder;

    /// <summary>MIDI 모듈 규격 (다음 재생부터): GM / MT-32.</summary>
    public MidiModule MidiModule { get { lock (sync) return midi.Module; } set { lock (sync) midi.Module = value; } }

    /// <summary>
    /// MT-32 에뮬레이터를 ROM 폴더로 연다 (이미 같은 폴더로 열려 있으면 그대로). 성공하면 ROM 설명, 실패하면 null과 이유.
    /// 열린 에뮬레이터는 MT-32 모듈 재생에 쓰인다.
    /// </summary>
    public string OpenMt32(string romFolder, out string error)
    {
        error = null;
        lock (sync)
        {
            if (mt32Emu != null && string.Equals(mt32Folder, romFolder, StringComparison.OrdinalIgnoreCase)) { midi.Mt32 = mt32Emu; return mt32Emu.RomDescription; }
            if (source == Source.Midi && midi.ActiveSynth == MidiSynth.Mt32) midi.Stop();
            if (mt32Emu != null) { mt32Emu.Dispose(); mt32Emu = null; }
            midi.Mt32 = null;
            mt32Folder = null;
            try
            {
                mt32Emu = Mt32Emu.Open(romFolder);
                mt32Folder = romFolder;
                midi.Mt32 = mt32Emu;
                return mt32Emu.RomDescription;
            }
            catch (Exception ex) { error = ex.Message; return null; }
        }
    }

    /// <summary>오르골 시험 연주: (틱, 음 번호) 목록을 빗살 소리로. ticksPerSecond = 돌리는 속도.</summary>
    public void PlayMusicBox(IEnumerable<KeyValuePair<int, int>> hits, double ticksPerSecond, int startTick, int endTick)
    {
        lock (sync)
        {
            if (sequencer.IsPlaying) sequencer.Stop();
            sop.Stop(); midi.Stop();
            source = Source.MusicBox;
            musicBox.Start(hits, ticksPerSecond, Math.Max(0, startTick), endTick);
            generation++;
            this.startTick = Math.Max(0, startTick);
        }
    }

    /// <summary>
    /// Scream Tracker 곡(STM/S3M) 재생. 위치(PositionTick)는 ms. singlePattern ≥ 0이면 그 패턴만, loop = 끝에서 처음으로(편집기).
    /// skipMs만큼 앞으로 감아서 시작 (Jukebox 이동).
    /// </summary>
    public void PlayTracker(TrackerModule module, int startOrder = 0, int startRow = 0, int singlePattern = -1, bool loop = false, PlaybackOptions options = null, double skipMs = 0)
    {
        lock (sync)
        {
            if (sequencer.IsPlaying) sequencer.Stop();
            sop.Stop(); musicBox.Stop(); midi.Stop();
            source = Source.Tracker;
            tracker.Start(module, startOrder, startRow, singlePattern, loop, options, skipMs);
            generation++;
            this.startTick = skipMs;
        }
    }

    /// <summary>트래커 편집기 시음: 곡 재생 중이 아니면 악기 sampleNo(1부터)로 note를 한 채널에서 낸다.</summary>
    public void TrackerJam(TrackerModule module, int sampleNo, int note, int channel)
    {
        lock (sync)
        {
            if (source == Source.Tracker && tracker.IsSongPlaying) return;
            if (sequencer.IsPlaying) sequencer.Stop();
            if (source == Source.Midi) midi.Stop();
            source = Source.Tracker;
            tracker.Jam(module, sampleNo, note, channel);
        }
    }

    public void TrackerJamOff(int channel) { lock (sync) { if (source == Source.Tracker) tracker.JamOff(channel); } }

    /// <summary>트래커 재생 위치 (렌더 위치 기준, 출력 버퍼만큼 앞설 수 있다).</summary>
    public bool GetTrackerPosition(out int order, out int pattern, out int row)
    {
        lock (sync)
        {
            order = tracker.CurrentOrder; pattern = tracker.CurrentPattern; row = tracker.CurrentRow;
            return source == Source.Tracker && tracker.IsSongPlaying;
        }
    }

    /// <summary>오르골 시험 연주 속도 (틱/초)를 재생 중에 바꾼다.</summary>
    public void SetMusicBoxSpeed(double ticksPerSecond) { lock (sync) { musicBox.SetSpeed(ticksPerSecond); } }

    public void Stop()
    {
        lock (sync) { sequencer.Stop(); sop.Stop(); musicBox.Stop(); tracker.Stop(); midi.Stop(); }
    }

    // ---------------------------------------------------------------- 재생 조절 (Jukebox)

    /// <summary>지금 재생의 조절값 (Play에 넘긴 것). 값을 바꾼 뒤에는 아래 Set...을 부른다.</summary>
    public PlaybackOptions Options { get { lock (sync) { return CurrentOptions; } } }

    private PlaybackOptions CurrentOptions { get { return sopMode ? sop.Options : source == Source.Tracker ? tracker.Options : source == Source.Midi ? midi.Options : sequencer.Options; } }

    public void SetSpeed(double speed) { lock (sync) { CurrentOptions.Speed = speed; if (sopMode) sop.ApplySpeed(); else if (source == Source.Tracker) tracker.ApplySpeed(); else if (source == Source.Midi) midi.ApplySpeed(); else sequencer.ApplySpeed(); } }
    public void SetTranspose(int semitones) { lock (sync) { CurrentOptions.Transpose = semitones; } }
    public void SetVoiceTranspose(int voice, int semitones) { lock (sync) { CurrentOptions.VoiceTranspose[voice] = semitones; } }
    public void SetVoiceMute(int voice, bool mute) { lock (sync) { CurrentOptions.VoiceMute[voice] = mute; if (sopMode) sop.ApplyMute(voice); else if (source == Source.Tracker) tracker.ApplyMute(voice); else if (source == Source.Midi) midi.ApplyMute(voice); else sequencer.ApplyMute(voice); } }
    public void SetGain(double gain) { lock (sync) { CurrentOptions.Gain = gain; } }

    /// <summary>지금 템포 배수 (곡의 템포 이벤트). SOP는 지금 템포 / 기본 템포.</summary>
    public double TempoMultiplier
    {
        get
        {
            lock (sync)
            {
                if (sopMode) return sop.Song != null && sop.Song.BasicTempo > 0 ? (double)sop.Tempo / sop.Song.BasicTempo : 1.0;
                if (source == Source.Tracker) return tracker.Module != null && tracker.Module.Kind == TrackerKind.S3m && tracker.Module.Tempo > 0 ? (double)tracker.Tempo / tracker.Module.Tempo : 1.0;
                if (source == Source.Midi) return midi.Song != null ? midi.Bpm / midi.Song.InitialBpm : 1.0;
                return sequencer.TempoMultiplier;
            }
        }
    }

    /// <summary>보이스별 상태 (울리는 음, 볼륨, 악기). ROL/IMS는 11개, SOP는 트랙 20개.</summary>
    public void GetVoiceStates(VoiceState[] dst) { lock (sync) { if (sopMode) sop.CopyStates(dst); else if (source == Source.Tracker) tracker.CopyStates(dst); else if (source == Source.Midi) midi.CopyStates(dst); else sequencer.CopyStates(dst); } }

    // ---------------------------------------------------------------- 시음 (Instrument Maker / 건반 클릭)

    // 4-op 악기(SOP)는 OPL2 시음 신디사이저로 낼 수 없어 OPL3(SopPlayer 시음)로 소리 낸다. 멜로디 보이스만.
    private readonly AdlibInstrument[] previewIns = new AdlibInstrument[OplSynth.VoiceCount];
    private int jamVoice = -1;

    private bool Is4OpPreview(int voice)
    {
        if (voice < 0 || voice >= previewIns.Length) return false;
        var ins = previewIns[voice];
        return ins != null && ins.Is4Op && (!synth.RhythmMode || voice < 6);
    }

    /// <summary>시음 모드 (리듬 / 멜로디). 4-op 시음 중이면 그 소리는 그대로 둔다 (편집하면서 누르고 있는 음).</summary>
    public void PreviewSetMode(bool rhythm) { lock (sync) { if (!(source == Source.Sop && sop.IsJam)) LeaveSop(); synth.SetRhythmMode(rhythm); } }

    public void PreviewSetInstrument(int voice, AdlibInstrument ins)
    {
        lock (sync)
        {
            if (voice >= 0 && voice < previewIns.Length) previewIns[voice] = ins;
            if (source == Source.Sop && sop.IsJam && voice == jamVoice && ins != null && ins.Is4Op) { sop.JamUpdate(SopConvert.ToSop(ins)); return; }
            LeaveSop();
            synth.SetInstrument(voice, ins);
        }
    }

    public void PreviewNoteOn(int voice, int note)
    {
        lock (sync)
        {
            LeaveSop();
            if (SourcePlaying) return;
            if (Is4OpPreview(voice))
            {
                synth.AllNotesOff();
                source = Source.Sop;
                jamVoice = voice;
                sop.JamOn(SopConvert.ToSop(previewIns[voice]), note);
                return;
            }
            synth.NoteOn(voice, note);
        }
    }

    public void PreviewNoteOff(int voice)
    {
        lock (sync)
        {
            if (source == Source.Sop && sop.IsJam) { if (voice == jamVoice) sop.JamOff(); return; }
            if (!SourcePlaying) synth.NoteOff(voice);
        }
    }

    public void PreviewAllOff()
    {
        lock (sync)
        {
            if (source == Source.Sop && sop.IsJam) { sop.JamOff(); return; }
            if (!SourcePlaying) synth.AllNotesOff();
        }
    }

    /// <summary>SOP 재생(또는 4-op 시음)이 끝났으면 OPL2 쪽(시음)으로 돌아간다.</summary>
    private void LeaveSop()
    {
        if (source == Source.Sop && sop.IsJam) { sop.Stop(); source = Source.Opl2; return; }
        if (source != Source.Opl2 && !SourcePlaying) source = Source.Opl2;
    }

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
        bool useSop = src == Source.Sop || src == Source.Midi;
        bool mt32 = src == Source.Midi && midi.ActiveSynth == MidiSynth.Mt32;
        double step = (double)(src == Source.Midi ? midi.Rate : useSop ? opl3.NativeRate : src == Source.MusicBox ? MusicBoxPlayer.Rate : src == Source.Tracker ? TrackerPlayer.Rate : synth.NativeRate) / OutputRate;
        double gain = MasterVolume * CurrentOptions.Gain;
        for (int i = 0; i < count; i++)
        {
            resamplePos += step;
            while (resamplePos >= 1.0)
            {
                resamplePos -= 1.0;
                prevL = curL; prevR = curR;
                if (src == Source.Tracker)
                {
                    double tl, tr;
                    tracker.Generate(out tl, out tr);
                    curL = tl; curR = tr;
                }
                else if (src == Source.MusicBox)
                {
                    musicBox.Step();
                    curL = curR = musicBox.Generate() * MusicBoxScale;
                }
                else if (useSop)
                {
                    int l, r;
                    if (src == Source.Midi) midi.Step(); else sop.Step();
                    if (mt32) { midi.Mt32.Render(out l, out r); l = (int)(l * Mt32Level); r = (int)(r * Mt32Level); }
                    else opl3.GenerateStereo(out l, out r);
                    curL = l; curR = r;
                }
                else
                {
                    sequencer.Step();
                    curL = curR = synth.Generate();
                }
                if (toneStandard && !mt32 && (src == Source.Opl2 || useSop))
                {
                    curL = toneL.Process(curL);
                    curR = toneR.Process(curR);
                }
            }
            double vl = (prevL + (curL - prevL) * resamplePos) * gain;
            double vr = (prevR + (curR - prevR) * resamplePos) * gain;
            // 스피커: 필터는 늘 돌려 두고(바꿀 때 상태가 이어지게) 비율만 옮긴다
            double wl = spkL.Process(vl), wr = spkR.Process(vr);
            speakerMix += ((vintageSpeaker ? 1.0 : 0.0) - speakerMix) * SpeakerFade;
            if (speakerMix > 1e-4)
            {
                vl += (wl - vl) * speakerMix;
                vr += (wr - vr) * speakerMix;
            }
            vl = SoftClip(vl);
            vr = SoftClip(vr);
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
        midi.Dispose();
        if (mt32Emu != null) { mt32Emu.Dispose(); mt32Emu = null; }
    }
}
