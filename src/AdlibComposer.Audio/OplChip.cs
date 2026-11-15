using System;
using AdlibComposer.Nuked;

namespace AdlibComposer.Audio;

/// <summary>OPL 에뮬레이터 코어.</summary>
public enum OplCore
{
    /// <summary>내장 간이 에뮬레이터 (<see cref="Opl2Emulator"/> / <see cref="Opl3Emulator"/>). 가볍다.</summary>
    Builtin,
    /// <summary>Nuked OPL3 (사이클 정밀, 실제 칩과 거의 같은 소리). CPU를 더 쓴다.</summary>
    Nuked,
}

/// <summary>
/// 코어를 바꿔 끼울 수 있는 OPL 칩. 프로그램의 모든 칩(ROL/IMS 신디사이저, SOP, S3M AdLib 채널)이 이것을 쓰고,
/// <see cref="GlobalCore"/>를 바꾸면 다음 쓰기 / 샘플부터 모두 그 코어로 바뀐다.
/// 바꿀 때는 지금까지 쓴 레지스터를 새 코어에 다시 써서 악기 · 음높이를 이어 간다 (울리던 음은 처음부터 다시 울린다).
/// 쓰기 · 샘플은 오디오 엔진 락 안에서만 불리므로 바꾸는 것도 그 안에서 일어난다.
/// </summary>
public sealed class OplChip : IOplChip, ISoundChip
{
    private static volatile OplCore globalCore = OplCore.Builtin;

    /// <summary>모든 칩이 쓸 코어 (설정 → '에뮬레이터'). 어느 스레드에서 바꿔도 된다.</summary>
    public static OplCore GlobalCore { get { return globalCore; } set { globalCore = value; } }

    /// <summary>
    /// Nuked 출력에 곱해 내장 코어와 크기를 맞추는 값 (demo.rol RMS 기준). 같은 MasterGain에서 같은 크기로 들리게 한다.
    /// </summary>
    public const double NukedLevel = 1.15;

    private readonly bool opl3;
    private readonly IOplChip builtin;
    private NukedOpl3 nuked;
    private OplCore core = OplCore.Builtin;
    private readonly byte[] regs = new byte[512];
    private readonly bool[] written = new bool[512];
    private double feedbackScale = 1.0;

    /// <param name="opl3">true = YMF262 (레지스터 512개, 스테레오), false = YM3812.</param>
    public OplChip(bool opl3)
    {
        this.opl3 = opl3;
        builtin = opl3 ? new Opl3Emulator() : new Opl2Emulator();
        SyncCore();
    }

    /// <summary>지금 소리를 내는 코어.</summary>
    public OplCore Core { get { return core; } }

    public int NativeRate { get { return core == OplCore.Nuked ? NukedOpl3.NativeRate : builtin.NativeRate; } }

    /// <summary>출력 이득 (두 코어에 같이).</summary>
    public double MasterGain
    {
        get { return opl3 ? ((Opl3Emulator)builtin).MasterGain : ((Opl2Emulator)builtin).MasterGain; }
        set { if (opl3) ((Opl3Emulator)builtin).MasterGain = value; else ((Opl2Emulator)builtin).MasterGain = value; }
    }

    public double FeedbackScale
    {
        get { return feedbackScale; }
        set
        {
            feedbackScale = value;
            builtin.FeedbackScale = value;
            if (nuked != null) nuked.FeedbackScale = value;
        }
    }

    public void Reset()
    {
        SyncCore();
        Array.Clear(regs, 0, regs.Length);
        Array.Clear(written, 0, written.Length);
        Array.Clear(mute, 0, mute.Length);
        Array.Clear(pitch, 0, pitch.Length);
        adjusted = false;
        builtin.Reset();
        if (nuked != null) nuked.Reset();
    }

    /// <summary>레지스터 쓰기를 엿듣는 쪽 (VGM 내보내기). 코어를 바꿀 때 다시 쓰는 것은 알리지 않는다.</summary>
    public Action<int, int> WriteHook { get; set; }

    public void Write(int register, int value)
    {
        SyncCore();
        register &= opl3 ? 0x1FF : 0xFF;
        WriteHook?.Invoke(register, value & 0xFF);
        regs[register] = (byte)value;
        written[register] = true;
        int lo = register & 0xFF;
        if (adjusted && ((lo >= 0xA0 && lo <= 0xA8) || (lo >= 0xB0 && lo <= 0xB8))) { WriteFreq(register >> 8, lo & 0x0F); return; }
        CoreWrite(register, value);
    }

    private void CoreWrite(int register, int value)
    {
        if (core == OplCore.Nuked) nuked.WriteReg(register, value);
        else builtin.Write(register, value);
    }

    // ------------------------------------------------------------ ISoundChip (채널 = 뱅크 × 9 + 채널)

    private readonly bool[] mute = new bool[18];
    private readonly double[] pitch = new double[18];
    private bool adjusted;

    public string Name { get { return opl3 ? "YMF262 (OPL3)" : "YM3812 (OPL2)"; } }
    public int ChannelCount { get { return opl3 ? 18 : 9; } }
    public string ChannelLabel(int ch) { return "F" + (ch + 1); }
    void ISoundChip.Write(int port, int reg, int value) { Write(((port & 1) << 8) | (reg & 0xFF), value); }
    void ISoundChip.Generate(out int left, out int right) { GenerateStereo(out left, out right); }

    public void SetMute(int ch, bool on)
    {
        if (ch < 0 || ch >= ChannelCount || mute[ch] == on) return;
        mute[ch] = on;
        RefreshAdjust(ch);
    }

