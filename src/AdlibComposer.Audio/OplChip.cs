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
public sealed class OplChip : IOplChip
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
        builtin.Reset();
        if (nuked != null) nuked.Reset();
    }

    public void Write(int register, int value)
    {
        SyncCore();
        register &= opl3 ? 0x1FF : 0xFF;
        regs[register] = (byte)value;
        written[register] = true;
        if (core == OplCore.Nuked) nuked.WriteReg(register, value);
        else builtin.Write(register, value);
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
