using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>MIDI를 무엇으로 소리 낼지.</summary>
public enum MidiSynth
{
    /// <summary>OPL3 FM (GM 뱅크: GENMIDI.OP2 또는 BNK 이름 맞춤). 오디오 엔진 · 음색 · 스피커 · Nuked가 모두 적용된다.</summary>
    Fm,
    /// <summary>Windows MIDI 출력 (기본 = Microsoft GS Wavetable Synth). 엔진 소리는 무음.</summary>
    Windows,
    /// <summary>Roland MT-32 에뮬레이터 (Munt, <see cref="Mt32Emu"/>). 엔진 스테레오 출력, 음색 · 스피커 중 스피커만 적용.</summary>
    Mt32,
}

/// <summary>곡이 어떤 MIDI 모듈을 위해 만들어졌는지 (설정 → MIDI module).</summary>
public enum MidiModule
{
    /// <summary>General MIDI.</summary>
    Gm,
    /// <summary>Roland MT-32: 에뮬레이터(ROM)가 있으면 그것으로, 없으면 MT-32 음색 번호를 GM에 맞춰 FM / Windows로.</summary>
    Mt32,
}

/// <summary>
/// MIDI 곡 재생기 (.mid / .wrk → <see cref="MidiSong"/>). <see cref="SopPlayer"/>처럼 칩 샘플 1개마다 <see cref="Step"/>을 부른다.
/// <para>FM: OPL3 2-op 채널 18개에 음을 나눠 싣는다 (빈 채널 → 가장 오래 꺼져 있던 채널 → 가장 오래 울린 음을 뺏음). GM 드럼은 MIDI 채널 10.
/// 볼륨 · 익스프레션 · 벨로시티는 GM 권장 곡선(40·log10)으로 캐리어(가산 연결이면 모듈레이터도) TL에 더한다. 팬은 왼쪽 · 가운데 · 오른쪽 3단계 (OPL3 C0 비트).
/// 피치 벤드 (RPN 0 = 벤드 범위, 기본 ±2), 서스테인 페달(CC64), CC121 · 123 지원.</para>
/// <para>Windows: 이벤트를 midiOutShortMsg로, SysEx는 midiOutLongMsg로 보낸다. 조옮김 · 채널 끄기 · 속도 · 볼륨(CC7 비율)도 적용된다.
/// 시작할 때 곡 규격에 맞는 리셋(GS 리셋 / GM On …)을 먼저 보내 앞 곡의 설정이 남지 않게 한다.</para>
/// <para>리셋 SysEx (GM · GM2 · GS · XG): 채널 상태를 처음으로 되돌리고 규격(모드)을 바꾼다. 드럼 채널: 기본은 10,
/// GS "Use for rhythm part" SysEx, XG 뱅크 MSB 126 · 127, GM2 뱅크 MSB 120 (프로그램 체인지 때 적용)으로 다른 채널도 드럼이 된다.
/// FM은 드럼 채널이면 GM 뱅크의 드럼 음색을 쓰고, Windows(GS 신디)에는 XG · GM2 드럼 채널을 GS SysEx로 바꿔 알린다.</para>
/// <para>MT-32 (<see cref="MidiModule.Mt32"/>): 에뮬레이터가 열려 있으면 이벤트 · SysEx를 그대로 보내고 엔진이 그 출력을 낸다.
/// 없으면 프로그램 번호를 <see cref="GeneralMidi.Mt32ToGm"/>로 바꿔 FM / Windows로 낸다.</para>
/// 보이스 상태(<see cref="CopyStates"/>)는 MIDI 채널 16개.
/// </summary>
public sealed class MidiPlayer : IDisposable
{
    public const int Voices = 18;
    private static readonly int[] ModOff = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };
    private static readonly int[] CarOff = { 3, 4, 5, 11, 12, 13, 19, 20, 21 };

    private readonly OplChip chip;
    private MidiSong song;
    private PlaybackOptions options = new PlaybackOptions();
    private int next;
    private double tick, ticksPerSample;
    private int tempo = MidiSong.DefaultTempo;
    private bool playing, ending;
    private int tailSamples;

    // MIDI 채널 상태
    private readonly int[] program = new int[16];
    private readonly int[] volume = new int[16];
    private readonly int[] expression = new int[16];
    private readonly int[] pan = new int[16];
    private readonly int[] bend = new int[16];
    private readonly int[] bendRange = new int[16];
    private readonly bool[] sustain = new bool[16];
    private readonly int[] rpn = new int[16];
    private readonly int[] bankMsb = new int[16];
    private readonly int[] bankLsb = new int[16];
    private readonly bool[] drum = new bool[16];
    /// <summary>지금 규격 (곡 안 리셋 SysEx로 바뀐다). XG · GM2면 뱅크 MSB가 드럼을 정한다.</summary>
    private MidiStandard mode;
    private readonly VoiceState[] states = new VoiceState[16];

    // FM 보이스
    private struct Voice
    {
        public int Channel, Note, SentNote;   // Note = 원래 MIDI 음 (키오프 짝 맞추기), SentNote = 낸 음
        public bool KeyOn, Sustained;
        public long Age;
        public AdlibInstrument Ins;
        public double Pitch;                  // 반음 (벤드 제외: 옮김 · 오프셋 · 미세 조정 포함)
        public int Velocity;
        public bool Fixed;
    }
    private readonly Voice[] voices = new Voice[Voices];
    private long clock;

    // Windows
    private IntPtr midiOut;
    private readonly int[,] sentNote = new int[16, 128];
    private double lastGain = -1;

    public MidiPlayer(OplChip chip) { this.chip = chip; }

    /// <summary>FM GM 뱅크. null이면 기본 악기 하나로.</summary>
    public GmBank Bank { get; set; }
    /// <summary>다음 Start부터 쓸 신디사이저 (GM: FM / Windows).</summary>
    public MidiSynth Synth { get; set; } = MidiSynth.Fm;
    /// <summary>다음 Start부터의 MIDI 모듈 규격.</summary>
    public MidiModule Module { get; set; } = MidiModule.Gm;
    /// <summary>열린 MT-32 에뮬레이터 (MT-32 모듈일 때 엔진이 넣는다). null이면 GM 대응.</summary>
    public Mt32Emu Mt32 { get; set; }
    /// <summary>MT-32 곡을 에뮬레이터 없이 GM 번호로 바꿔 내는 중.</summary>
    private bool Mt32Mapped { get { return Module == MidiModule.Mt32 && ActiveSynth != MidiSynth.Mt32; } }
    /// <summary>Step 1번 = 출력 샘플 1개의 주기 (OPL = 49716Hz, MT-32 = 32000Hz).</summary>
    public int Rate { get { return ActiveSynth == MidiSynth.Mt32 && Mt32 != null ? Mt32.Rate : chip.NativeRate; } }
    /// <summary>지금 재생이 쓰는 신디사이저.</summary>
    public MidiSynth ActiveSynth { get; private set; }

    public bool IsPlaying { get { return playing; } }
    public double PositionTick { get { return tick; } }
    public PlaybackOptions Options { get { return options; } }
    public MidiSong Song { get { return song; } }
    /// <summary>지금 템포 (BPM, 속도 배수 제외).</summary>
    public double Bpm { get { return 60000000.0 / tempo; } }
    /// <summary>지금 규격 (리셋 SysEx로 바뀐 것).</summary>
    public MidiStandard Mode { get { return mode; } }
    /// <summary>채널 c가 지금 드럼 채널인가.</summary>
    public bool IsDrum(int c) { return c >= 0 && c < 16 && drum[c]; }

    public void CopyStates(VoiceState[] dst) { Array.Copy(states, dst, Math.Min(dst.Length, states.Length)); }

    // ------------------------------------------------------------ 시작 / 정지

    public void Start(MidiSong s, double startTick, PlaybackOptions opts = null)
    {
        song = s;
        options = opts ?? new PlaybackOptions();
        ActiveSynth = Synth == MidiSynth.Mt32 ? MidiSynth.Fm : Synth;
        if (Module == MidiModule.Mt32 && Mt32 != null && Mt32.IsOpen) ActiveSynth = MidiSynth.Mt32;
        else if (ActiveSynth == MidiSynth.Windows && !OpenWindows()) ActiveSynth = MidiSynth.Fm;
        ResetChip();
        mode = s.Standard == MidiStandard.Mt32 ? MidiStandard.None : s.Standard;
        ResetChannels();
        if (ActiveSynth == MidiSynth.Mt32) Mt32.Reset();   // 앞 곡의 사용자 음색 · 설정을 지운다 (전원을 다시 켠 것처럼)
        SendAllOff();
        if (ActiveSynth == MidiSynth.Windows)
        {
            // 앞 곡의 GS 설정(드럼 파트 · 리버브 …)을 지운다: 곡 규격의 리셋, XG는 GS 신디가 모르므로 GM On도
            if (s.Standard == MidiStandard.Gs) SendSysEx(MidiSysEx.GsReset);
            else if (s.Standard == MidiStandard.Gm2) SendSysEx(MidiSysEx.Gm2On);
            else { SendSysEx(MidiSysEx.GmOn); if (s.Standard == MidiStandard.Xg) SendSysEx(MidiSysEx.XgOn); }
            for (int c = 0; c < 16; c++) { Send(0xB0 | c, 121, 0); Send(0xB0 | c, 0, 0); Send(0xB0 | c, 32, 0); Send(0xC0 | c, 0, 0); Send(0xE0 | c, 0, 64); SendVolume(c); }
        }
        tempo = MidiSong.DefaultTempo;
        next = 0;
        // 앞으로 감기: 노트는 건너뛰고 템포 · 프로그램 · 컨트롤 · 벤드만
        while (next < s.Events.Count && s.Events[next].Tick < startTick)
        {
            var e = s.Events[next++];
            if (e.IsTempo) tempo = e.Tempo;
            else if (!e.IsNoteOn && !e.IsNoteOff) Apply(e);
        }
        tick = Math.Max(0, startTick);
        ending = false;
        tailSamples = 0;
        SetTempo(tempo);
        playing = true;
    }

    public void Stop()
    {
        playing = false;
        AllNotesOff();
        SendAllOff();
    }

    public void ApplySpeed() { SetTempo(tempo); }

    public void ApplyMute(int ch)
    {
        if (ch < 0 || ch >= 16 || !options.VoiceMute[ch]) return;
        for (int v = 0; v < Voices; v++) if (voices[v].KeyOn && voices[v].Channel == ch) KeyOff(v);
        if (ActiveSynth != MidiSynth.Fm) { Send(0xB0 | ch, 123, 0); ClearSent(ch); }
        states[ch].Note = -1;
    }

    private void SetTempo(int usPerQuarter)
    {
        tempo = usPerQuarter <= 0 ? MidiSong.DefaultTempo : usPerQuarter;
        double speed = options.Speed < 0.05 ? 0.05 : options.Speed;
        ticksPerSample = song.Division * 1e6 / tempo * speed / Rate;
    }

    private void ResetChannels()
    {
        for (int c = 0; c < 16; c++)
        {
            program[c] = 0; volume[c] = 100; expression[c] = 127; pan[c] = 64; bend[c] = 8192; bendRange[c] = 2; sustain[c] = false; rpn[c] = 0x3FFF;
            drum[c] = c == GeneralMidi.DrumChannel;
            bankMsb[c] = !drum[c] ? 0 : mode == MidiStandard.Xg ? MidiSysEx.XgDrumBank : mode == MidiStandard.Gm2 ? MidiSysEx.Gm2DrumBank : 0;
            bankLsb[c] = 0;
            states[c] = new VoiceState { Note = -1, Volume = 100 / 127f, NoteOnCount = states[c].NoteOnCount, Instrument = InstrumentName(c), InstrumentFound = PatchFound(c), Drum = drum[c] };
        }
        lastGain = -1;
    }

    private void ResetChip()
    {
        chip.Reset();
        chip.Write(0x105, 0x01);   // OPL3
        chip.Write(0x104, 0x00);   // 4-op 없음
        chip.Write(0x01, 0x20);
        chip.Write(0x08, 0x00);
        chip.Write(0xBD, 0x00);
        for (int v = 0; v < Voices; v++) voices[v] = new Voice { Channel = -1, Note = -1 };
        clock = 0;
    }

    // ------------------------------------------------------------ 진행

    public bool Step()
    {
        if (!playing) return false;
        var ev = song.Events;
        while (next < ev.Count && ev[next].Tick <= tick)
        {
            var e = ev[next++];
            if (e.IsTempo) SetTempo(e.Tempo);
            else Apply(e);
        }
        if (ActiveSynth == MidiSynth.Windows && options.Gain != lastGain) SendVolumes();
        tick += ticksPerSample;
        clock++;
        if (!ending && next >= ev.Count && tick > song.LengthTicks)
        {
            ending = true;
            AllNotesOff();
        }
        if (ending && ++tailSamples > Rate)
        {
            playing = false;
            SendAllOff();
            return false;
        }
        return true;
    }

    private void Apply(MidiEvent e)
    {
        int c = e.Channel;
        switch (e.Kind)
        {
            case 0x80: NoteOff(c, e.Data1); break;
            case 0x90: if (e.Data2 == 0) NoteOff(c, e.Data1); else NoteOn(c, e.Data1, e.Data2); break;
            case 0xB0: Control(c, e.Data1, e.Data2); break;
            case 0xC0:
                ApplyBank(c);
                program[c] = Mt32Mapped && !drum[c] ? GeneralMidi.Mt32ToGm[e.Data1 & 127] : e.Data1;
                states[c].Instrument = InstrumentName(c);
                states[c].InstrumentFound = PatchFound(c);
                Send(0xC0 | c, program[c], 0);
                break;
            case 0xF0:
                if (e.SysEx != null) ApplySysEx(e.SysEx);
                break;
            case 0xE0:
                bend[c] = e.Data1 | (e.Data2 << 7);
                Send(0xE0 | c, e.Data1, e.Data2);
                for (int v = 0; v < Voices; v++) if (voices[v].Channel == c && voices[v].Note >= 0) WriteFreq(v);
                break;
            case 0xA0:
            case 0xD0:
                Send(e.Status, e.Data1, e.Data2);
                break;
        }
    }

    /// <summary>
    /// 리셋 SysEx면 채널 상태를 처음으로 (규격도 바꿈), GS 드럼 파트 SysEx면 그 채널을 드럼 / 멜로디로. 그 뒤 장치(Windows · MT-32)로 그대로 보낸다.
    /// </summary>
    private void ApplySysEx(byte[] sx)
    {
        var kind = MidiSysEx.Classify(sx, out int pc, out int val);
        switch (kind)
        {
            case SysExKind.GmOn: ResetByMessage(MidiStandard.Gm); break;
            case SysExKind.GmOff: ResetByMessage(MidiStandard.None); break;
            case SysExKind.Gm2On: ResetByMessage(MidiStandard.Gm2); break;
            case SysExKind.GsReset: ResetByMessage(MidiStandard.Gs); break;
            case SysExKind.XgOn: ResetByMessage(MidiStandard.Xg); break;
            case SysExKind.GsRhythmPart:
                if (pc >= 0 && pc < 16 && ActiveSynth != MidiSynth.Mt32) SetDrum(pc, val != 0, false);
                break;
        }
        SendSysEx(sx);
    }

    /// <summary>리셋 메시지: 울리는 음을 끄고 채널 상태(프로그램 · 볼륨 · 벤드 · 뱅크 · 드럼)를 처음으로.</summary>
    private void ResetByMessage(MidiStandard newMode)
    {
        if (ActiveSynth == MidiSynth.Mt32) return;   // MT-32은 GM · GS 리셋을 모른다
        AllNotesOff();
        mode = newMode;
        ResetChannels();
    }

    /// <summary>XG · GM2: 프로그램 체인지 때 뱅크 MSB로 드럼 / 멜로디를 정한다.</summary>
    private void ApplyBank(int c)
    {
        if (mode == MidiStandard.Xg) SetDrum(c, bankMsb[c] == MidiSysEx.XgDrumBank || bankMsb[c] == MidiSysEx.XgSfxBank, true);
        else if (mode == MidiStandard.Gm2) SetDrum(c, bankMsb[c] == MidiSysEx.Gm2DrumBank, true);
    }

    /// <param name="tellDevice">Windows(GS 신디)에 GS 드럼 파트 SysEx로 알린다 (XG · GM2 뱅크로 바뀐 경우).</param>
    private void SetDrum(int c, bool on, bool tellDevice)
    {
        if (drum[c] == on) return;
        for (int v = 0; v < Voices; v++) if (voices[v].Channel == c && voices[v].KeyOn) KeyOff(v);
        drum[c] = on;
        states[c].Drum = on;
        states[c].Note = -1;
        states[c].Instrument = InstrumentName(c);
        states[c].InstrumentFound = PatchFound(c);
        if (tellDevice && ActiveSynth == MidiSynth.Windows)
            SendSysEx(MidiSysEx.GsData(0x40, 0x10 | MidiSysEx.ChannelToGsBlock(c), 0x15, on ? 1 : 0));
    }

    private void Control(int c, int cc, int val)
    {
        switch (cc)
        {
            case 0: bankMsb[c] = val; break;
            case 32: bankLsb[c] = val; break;
            case 7:
                volume[c] = val; UpdateVolumes(c);
                if (ActiveSynth == MidiSynth.Windows) SendVolume(c);
                else if (ActiveSynth == MidiSynth.Mt32) Mt32.Send(0xB0 | c, 7, val);   // MT-32은 엔진을 거치므로 볼륨 비율은 엔진에서
                return;
            case 11: expression[c] = val; UpdateVolumes(c); break;
            case 10: pan[c] = val; UpdatePan(c); break;
            case 64:
                sustain[c] = val >= 64;
                if (!sustain[c])
                    for (int v = 0; v < Voices; v++)
                        if (voices[v].Channel == c && voices[v].Sustained) { voices[v].Sustained = false; KeyOff(v); }
                break;
            case 101: rpn[c] = (rpn[c] & 0x7F) | (val << 7); break;
            case 100: rpn[c] = (rpn[c] & 0x3F80) | val; break;
            case 6: if (rpn[c] == 0) bendRange[c] = Math.Max(0, Math.Min(24, val)); break;
            case 120:
            case 123:
                for (int v = 0; v < Voices; v++) if (voices[v].Channel == c && voices[v].KeyOn) KeyOff(v);
                states[c].Note = -1;
                break;
            case 121:
                expression[c] = 127; pan[c] = 64; bend[c] = 8192; sustain[c] = false;
                UpdateVolumes(c); UpdatePan(c);
                break;
        }
        Send(0xB0 | c, cc, val);
    }

    // ------------------------------------------------------------ 노트

    private int Transposed(int c, int n)
    {
        return n + options.VoiceTranspose[c] + (drum[c] ? 0 : options.Transpose);
    }

    private void NoteOn(int c, int n, int vel)
    {
        states[c].Note = n;
        states[c].NoteOnCount++;
        states[c].Volume = Math.Max(0.15f, vel / 127f * volume[c] / 127f * expression[c] / 127f);
        if (options.VoiceMute[c]) return;
        if (ActiveSynth != MidiSynth.Fm)
        {
            int t = drum[c] ? n + options.VoiceTranspose[c] : Transposed(c, n);
            if (t < 0 || t > 127) return;
            if (sentNote[c, n] >= 0) Send(0x80 | c, sentNote[c, n], 0);
            sentNote[c, n] = t;
            Send(0x90 | c, t, vel);
            return;
        }
        // FM: 같은 채널 · 같은 음이 울리고 있으면 다시 친다
        for (int v = 0; v < Voices; v++) if (voices[v].Channel == c && voices[v].Note == n && voices[v].KeyOn) KeyOff(v);
        var patch = Patch(c, n);
        if (patch == null || patch.Voice1 == null) return;
        bool isDrum = drum[c];
        int baseNote = patch.FixedNote >= 0 ? patch.FixedNote + (isDrum ? options.VoiceTranspose[c] : 0) : Transposed(c, n);
        StartVoice(c, n, vel, patch.Voice1, baseNote + patch.Offset1, patch.FixedNote >= 0);
        if (patch.Voice2 != null)
            StartVoice(c, n, vel, patch.Voice2, baseNote + patch.Offset2 + ((patch.FineTune / 2) - 64) / 32.0, patch.FixedNote >= 0);
    }

    private void NoteOff(int c, int n)
    {
        if (states[c].Note == n) states[c].Note = -1;
        if (ActiveSynth != MidiSynth.Fm)
        {
            if (sentNote[c, n] >= 0) { Send(0x80 | c, sentNote[c, n], 0); sentNote[c, n] = -1; }
            return;
        }
        for (int v = 0; v < Voices; v++)
        {
            if (voices[v].Channel != c || voices[v].Note != n || !voices[v].KeyOn || voices[v].Sustained) continue;
            if (sustain[c]) voices[v].Sustained = true;
            else KeyOff(v);
        }
    }

    private void StartVoice(int c, int n, int vel, AdlibInstrument ins, double pitch, bool fixedPitch)
    {
        int v = Allocate();
        if (voices[v].KeyOn) KeyOffRegister(v);
        voices[v] = new Voice { Channel = c, Note = n, KeyOn = true, Age = clock, Ins = ins, Pitch = pitch, Velocity = vel, Fixed = fixedPitch };
        LoadInstrument(v);
        WriteFreq(v);
    }

    /// <summary>빈 채널 → 가장 오래 꺼져 있던 채널 → 가장 오래 울린 음.</summary>
    private int Allocate()
    {
        int best = -1;
        long bestAge = long.MaxValue;
        for (int v = 0; v < Voices; v++)
            if (!voices[v].KeyOn && voices[v].Age < bestAge) { best = v; bestAge = voices[v].Age; }
        if (best >= 0) return best;
        for (int v = 0; v < Voices; v++)
        {
            // 드럼보다 멜로디를, 서스테인으로 남은 음을 먼저 뺏는다
            long age = voices[v].Age - (voices[v].Sustained ? 1L << 40 : 0) + (IsDrum(voices[v].Channel) ? 1L << 20 : 0);
            if (age < bestAge) { best = v; bestAge = age; }
        }
        return best;
    }

    private void KeyOff(int v)
    {
        if (!voices[v].KeyOn) return;
        voices[v].KeyOn = false;
        voices[v].Sustained = false;
        voices[v].Age = clock;
        KeyOffRegister(v);
    }

    private void AllNotesOff()
    {
        for (int v = 0; v < Voices; v++) KeyOff(v);
        for (int c = 0; c < 16; c++) states[c].Note = -1;
    }

    // ------------------------------------------------------------ OPL 레지스터

    private static void Port(int v, out int bank, out int ch) { bank = v < 9 ? 0 : 0x100; ch = v % 9; }

    private GmPatch Patch(int c, int n)
    {
        if (Bank == null) return null;
        if (drum[c]) return n >= 0 && n < 128 ? Bank.Drums[n] : null;
        return Bank.Melodic[program[c] & 127];
    }

    private string InstrumentName(int c)
    {
        int p = program[c] & 127;
        if (drum[c])
        {
            if (ActiveSynth == MidiSynth.Mt32) return "Rhythm";
            if (mode == MidiStandard.Xg) return bankMsb[c] == MidiSysEx.XgSfxBank ? "SFX Drums" : "Drums" + (p != 0 ? " · Kit " + (p + 1) : "");
            return mode == MidiStandard.Gs || p != 0 ? "Drums · " + MidiSysEx.GsDrumKitName(p) : "Drums";
        }
        if (ActiveSynth == MidiSynth.Mt32) return "MT-32 " + (p + 1) + " · " + GeneralMidi.ProgramNames[GeneralMidi.Mt32ToGm[p]];
        string name = GeneralMidi.ProgramNames[p];
        if (bankMsb[c] != 0 || bankLsb[c] != 0) name += " · 뱅크 " + bankMsb[c] + (bankLsb[c] != 0 ? "/" + bankLsb[c] : "");
        return name;
    }

    private bool PatchFound(int c)
    {
        if (ActiveSynth != MidiSynth.Fm) return true;
        return Bank != null && (drum[c] || Bank.Melodic[program[c] & 127] != null);
    }

    /// <summary>GM 권장 곡선: dB = 40·log10(v/127). OPL TL 1 = 0.75dB.</summary>
    private static int Attenuation(double factor)
    {
        if (factor <= 0.0005) return 63;
        double db = -40 * Math.Log10(factor);
        return (int)Math.Round(db / 0.75);
    }

    private int ChannelAttenuation(int v)
    {
        int c = voices[v].Channel;
        return Attenuation(voices[v].Velocity / 127.0) + Attenuation(volume[c] / 127.0) + Attenuation(expression[c] / 127.0);
    }

    private int PanBits(int c) { return pan[c] < 43 ? 0x10 : pan[c] > 85 ? 0x20 : 0x30; }

    private void LoadInstrument(int v)
    {
        var ins = voices[v].Ins;
        Port(v, out int bank, out int ch);
        WriteOperator(bank, ModOff[ch], ins.Modulator, ins.Additive ? ins.Modulator.TotalLevel + ChannelAttenuation(v) : ins.Modulator.TotalLevel);
        WriteOperator(bank, CarOff[ch], ins.Carrier, ins.Carrier.TotalLevel + ChannelAttenuation(v));
        WriteC0(v);
    }

    private void WriteC0(int v)
    {
        var ins = voices[v].Ins;
        Port(v, out int bank, out int ch);
        chip.Write(bank | (0xC0 + ch), ((ins.Modulator.Feedback & 7) << 1) | (ins.Additive ? 1 : 0) | PanBits(voices[v].Channel));
    }

    private void WriteOperator(int bank, int off, OplOperator op, int tl)
    {
        chip.Write(bank | (0x20 + off), (op.Am != 0 ? 0x80 : 0) | (op.Vib != 0 ? 0x40 : 0) | (op.Eg != 0 ? 0x20 : 0) | (op.Ksr != 0 ? 0x10 : 0) | (op.Multiple & 0x0F));
        chip.Write(bank | (0x40 + off), ((op.Ksl & 3) << 6) | Math.Max(0, Math.Min(63, tl)));
        chip.Write(bank | (0x60 + off), ((op.Attack & 0x0F) << 4) | (op.Decay & 0x0F));
        chip.Write(bank | (0x80 + off), ((op.Sustain & 0x0F) << 4) | (op.ReleaseRate & 0x0F));
        chip.Write(bank | (0xE0 + off), op.WaveSelect & 7);
    }

    private void UpdateVolumes(int c)
    {
        states[c].Volume = volume[c] / 127f * expression[c] / 127f;
        if (ActiveSynth != MidiSynth.Fm) return;
        for (int v = 0; v < Voices; v++)
        {
            if (voices[v].Channel != c || voices[v].Ins == null) continue;
            var ins = voices[v].Ins;
            Port(v, out int bank, out int ch);
            int att = ChannelAttenuation(v);
            chip.Write(bank | (0x40 + CarOff[ch]), ((ins.Carrier.Ksl & 3) << 6) | Math.Min(63, ins.Carrier.TotalLevel + att));
            if (ins.Additive) chip.Write(bank | (0x40 + ModOff[ch]), ((ins.Modulator.Ksl & 3) << 6) | Math.Min(63, ins.Modulator.TotalLevel + att));
        }
    }

    private void UpdatePan(int c)
    {
        if (ActiveSynth != MidiSynth.Fm) return;
        for (int v = 0; v < Voices; v++) if (voices[v].Channel == c && voices[v].Ins != null) WriteC0(v);
    }

    private void WriteFreq(int v)
    {
        int c = voices[v].Channel;
        double semis = voices[v].Pitch + (voices[v].Fixed && drum[c] ? 0 : (bend[c] - 8192) / 8192.0 * bendRange[c]);
        double freq = 261.6255653 * Math.Pow(2.0, (semis - 60) / 12.0);
        int block, fnum = 1023;
        for (block = 0; block < 8; block++)
        {
            fnum = (int)Math.Round(freq * (1 << (20 - block)) / chip.NativeRate);
            if (fnum <= 1023) break;
        }
        if (block > 7) { block = 7; fnum = 1023; }
        if (fnum < 0) fnum = 0;
        Port(v, out int bank, out int ch);
        chip.Write(bank | (0xA0 + ch), fnum & 0xFF);
        chip.Write(bank | (0xB0 + ch), (voices[v].KeyOn ? 0x20 : 0) | (block << 2) | (fnum >> 8));
        voices[v].SentNote = fnum | (block << 10);
    }

    private void KeyOffRegister(int v)
    {
        Port(v, out int bank, out int ch);
        int f = voices[v].SentNote;
        chip.Write(bank | (0xB0 + ch), ((f >> 10) << 2) | ((f & 1023) >> 8));
    }

    // ------------------------------------------------------------ Windows MIDI 출력

    [DllImport("winmm.dll")] private static extern int midiOutOpen(out IntPtr handle, int deviceId, IntPtr callback, IntPtr instance, int flags);
    [DllImport("winmm.dll")] private static extern int midiOutShortMsg(IntPtr handle, int message);
    [DllImport("winmm.dll")] private static extern int midiOutReset(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int midiOutClose(IntPtr handle);
    [DllImport("winmm.dll")] private static extern int midiOutPrepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int midiOutUnprepareHeader(IntPtr handle, IntPtr header, int size);
    [DllImport("winmm.dll")] private static extern int midiOutLongMsg(IntPtr handle, IntPtr header, int size);

    /// <summary>MIDIHDR (mmsystem.h). 순서 배치가 C 구조체와 같다 (32 · 64비트 모두).</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MidiHdr
    {
        public IntPtr Data;
        public int BufferLength;
        public int BytesRecorded;
        public IntPtr User;
        public int Flags;
        public IntPtr Next;
        public IntPtr Reserved;
        public int Offset;
        public IntPtr R0, R1, R2, R3, R4, R5, R6, R7;
    }

    private static readonly int HdrSize = Marshal.SizeOf(typeof(MidiHdr));
    private const int MidiErrStillPlaying = 65;
    /// <summary>장치가 아직 다 보내지 않은 SysEx 버퍼 (다 보내면 풀어 준다).</summary>
    private readonly List<(IntPtr hdr, IntPtr data)> pendingSysEx = new List<(IntPtr, IntPtr)>();
    /// <summary>보낸 SysEx 수 (Windows 장치로, 시험 · 진단용).</summary>
    public int SysExSent { get; private set; }
    /// <summary>장치가 아직 붙잡고 있는 SysEx 버퍼 수.</summary>
    public int SysExPending { get { return pendingSysEx.Count; } }

    /// <summary>SysEx 하나 보내기 (F0 … F7): MT-32 에뮬레이터 또는 Windows 장치. FM은 할 일 없음.</summary>
    private void SendSysEx(byte[] sx)
    {
        if (sx == null || sx.Length < 2) return;
        if (ActiveSynth == MidiSynth.Mt32) { Mt32?.SendSysEx(sx); return; }
        if (ActiveSynth != MidiSynth.Windows || midiOut == IntPtr.Zero) return;
        if (sx.Length > 4 && sx[1] == 0x41 && sx[3] == 0x16) return;   // MT-32 SysEx는 GS 신디에 보내지 않는다
        ReleaseSysEx(false);
        IntPtr data = Marshal.AllocHGlobal(sx.Length);
        IntPtr hdr = Marshal.AllocHGlobal(HdrSize);
        Marshal.Copy(sx, 0, data, sx.Length);
        Marshal.StructureToPtr(new MidiHdr { Data = data, BufferLength = sx.Length, BytesRecorded = sx.Length }, hdr, false);
        try
        {
            if (midiOutPrepareHeader(midiOut, hdr, HdrSize) != 0) { Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data); return; }
            if (midiOutLongMsg(midiOut, hdr, HdrSize) != 0) { midiOutUnprepareHeader(midiOut, hdr, HdrSize); Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data); return; }
        }
        catch (Exception) { Marshal.FreeHGlobal(hdr); Marshal.FreeHGlobal(data); return; }
        pendingSysEx.Add((hdr, data));
        SysExSent++;
        ReleaseSysEx(false);
    }

    /// <summary>다 보낸 SysEx 버퍼를 푼다. all = 장치를 닫기 전 (리셋 뒤라 모두 끝나 있다).</summary>
    private void ReleaseSysEx(bool all)
    {
        for (int i = pendingSysEx.Count - 1; i >= 0; i--)
        {
            var (hdr, data) = pendingSysEx[i];
            int r = midiOut != IntPtr.Zero ? midiOutUnprepareHeader(midiOut, hdr, HdrSize) : 0;
            if (r == MidiErrStillPlaying && !all) continue;
            Marshal.FreeHGlobal(hdr);
            Marshal.FreeHGlobal(data);
            pendingSysEx.RemoveAt(i);
        }
    }

    private bool OpenWindows()
    {
        if (midiOut != IntPtr.Zero) return true;
        try
        {
            if (midiOutOpen(out midiOut, -1, IntPtr.Zero, IntPtr.Zero, 0) != 0) { midiOut = IntPtr.Zero; return false; }   // -1 = MIDI 매퍼
            return true;
        }
        catch (Exception) { midiOut = IntPtr.Zero; return false; }   // winmm 없음 (Windows 아님)
    }

    private void Send(int status, int d1, int d2)
    {
        if (ActiveSynth == MidiSynth.Mt32) { Mt32?.Send(status, d1, d2); return; }
        if (ActiveSynth != MidiSynth.Windows || midiOut == IntPtr.Zero) return;
        if ((status & 0xF0) == 0xB0 && d1 == 7) return;   // 볼륨은 SendVolume이 비율을 곱해 보낸다
        midiOutShortMsg(midiOut, (status & 0xFF) | ((d1 & 0x7F) << 8) | ((d2 & 0x7F) << 16));
    }

    private void SendVolume(int c)
    {
        if (midiOut == IntPtr.Zero) return;
        int v = (int)Math.Round(volume[c] * Math.Min(1.0, options.Gain));
        midiOutShortMsg(midiOut, (0xB0 | c) | (7 << 8) | (Math.Max(0, Math.Min(127, v)) << 16));
    }

    private void SendVolumes()
    {
        lastGain = options.Gain;
        for (int c = 0; c < 16; c++) SendVolume(c);
    }

    private void ClearSent(int c) { for (int n = 0; n < 128; n++) sentNote[c, n] = -1; }

    private void SendAllOff()
    {
        for (int c = 0; c < 16; c++) ClearSent(c);
        if (ActiveSynth == MidiSynth.Mt32) Mt32?.AllOff();
        if (midiOut == IntPtr.Zero) return;
        for (int c = 0; c < 16; c++) { midiOutShortMsg(midiOut, (0xB0 | c) | (64 << 8)); midiOutShortMsg(midiOut, (0xB0 | c) | (123 << 8)); }
    }

    public void Dispose()
    {
        if (midiOut == IntPtr.Zero) return;
        try { midiOutReset(midiOut); ReleaseSysEx(true); midiOutClose(midiOut); } catch (Exception) { }
        midiOut = IntPtr.Zero;
    }
}
