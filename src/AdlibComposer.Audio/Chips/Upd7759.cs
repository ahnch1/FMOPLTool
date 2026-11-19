using System;
using System.Collections.Generic;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// NEC uPD7759 ADPCM 음성 칩 (세가 System 16 · 18의 목소리 · 효과음), 슬레이브 모드 (자기 ROM 없이 CPU가 바이트를 하나씩 넣어 줌).
/// MAME upd7759.cpp의 상태 기계를 따른다: START → (35클럭) 첫 요청 → 마지막 곡 번호 · 빈 바이트 · 주소 두 바이트 · 빈 바이트 → 블록 머리
/// (00 = 쉼 · 40 = 256니블 · 80 = n니블 · C0 = 되풀이) → 니블. 바이트를 받을 때마다 DRQ를 올린다 (21클럭).
/// <para><see cref="Queued"/> = false (보드 흉내): 칩은 마지막으로 쓴 바이트를 읽는다 — DRQ 시점에 CPU가 다음 바이트를 쓴다.
/// true (VGM 재생기): 쓴 바이트를 차례로 쌓아 두고 DRQ마다 하나씩 꺼내 둔다 (CPU는 DRQ마다 꼭 한 바이트를 쓰므로, 기록 시각이 거칠어도 짝이 맞는다).</para>
/// </summary>
public sealed class Upd7759Core
{
    public const double StandardClock = 640000;

    private static readonly int[,] Step =
    {
        { 0,  0,  1,  2,  3,   5,   7,  10,  0,   0,  -1,  -2,  -3,   -5,   -7,  -10 },
        { 0,  1,  2,  3,  4,   6,   8,  13,  0,  -1,  -2,  -3,  -4,   -6,   -8,  -13 },
        { 0,  1,  2,  4,  5,   7,  10,  15,  0,  -1,  -2,  -4,  -5,   -7,  -10,  -15 },
        { 0,  1,  3,  4,  6,   9,  13,  19,  0,  -1,  -3,  -4,  -6,   -9,  -13,  -19 },
        { 0,  2,  3,  5,  8,  11,  15,  23,  0,  -2,  -3,  -5,  -8,  -11,  -15,  -23 },
        { 0,  2,  4,  7, 10,  14,  19,  29,  0,  -2,  -4,  -7, -10,  -14,  -19,  -29 },
        { 0,  3,  5,  8, 12,  16,  22,  33,  0,  -3,  -5,  -8, -12,  -16,  -22,  -33 },
        { 1,  4,  7, 10, 15,  20,  29,  43, -1,  -4,  -7, -10, -15,  -20,  -29,  -43 },
        { 1,  4,  8, 13, 18,  25,  35,  53, -1,  -4,  -8, -13, -18,  -25,  -35,  -53 },
        { 1,  6, 10, 16, 22,  31,  43,  64, -1,  -6, -10, -16, -22,  -31,  -43,  -64 },
        { 2,  7, 12, 19, 27,  37,  51,  76, -2,  -7, -12, -19, -27,  -37,  -51,  -76 },
        { 2,  9, 16, 24, 34,  46,  64,  96, -2,  -9, -16, -24, -34,  -46,  -64,  -96 },
        { 3, 11, 19, 29, 41,  57,  79, 117, -3, -11, -19, -29, -41,  -57,  -79, -117 },
        { 4, 13, 24, 36, 50,  69,  96, 143, -4, -13, -24, -36, -50,  -69,  -96, -143 },
        { 4, 16, 29, 44, 62,  85, 118, 175, -4, -16, -29, -44, -62,  -85, -118, -175 },
        { 6, 20, 36, 54, 76, 104, 144, 214, -6, -20, -36, -54, -76, -104, -144, -214 },
    };
    private static readonly int[] StateTable = { -1, -1, 0, 0, 1, 2, 2, 3, -1, -1, 0, 0, 1, 2, 2, 3 };

    private enum St { Idle, DropDrq, Start, FirstReq, LastSample, Dummy1, AddrMsb, AddrLsb, Dummy2, BlockHeader, NibbleCount, NibbleMsn, NibbleLsn }

