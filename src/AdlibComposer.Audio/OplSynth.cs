using System;
using AdlibComposer.Core;

namespace AdlibComposer.Audio;

/// <summary>
/// 보이스 단위 신디사이저 (Visual Composer의 보이스 1~9 / 리듬 모드 1~6,BD,SD,TO,CY,HH).
/// 악기 → OPL 레지스터 변환, 음높이(F-Number/Block) 계산, 퍼커션 규칙을 담당한다.
/// </summary>
public sealed class OplSynth
{
    public const int VoiceCount = 11;
    private static readonly int[] ModOff = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };
    private static readonly int[] CarOff = { 3, 4, 5, 11, 12, 13, 19, 20, 21 };

    private readonly IOplChip chip;
    private readonly AdlibInstrument[] inst = new AdlibInstrument[VoiceCount];
    private readonly double[] volume = new double[VoiceCount];
    private readonly double[] bend = new double[VoiceCount];
    private readonly int[] note = new int[VoiceCount];
    private readonly bool[] keyOn = new bool[9];
    private readonly int[] chFnum = new int[9];
    private readonly int[] chBlock = new int[9];
    private readonly AdlibInstrument fallback = AdlibInstrument.CreateDefault("DEFAULT");
    private int drumBits;
    private int tomNote = 36;   // adlib.c TOM_PITCH 24 (칩 노트) = ROL 36

    public OplMapping Mapping { get; } = new OplMapping();
    public bool RhythmMode { get; private set; }
    public bool DeepTremolo { get; set; }
    public bool DeepVibrato { get; set; }
    public IOplChip Chip { get { return chip; } }

    public OplSynth() : this(new Opl2Emulator()) { }

    public OplSynth(IOplChip chip)
    {
        this.chip = chip;
        Reset();
    }

    public int NativeRate { get { return chip.NativeRate; } }

    public void Reset()
    {
        chip.Reset();
        for (int i = 0; i < VoiceCount; i++) { volume[i] = 1.0; bend[i] = 1.0; note[i] = -1; }
        for (int c = 0; c < 9; c++) { keyOn[c] = false; chFnum[c] = 0; chBlock[c] = 0; }
        drumBits = 0;
        chip.Write(0x01, 0x20);
        chip.Write(0x08, 0x00);
        WriteBd();
        for (int v = 0; v < VoiceCount; v++) if (inst[v] != null) LoadVoice(v);
    }

    public void SetRhythmMode(bool on)
    {
        if (RhythmMode == on && drumBits == 0) { RhythmMode = on; WriteBd(); ReloadAll(); return; }
        AllNotesOff();
        RhythmMode = on;
        drumBits = 0;
        WriteBd();
        if (on)
        {
            SetChannelPitch(7, tomNote + 7);
            SetChannelPitch(8, tomNote);
        }
        ReloadAll();
    }

    private void ReloadAll()
    {
        for (int v = 0; v < VoiceCount; v++) if (IsValid(v)) LoadVoice(v);
    }

    private bool IsValid(int voice) { return voice >= 0 && voice < (RhythmMode ? 11 : 9); }
    private bool IsMelodicSlot(int voice) { return !RhythmMode || voice < 6; }

    private void WriteBd()
    {
        chip.Write(0xBD, (RhythmMode ? 0x20 : 0) | drumBits | (DeepTremolo ? 0x80 : 0) | (DeepVibrato ? 0x40 : 0));
    }

    // ------------------------------------------------------------ 악기 / 볼륨 / 피치

    public void SetInstrument(int voice, AdlibInstrument ins)
    {
        if (voice < 0 || voice >= VoiceCount) return;
        inst[voice] = ins;
        if (IsValid(voice)) LoadVoice(voice);
    }

    public AdlibInstrument GetInstrument(int voice) { return inst[voice]; }

    public void SetVolume(int voice, double v)
    {
        if (voice < 0 || voice >= VoiceCount) return;
        volume[voice] = Calc.Clamp(v, 0.0, 1.0);
        if (IsValid(voice)) LoadVoice(voice);
    }

    public void SetPitchBend(int voice, double b)
    {
        if (voice < 0 || voice >= VoiceCount) return;
        bend[voice] = Calc.Clamp(b, 0.0, 2.0);
        if (!IsValid(voice) || note[voice] < 0) return;
        if (IsMelodicSlot(voice) || voice == 6) SetChannelPitch(voice, note[voice] + BendSemis(voice));
        else if (voice == 8) { SetChannelPitch(8, note[8] + BendSemis(8)); SetChannelPitch(7, note[8] + 7 + BendSemis(8)); }
    }

    private double BendSemis(int voice) { return (bend[voice] - 1.0) * Mapping.PitchBendSemitones; }

    private void LoadVoice(int voice)
    {
        var ins = inst[voice] ?? fallback;
        double vol = volume[voice];
        if (IsMelodicSlot(voice) || voice == 6)
        {
            int ch = voice;
            int tlMod = ins.Additive ? Mapping.TlRegister(ins.Modulator.TotalLevel, vol) : Mapping.TlRegister(ins.Modulator.TotalLevel, 1.0);
            int tlCar = Mapping.TlRegister(ins.Carrier.TotalLevel, vol);
            WriteOperator(ModOff[ch], ins.Modulator, tlMod);
            WriteOperator(CarOff[ch], ins.Carrier, tlCar);
            chip.Write(0xC0 + ch, ((ins.Modulator.Feedback & 7) << 1) | (ins.Additive ? 1 : 0));
            return;
        }
        // 단일 오퍼레이터 퍼커션: 파일의 모듈레이터 값을 해당 OPL 오퍼레이터에 쓴다.
        int off;
        switch (voice)
        {
            case 7: off = 0x14; break;   // 스네어  = 채널7 캐리어
            case 8: off = 0x12; break;   // 탐탐    = 채널8 모듈레이터
            case 9: off = 0x15; break;   // 심벌    = 채널8 캐리어
            default: off = 0x11; break;  // 하이햇  = 채널7 모듈레이터
        }
        WriteOperator(off, ins.Modulator, Mapping.TlRegister(ins.Modulator.TotalLevel, vol));
    }

    private void WriteOperator(int off, OplOperator op, int tl)
    {
        chip.Write(0x20 + off, (op.Am != 0 ? 0x80 : 0) | (op.Vib != 0 ? 0x40 : 0) | (op.Eg != 0 ? 0x20 : 0)
                               | (op.Ksr != 0 ? 0x10 : 0) | (op.Multiple & 0x0F));
        chip.Write(0x40 + off, ((op.Ksl & 3) << 6) | (tl & 0x3F));
        chip.Write(0x60 + off, ((op.Attack & 0x0F) << 4) | (op.Decay & 0x0F));
        chip.Write(0x80 + off, ((op.Sustain & 0x0F) << 4) | (op.ReleaseRate & 0x0F));
        chip.Write(0xE0 + off, op.WaveSelect & 3);
    }

    // ------------------------------------------------------------ 노트

    public void NoteOn(int voice, int n)
    {
        if (!IsValid(voice)) return;
        note[voice] = n;
        if (IsMelodicSlot(voice))
        {
            SetChannelPitch(voice, n + BendSemis(voice), false);
            if (keyOn[voice]) { keyOn[voice] = false; WriteFreq(voice); }
            keyOn[voice] = true;
            WriteFreq(voice);
            return;
        }
        switch (voice)
        {
            case 6: SetChannelPitch(6, n + BendSemis(6)); TriggerDrum(0x10); break;   // 드럼 채널은 키온 비트가 없으므로 주파수를 바로 쓴다
            case 7: TriggerDrum(0x08); break;
            case 8:
                tomNote = n;
                SetChannelPitch(8, n + BendSemis(8));
                SetChannelPitch(7, n + 7 + BendSemis(8));
                TriggerDrum(0x04);
                break;
            case 9: TriggerDrum(0x02); break;
            case 10: TriggerDrum(0x01); break;
        }
    }

    public void NoteOff(int voice)
    {
        if (!IsValid(voice)) return;
        note[voice] = -1;
        if (IsMelodicSlot(voice)) { keyOn[voice] = false; WriteFreq(voice); return; }
        int bit = voice == 6 ? 0x10 : voice == 7 ? 0x08 : voice == 8 ? 0x04 : voice == 9 ? 0x02 : 0x01;
        drumBits &= ~bit;
        WriteBd();
    }

    public void AllNotesOff()
    {
        for (int v = 0; v < VoiceCount; v++) note[v] = -1;
        for (int c = 0; c < 9; c++) { if (keyOn[c]) { keyOn[c] = false; WriteFreq(c); } }
        drumBits = 0;
        WriteBd();
    }

    private void TriggerDrum(int bit)
    {
        if ((drumBits & bit) != 0) { drumBits &= ~bit; WriteBd(); }
        drumBits |= bit;
        WriteBd();
    }

    // ------------------------------------------------------------ 주파수

    private void SetChannelPitch(int ch, double semitones, bool write = true)
    {
        double freq = NoteHelper.Frequency(semitones);
        int block = 0, fnum = 1023;
        double rate = chip.NativeRate;
        for (block = 0; block < 8; block++)
        {
            fnum = (int)Math.Round(freq * (1 << (20 - block)) / rate);
            if (fnum <= 1023) break;
        }
        if (block > 7) block = 7;
        chFnum[ch] = Calc.Clamp(fnum, 0, 1023);
        chBlock[ch] = block;
        if (write) WriteFreq(ch);
    }

    private void WriteFreq(int ch)
    {
        chip.Write(0xA0 + ch, chFnum[ch] & 0xFF);
        chip.Write(0xB0 + ch, (keyOn[ch] && IsMelodicSlot(ch) ? 0x20 : 0) | (chBlock[ch] << 2) | (chFnum[ch] >> 8));
    }

    public short Generate() { return chip.Generate(); }
}
