using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// OKI 4비트 ADPCM 한 줄기 (MSM6258 · MSM5205 공통): 바깥(CPU · DMA)이 니블을 칩 속도에 맞춰 넣으면 하나씩 풀어 낸다.
/// 풀기는 MSM6295와 같은 방식 (단계 49칸 = floor(16 × 1.1^n), 12비트 신호).
/// <para>넣는 쪽 시간이 칩 속도와 조금 어긋나도 끊기지 않게 작은 대기열(<see cref="QueueSize"/> 니블)을 둔다.
/// 비면 마지막 값을 천천히 0으로 줄인다 (딱 소리 없이). 가득 차면 (빨리 감기 등) 가장 오래된 니블을 풀어 버린다 (ADPCM 상태가 어긋나지 않게).</para>
/// </summary>
public abstract class OkiAdpcmStream : ISoundChip
{
    public const int QueueSize = 16;
    private static readonly int[] StepSize = BuildSteps();
    private static readonly int[] IndexShift = { -1, -1, -1, -1, 2, 4, 6, 8 };

    private static int[] BuildSteps()
    {
        var t = new int[49];
        for (int i = 0; i < 49; i++) t[i] = (int)Math.Floor(16.0 * Math.Pow(11.0 / 10.0, i));
        return t;
    }

    private readonly int[] queue = new int[QueueSize];
    private int qHead, qCount;
    protected int Signal, Step;
    private int prev;
    private double pos, hold = 1;
    private bool mute;
    protected readonly int OutRate;
    /// <summary>칩이 소리를 내는 중 (재생 명령 · 리셋 풀림).</summary>
    protected bool Running;
    /// <summary>12비트 신호에서 아래 비트를 버린다 (MSM6258 10비트 출력 = 2).</summary>
    protected int DropBits;
    /// <summary>왼쪽 · 오른쪽 끄기 (MSM6258 팬).</summary>
    protected bool LeftOff, RightOff;
    /// <summary>표시: 최근 출력 크기.</summary>
    private double level;

    protected OkiAdpcmStream(int rate) { OutRate = rate > 0 ? rate : 44100; NativeRate = OutRate; }

    public abstract string Name { get; }
    public int NativeRate { get; }
    public int ChannelCount { get { return 1; } }
    public virtual string ChannelLabel(int ch) { return "PCM"; }
    /// <summary>칩 속도 (니블 / 초).</summary>
    public abstract double ChipRate { get; }

    public virtual void Reset()
    {
        qHead = qCount = 0;
        Signal = prev = 0; Step = 0; pos = 0; hold = 1; level = 0;
        Running = false;
    }

    public abstract void Write(int port, int reg, int value);

    /// <summary>니블 하나를 대기열에 (가득 차면 가장 오래된 것을 버린다).</summary>
    protected void PushNibble(int n)
    {
        if (qCount == QueueSize) Decode();   // 가득 참 (빨리 감기 등): 가장 오래된 것을 풀어 신호 · 단계를 맞춰 둔다
        queue[(qHead + qCount) % QueueSize] = n & 15;
        qCount++;
    }

    protected void ClearQueue() { qHead = qCount = 0; }

    /// <summary>재생을 처음부터 (신호 · 단계 0).</summary>
    protected void Restart() { Signal = -2; prev = -2; Step = 0; pos = 0; hold = 1; }

    private void Decode()
    {
        prev = Signal;
        if (qCount == 0) { hold *= 0.995; return; }   // 데이터 없음: 그대로 두고 줄여 간다
        int n = queue[qHead]; qHead = (qHead + 1) % QueueSize; qCount--;
        hold = 1;
        int ss = StepSize[Step];
        int diff = ss >> 3;
        if ((n & 1) != 0) diff += ss >> 2;
        if ((n & 2) != 0) diff += ss >> 1;
        if ((n & 4) != 0) diff += ss;
        Signal += (n & 8) != 0 ? -diff : diff;
        Signal = Math.Max(-2048, Math.Min(2047, Signal));
        Step = Math.Max(0, Math.Min(48, Step + IndexShift[n & 7]));
    }

    public void Generate(out int left, out int right)
    {
        left = right = 0;
        if (!Running) { level *= 0.99; return; }
        pos += ChipRate / OutRate;
        while (pos >= 1) { pos -= 1; Decode(); }
        int a = (prev >> DropBits) << DropBits, b = (Signal >> DropBits) << DropBits;
        double v = (a + (b - a) * pos) * hold * 8;
        level = Math.Max(level * 0.999, Math.Abs(v) / 16384.0);
        if (mute) return;
        int o = SoundChips.Clip(v);
        left = LeftOff ? 0 : o;
        right = RightOff ? 0 : o;
    }