    public bool Queued;
    private St state = St.Idle, postDrqState;
    private int clocksLeft, postDrqClocks;
    private bool reset = true, start;
    private int fifoIn;
    private readonly Queue<int> fifo = new Queue<int>();
    private int pendingDrqs;
    private int reqSample, lastSample, blockHeader, sampleRate = 1, nibblesLeft, adpcmData, adpcmState, repeatCount;
    private bool firstValidHeader;
    /// <summary>지금 출력 (부호 있는 값, ±256 안팎).</summary>
    public int Sample;
    /// <summary>바이트 요청 (DRQ). 보드는 이것이 올라갈 때 CPU에 NMI.</summary>
    public bool Drq;

    public bool Busy { get { return state != St.Idle; } }
    /// <summary>쌓여 있는 바이트 수 (재생기: 많이 밀리면 소리 없이 따라잡는다).</summary>
    public int Backlog { get { return fifo.Count; } }

    public void ResetChip()
    {
        state = St.Idle; clocksLeft = 0; Sample = 0; adpcmState = 0; Drq = false; repeatCount = 0; fifo.Clear(); pendingDrqs = 0;
    }

    public void SetReset(bool high)
    {
        bool old = reset;
        reset = high;
        if (old && !high) ResetChip();   // /RESET 내림 = 처음으로
    }

    public void SetStart(bool high)
    {
        bool old = start;
        start = high;
        if (state == St.Idle && !old && high && reset) { state = St.Start; clocksLeft = 0; fifo.Clear(); pendingDrqs = 0; }   // START 올라가는 가장자리 (쉬고 있고 /RESET 높을 때)
    }

    public void Data(int v)
    {
        if (!Queued) { fifoIn = v & 0xFF; return; }
        if (pendingDrqs > 0) { pendingDrqs--; fifoIn = v & 0xFF; }   // 이미 지난 DRQ의 바이트
        else fifo.Enqueue(v & 0xFF);
    }

    /// <summary>DRQ를 올림: 쌓아 두는 방식이면 그 DRQ의 바이트를 꺼내 둔다.</summary>
    private void RaiseDrq()
    {
        Drq = true;
        if (!Queued) return;
        if (fifo.Count > 0) fifoIn = fifo.Dequeue(); else pendingDrqs++;
    }

    private int ReadByte() { return fifoIn; }

    /// <summary>칩 클럭 cycles만큼 진행.</summary>
    public void Advance(int cycles)
    {
        while (cycles > 0)
        {
            if (state == St.Idle) { Drq = false; return; }
            if (clocksLeft > cycles) { clocksLeft -= cycles; return; }
            cycles -= Math.Max(0, clocksLeft);
            clocksLeft = 0;
            AdvanceState();
            if (clocksLeft <= 0) clocksLeft = 1;
        }
    }