    public void SetPitch(int ch, double ratio)
    {
        if (ch < 0 || ch >= ChannelCount) return;
        if (ratio <= 0) ratio = 1.0;
        if (pitch[ch] == ratio || (pitch[ch] == 0 && ratio == 1.0)) return;
        pitch[ch] = ratio;
        RefreshAdjust(ch);
    }

    private void RefreshAdjust(int ch)
    {
        adjusted = false;
        for (int i = 0; i < 18; i++) if (mute[i] || (pitch[i] != 0 && pitch[i] != 1.0)) adjusted = true;
        WriteFreq(ch / 9, ch % 9);
    }

    /// <summary>원래 A0 · B0 값에 음높이 배수 · 끄기를 적용해 코어에 쓴다.</summary>
    private void WriteFreq(int bank, int ch)
    {
        int b = bank << 8, v = bank * 9 + ch;
        int fnum = regs[b + 0xA0 + ch] | ((regs[b + 0xB0 + ch] & 3) << 8);
        int block = (regs[b + 0xB0 + ch] >> 2) & 7;
        int key = regs[b + 0xB0 + ch] & 0x20;
        double r = pitch[v] == 0 ? 1.0 : pitch[v];
        if (r != 1.0 && fnum > 0)
        {
            double f = fnum * r;
            while (f >= 1023.5 && block < 7) { f /= 2; block++; }
            while (f < 256 && block > 0) { f *= 2; block--; }
            fnum = SoundChips.Clamp((int)Math.Round(f), 1, 1023);
        }
        if (mute[v]) key = 0;
        CoreWrite(b + 0xA0 + ch, fnum & 0xFF);
        CoreWrite(b + 0xB0 + ch, key | (block << 2) | (fnum >> 8));
    }

    public double ChannelFrequency(int ch)
    {
        if (ch < 0 || ch >= ChannelCount) return 0;
        int b = (ch / 9) << 8, c = ch % 9;
        if ((regs[b + 0xB0 + c] & 0x20) == 0) return 0;
        int fnum = regs[b + 0xA0 + c] | ((regs[b + 0xB0 + c] & 3) << 8);
        int block = (regs[b + 0xB0 + c] >> 2) & 7;
        return fnum * 49716.0 / Math.Pow(2, 20 - block);
    }

    public float ChannelLevel(int ch)
    {
        if (ch < 0 || ch >= ChannelCount) return 0;
        int b = (ch / 9) << 8, c = ch % 9;
        int car = (c / 3) * 8 + c % 3 + 3;
        return 1f - (regs[b + 0x40 + car] & 0x3F) / 63f;
    }

    public bool ChannelActive(int ch)
    {
        if (ch < 6 || ch > 8 || (regs[0xBD] & 0x20) == 0) return false;
        int bd = regs[0xBD];
        return ch == 6 ? (bd & 0x10) != 0 : ch == 7 ? (bd & 0x09) != 0 : (bd & 0x06) != 0;
    }

    /// <summary>모노 샘플 1개 (스테레오 코어는 좌우 평균).</summary>
    public short Generate()
    {
        SyncCore();
        if (core != OplCore.Nuked) return builtin.Generate();
        nuked.Generate(out short l, out short r);
        return Scale((l + r) * 0.5);
    }

    /// <summary>스테레오 샘플 1개 (16bit 범위).</summary>
    public void GenerateStereo(out int left, out int right)
    {
        SyncCore();
        if (core != OplCore.Nuked)
        {
            if (opl3) { ((Opl3Emulator)builtin).GenerateStereo(out left, out right); return; }
            left = right = builtin.Generate();
            return;
        }
        nuked.Generate(out short l, out short r);
        left = Scale(l);
        right = Scale(r);
    }

    private short Scale(double v)
    {
        v *= NukedLevel * MasterGain;
        if (v > 32767) v = 32767; else if (v < -32768) v = -32768;
        return (short)v;
    }

    // ------------------------------------------------------------ 코어 바꾸기

    private void SyncCore()
    {
        var want = globalCore;
        if (want == core) return;
        core = want;
        if (want == OplCore.Nuked)
        {
            if (nuked == null) nuked = new NukedOpl3();
            nuked.FeedbackScale = feedbackScale;
            nuked.Reset();
            Replay((r, v) => nuked.WriteReg(r, v));
        }
        else
        {
            builtin.Reset();
            Replay((r, v) => builtin.Write(r, v));
        }
        if (adjusted) for (int ch = 0; ch < ChannelCount; ch++) WriteFreq(ch / 9, ch % 9);
    }

    /// <summary>
    /// 쓴 레지스터를 새 코어에 다시 쓴다: 모드(0x105 NEW, 0x104 4-op, 0x01, 0x08) → 오퍼레이터 → 주파수 / 연결 → 키(B0) → 리듬(BD).
    /// </summary>
    private void Replay(Action<int, int> write)
    {
        void W(int r) { if (written[r]) write(r, regs[r]); }
        if (opl3) { W(0x105); W(0x104); }
        W(0x01); W(0x08);
        int banks = opl3 ? 2 : 1;
        for (int b = 0; b < banks; b++)
        {
            int o = b << 8;
            foreach (int baseReg in new[] { 0x20, 0x40, 0x60, 0x80, 0xE0 })
                for (int k = 0; k < 0x16; k++) W(o + baseReg + k);
            for (int ch = 0; ch < 9; ch++) { W(o + 0xA0 + ch); W(o + 0xC0 + ch); }
            for (int ch = 0; ch < 9; ch++) W(o + 0xB0 + ch);
        }
        W(0xBD);
    }
}