    public void SetMute(int ch, bool m) { if (ch == 0) mute = m; }
    /// <summary>바깥이 칩 속도에 맞춰 데이터를 넣으므로 음높이는 바꾸지 않는다.</summary>
    public void SetPitch(int ch, double ratio) { }
    public double ChannelFrequency(int ch) { return 0; }
    public float ChannelLevel(int ch) { return ch == 0 ? (float)Math.Min(1, level) : 0; }
    public bool ChannelActive(int ch) { return ch == 0 && Running && (qCount > 0 || level > 0.01); }

    /// <summary>들어 있는 니블 수 (시험용).</summary>
    public int Queued { get { return qCount; } }
}

/// <summary>
/// OKI MSM6258: X68000의 ADPCM (1채널). 속도 = 클럭 / 1024 · 768 · 512 (X68000 = 4MHz → 3.9 · 5.2 · 7.8kHz).
/// 데이터 바이트 하나 = 니블 둘 (아래 니블 먼저). 출력 10비트 또는 12비트.
/// <para>VGM 0xB7 aa dd: 0x00 명령 (bit0 멈춤, bit1 재생), 0x01 데이터, 0x02 팬 (bit0 = 오른쪽 끔, bit1 = 왼쪽 끔),
/// 0x08~0x0B 클럭 바이트, 0x0C 분주 (0 = 1024, 1 = 768, 2 · 3 = 512). 머리말 0x94 깃발: bit0~1 분주, bit3 = 12비트 출력.</para>
/// </summary>
public sealed class Msm6258 : OkiAdpcmStream
{
    public const double DefaultClock = 4000000;
    private static readonly int[] Dividers = { 1024, 768, 512, 512 };
    private double clock;
    private int divider;
    private readonly uint clockBytes;
    private uint newClock;
    private readonly int flags;

    /// <param name="flags">VGM 머리말 0x94 (bit0~1 분주, bit3 12비트 출력).</param>
    public Msm6258(double clock = DefaultClock, int flags = 0, int rate = 44100) : base(rate)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        clockBytes = (uint)this.clock;
        this.flags = flags;
        Reset();
    }

    public override string Name { get { return "OKI MSM6258 (ADPCM)"; } }
    public override string ChannelLabel(int ch) { return "ADPCM"; }
    public override double ChipRate { get { return clock / Dividers[divider & 3]; } }
    public double Clock { get { return clock; } }

    public override void Reset()
    {
        base.Reset();
        divider = flags & 3;
        DropBits = (flags & 0x08) != 0 ? 0 : 2;
        LeftOff = RightOff = false;
        newClock = clockBytes;
    }

    public override void Write(int port, int reg, int v)
    {
        v &= 0xFF;
        switch (reg)
        {
            case 0x00:
                if ((v & 1) != 0) { Running = false; ClearQueue(); break; }
                if ((v & 2) != 0) { if (!Running) { Running = true; Restart(); } }
                else Running = false;
                break;
            case 0x01: PushNibble(v); PushNibble(v >> 4); break;
            case 0x02: RightOff = (v & 1) != 0; LeftOff = (v & 2) != 0; break;
            case 0x08: case 0x09: case 0x0A: case 0x0B:
                {
                    int sh = (reg - 0x08) * 8;
                    newClock = (newClock & ~(0xFFu << sh)) | ((uint)v << sh);
                    if (reg == 0x0B && newClock > 0) clock = newClock;
                    break;
                }
            case 0x0C: divider = v & 3; break;
        }
    }
}

/// <summary>
/// OKI MSM5205: 아케이드 ADPCM (1채널). 클럭 384kHz 기준 속도 = 클럭 / 96 · 48 · 64 (4 · 8 · 6kHz), 바깥이 VCK마다 니블 하나를 넣는다.
/// <para>쓰기: reg 0 = 니블 데이터, reg 1 = 속도 고르기 (S1 · S2: 0 = /96, 1 = /48, 2 = /64, 3 = 멈춤), reg 2 = 리셋 (1 = 멈추고 비움, 0 = 풀기).
/// 3비트 ADPCM은 아직 없음 (4비트만).</para>
/// </summary>
public sealed class Msm5205 : OkiAdpcmStream
{
    public const double DefaultClock = 384000;
    private static readonly int[] Prescaler = { 96, 48, 64, 0 };
    private readonly double clock;
    private int select = 1;

    public Msm5205(double clock = DefaultClock, int rate = 44100) : base(rate)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        Reset();
    }

    public override string Name { get { return "OKI MSM5205 (ADPCM)"; } }
    public override double ChipRate { get { int p = Prescaler[select & 3]; return p > 0 ? clock / p : 0; } }

    public override void Reset()
    {
        base.Reset();
        DropBits = 0;
        Running = true;
        Restart();
    }

    public override void Write(int port, int reg, int v)
    {
        switch (reg)
        {
            case 0: PushNibble(v); break;
            case 1: select = v & 3; Running = select != 3; break;
            case 2:
                if ((v & 1) != 0) { Running = false; ClearQueue(); }
                else if (!Running && (select & 3) != 3) { Running = true; Restart(); }
                break;
        }
    }
}