    private void AdvanceState()
    {
        switch (state)
        {
            case St.Idle: clocksLeft = 4; break;
            case St.DropDrq:
                Drq = false;
                clocksLeft = postDrqClocks;
                state = postDrqState;
                break;
            case St.Start:
                reqSample = 0x10;   // 슬레이브 모드
                clocksLeft = 70;
                state = St.FirstReq;
                break;
            case St.FirstReq:
                Drq = true;
                clocksLeft = 44;
                state = St.LastSample;
                break;
            case St.LastSample:
                lastSample = ReadByte();
                Drq = true;
                clocksLeft = 28;
                state = reqSample > lastSample ? St.Idle : St.Dummy1;
                break;
            case St.Dummy1:
                Drq = true;
                clocksLeft = 32;
                state = St.AddrMsb;
                break;
            case St.AddrMsb:
                ReadByte();
                Drq = true;
                clocksLeft = 44;
                state = St.AddrLsb;
                break;
            case St.AddrLsb:
                ReadByte();
                Drq = true;
                clocksLeft = 36;
                state = St.Dummy2;
                break;
            case St.Dummy2:
                firstValidHeader = false;
                Drq = true;
                clocksLeft = 36;
                state = St.BlockHeader;
                break;
            case St.BlockHeader:
                if (repeatCount > 0) repeatCount--;   // 슬레이브 모드에서는 CPU가 같은 바이트를 다시 넣는다
                blockHeader = ReadByte();
                Drq = true;
                switch (blockHeader & 0xC0)
                {
                    case 0x00:
                        clocksLeft = 1024 * ((blockHeader & 0x3F) + 1);
                        state = blockHeader == 0 && firstValidHeader ? St.Idle : St.BlockHeader;
                        Sample = 0; adpcmState = 0;
                        break;
                    case 0x40:
                        sampleRate = (blockHeader & 0x3F) + 1;
                        nibblesLeft = 256;
                        clocksLeft = 36;
                        state = St.NibbleMsn;
                        break;
                    case 0x80:
                        sampleRate = (blockHeader & 0x3F) + 1;
                        clocksLeft = 36;
                        state = St.NibbleCount;
                        break;
                    default:
                        repeatCount = (blockHeader & 7) + 1;
                        clocksLeft = 36;
                        state = St.BlockHeader;
                        break;
                }
                if (blockHeader != 0) firstValidHeader = true;
                break;
            case St.NibbleCount:
                nibblesLeft = ReadByte() + 1;
                Drq = true;
                clocksLeft = 36;
                state = St.NibbleMsn;
                break;
            case St.NibbleMsn:
                adpcmData = ReadByte();
                Update(adpcmData >> 4);
                Drq = true;
                clocksLeft = sampleRate * 4;
                state = --nibblesLeft == 0 ? St.BlockHeader : St.NibbleLsn;
                break;
            case St.NibbleLsn:
                Update(adpcmData & 15);
                clocksLeft = sampleRate * 4;
                state = --nibblesLeft == 0 ? St.BlockHeader : St.NibbleMsn;
                break;
        }
        if (Drq && state != St.DropDrq)
        {
            if (Queued) { Drq = false; RaiseDrq(); }
            postDrqState = state;
            postDrqClocks = clocksLeft - 21;
            state = St.DropDrq;
            clocksLeft = 21;
        }
    }

    private void Update(int data)
    {
        Sample += Step[adpcmState, data];
        if (Sample > 255) Sample = 255; else if (Sample < -256) Sample = -256;   // 9비트 DAC
        adpcmState += StateTable[data];
        if (adpcmState < 0) adpcmState = 0; else if (adpcmState > 15) adpcmState = 15;
    }
}

/// <summary>uPD7759 VGM 칩 (명령 B6h: 레지스터 0 = /RESET, 1 = START, 2 = 데이터, 3 = 뱅크 — 슬레이브 모드만, 쓴 바이트를 차례로).</summary>
public sealed class Upd7759 : ISoundChip
{
    private readonly Upd7759Core core = new Upd7759Core { Queued = true };
    private readonly double clocksPerSample;
    private double acc;
    private bool mute;
    private float level;

    public Upd7759(double clock, int rate = 44100)
    {
        NativeRate = rate;
        clocksPerSample = (clock > 0 ? clock : Upd7759Core.StandardClock) / rate;
    }

    public string Name { get { return "NEC uPD7759 (ADPCM)"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 1; } }
    public string ChannelLabel(int ch) { return "PCM"; }
    public void Reset() { core.ResetChip(); }

    public void Write(int port, int reg, int value)
    {
        switch (reg)
        {
            case 0: core.SetReset(value != 0); break;
            case 1: core.SetStart(value != 0); break;
            case 2: core.Data(value); break;
        }
    }

    public void Generate(out int left, out int right)
    {
        acc += clocksPerSample;
        int n = (int)acc;
        acc -= n;
        // 곡 시작 앞의 바이트가 한꺼번에 들어왔으면 (보드가 이미 스트림을 흘리던 중) 소리 없이 따라잡는다
        for (int guard = 0; core.Backlog > 16 && guard < 100000; guard++) core.Advance(64);
        core.Advance(n);
        int v = mute ? 0 : core.Sample << 7;
        level = Math.Min(1f, Math.Abs(core.Sample) / 128f);
        left = right = v;
    }

    public void SetMute(int ch, bool m) { mute = m; }
    public void SetPitch(int ch, double ratio) { }
    public double ChannelFrequency(int ch) { return 0; }
    public float ChannelLevel(int ch) { return core.Busy ? level : 0; }
    public bool ChannelActive(int ch) { return core.Busy; }
}
