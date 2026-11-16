using System;
using AdlibComposer.Audio.Chips;

namespace AdlibComposer.Audio;

/// <summary>"8-bit" 신디가 쓰는 칩 (소리 풍).</summary>
public enum ChipSound
{
    /// <summary>MSX: 멜로디 = SCC+ 15음 (파형 메모리), 드럼 = PSG.</summary>
    PsgScc,
    /// <summary>MSX PSG만: 멜로디 = PSG 2개 6음 (사각파), 드럼 = PSG 1개.</summary>
    Psg,
    /// <summary>MSX FM-PAC: 멜로디 = YM2413 (OPLL) 내장 악기 15음, 드럼 = OPLL 리듬 모드.</summary>
    FmPac,
    /// <summary>PC엔진 (HES 풍): 멜로디 = HuC6280 파형 메모리 12음, 드럼 = HuC6280 (톤 2 · 노이즈 2).</summary>
    Pce,
    /// <summary>패미컴 / NES: 멜로디 = 2A03 사각파 4 + 삼각파 2 (베이스), 드럼 = 노이즈 2.</summary>
    Nes,
    /// <summary>메가드라이브: 멜로디 = YM2612 FM 12음 (갈래마다 4연산자 음색), 드럼 = SN76489 (톤 2 · 노이즈).</summary>
    MegaDrive,
    /// <summary>게임보이: 멜로디 = DMG 사각파 4 + 파형 2 (베이스), 드럼 = 노이즈 2.</summary>
    GameBoy,
    /// <summary>슈퍼패미컴: S-DSP 둘 — 멜로디 13음 (갈래 파형을 BRR 샘플로, 칩 ADSR, 에코), 드럼 3 (킥 · 스네어 샘플 + 노이즈).</summary>
    Snes,
}

/// <summary>
/// GXSCC 방식 "8-bit" MIDI 신디: MIDI 곡을 MSX 칩 소리로 낸다. <see cref="Sound"/>로 PSG + SCC 또는 PSG만.
/// <para>멜로디 = SCC+ (<see cref="SccEmulator"/>) 세 개 = 15음. 음마다 32칸 파형 메모리 · 4비트 볼륨 · 12비트 분주
/// (높은 음일수록 음높이가 조금씩 어긋나는 칩 그대로). GM 프로그램 16 갈래(8개씩)마다 파형 · 엔벨로프 · 비브라토를 정해 둔다.</para>
/// <para>PSG만 (<see cref="ChipSound.Psg"/>): 멜로디 = AY-3-8910 두 개 = 6음, 모두 사각파 (갈래의 파형은 쓰지 않고 엔벨로프 · 비브라토만).
/// PSG 볼륨은 한 단계 약 3dB라 크기를 dB로 바꿔 고른다.</para>
/// <para>드럼 = PSG (<see cref="PsgEmulator"/>) 3채널: 킥 · 탐은 톤을 아래로 쓸어내리고, 스네어는 톤 + 노이즈, 하이햇 · 심벌은 노이즈.
/// 노이즈 주기는 칩에 하나뿐이라 마지막에 친 드럼 것을 쓴다.</para>
/// <para>엔벨로프 · 비브라토 · 드럼은 옛 MSX 드라이버처럼 1/60초(프레임)마다 레지스터를 고친다. 노트온 · 벤드는 바로 쓴다.
/// 음이 모자라면 가장 오래된 음을 뺏는다 (드럼 채널은 드럼 칩만 쓴다). 출력은 모노.</para>
/// <para>다른 풍 (<see cref="ChipSound"/>): FM-PAC = YM2413 둘 (하나는 리듬 모드: 멜로디 6 + BD · SD · TOM · CYM · HH, 하나는 멜로디 9) — 갈래마다 내장 악기를 고르고
/// 엔벨로프는 칩이 낸다. PC엔진 = HuC6280 셋 (멜로디 6 × 2, 드럼 칩: 채널 1 · 2 사인파 톤 드럼 · 채널 5 · 6 노이즈), 갈래의 파형을 5비트로.
/// NES = 2A03 둘 (사각파 4음: 갈래 파형 → 듀티 12.5 · 25 · 50%, 삼각파 2음 = 베이스 갈래 먼저, 드럼 = 노이즈 둘).
/// 메가드라이브 = YM2612 둘 (FM 12음, 갈래마다 4연산자 음색, 엔벨로프는 칩이) + SN76489 드럼. 게임보이 = DMG 둘 (NES와 같은 배치: 사각파 4 + 파형 채널 2 + 노이즈 2).
/// 슈퍼패미컴 = S-DSP 둘 (SPC700 없이 레지스터를 바로): 갈래 파형 한 주기를 BRR로 바꾼 반복 샘플, 칩 ADSR · 가우스 보간 · 에코, 드럼 = 만든 킥 · 스네어 샘플 + DSP 노이즈.</para>
/// </summary>
public sealed class ChipMidiSynth
{
    public const int Rate = 44100;
    public const int SccCount = 3;
    public const int ToneVoices = SccCount * SccEmulator.Channels;
    /// <summary>PSG만일 때 멜로디 PSG 수 (3음씩).</summary>
    public const int PsgToneChips = 2;
    public const int PsgToneVoices = PsgToneChips * PsgEmulator.Channels;
    public const int DrumVoices = PsgEmulator.Channels;
    /// <summary>풍마다 멜로디 음 수 중 가장 큰 것 (음 표 크기).</summary>
    public const int MaxTones = 15;
    public const int FmPacVoices = 15, PceVoices = 12, NesVoices = 6, MegaDriveVoices = 12, GameBoyVoices = 6, SnesVoices = 13;
    /// <summary>드럼 음 수 중 가장 큰 것 (PC엔진 4).</summary>
    private const int MaxDrums = 4;
    private const int FrameSamples = Rate / 60;

    // ------------------------------------------------------------ 파형 · 악기

    private static readonly sbyte[][] Waves = BuildWaves();

    private enum Wave { Square, Pulse25, Pulse12, Saw, Triangle, Sine, Organ, Brass, Bass }

    private static sbyte[][] BuildWaves()
    {
        var w = new sbyte[9][];
        for (int k = 0; k < w.Length; k++) w[k] = new sbyte[32];
        for (int i = 0; i < 32; i++)
        {
            double x = i / 32.0, s = Math.Sin(2 * Math.PI * x);
            double tri = x < 0.5 ? 4 * x - 1 : 3 - 4 * x;
            double saw = 1 - 2 * x;
            w[(int)Wave.Square][i] = Q(i < 16 ? 1 : -1);
            w[(int)Wave.Pulse25][i] = Q(i < 8 ? 1 : -0.6);
            w[(int)Wave.Pulse12][i] = Q(i < 4 ? 1 : -0.4);
            w[(int)Wave.Saw][i] = Q(saw);
            w[(int)Wave.Triangle][i] = Q(tri);
            w[(int)Wave.Sine][i] = Q(s);
            w[(int)Wave.Organ][i] = Q((s + 0.5 * Math.Sin(4 * Math.PI * x) + 0.3 * Math.Sin(6 * Math.PI * x)) / 1.45);
            w[(int)Wave.Brass][i] = Q(0.7 * saw + 0.3 * (i < 16 ? 1 : -1));
            w[(int)Wave.Bass][i] = Q(0.6 * tri + 0.4 * (i < 16 ? 1 : -1));
        }
        return w;
    }

    private static sbyte Q(double v) { return (sbyte)Math.Max(-120, Math.Min(120, Math.Round(v * 120))); }

    /// <summary>한 갈래의 소리: 파형, 어택(프레임), 감쇠(프레임마다 곱, 서스테인까지), 서스테인, 릴리스(프레임마다 곱), 비브라토(반음 · 시작 프레임).</summary>
    private struct Patch
    {
        public Wave Wave;
        public int Attack;
        public double Decay, Sustain, Release;
        public double VibDepth;
        public int VibDelay;
        public Patch(Wave w, int a, double d, double s, double r, double vib = 0, int vibDelay = 0)
        { Wave = w; Attack = a; Decay = d; Sustain = s; Release = r; VibDepth = vib; VibDelay = vibDelay; }
    }

    /// <summary>GM 프로그램 갈래 16개 (프로그램 / 8).</summary>
    private static readonly Patch[] Families =
    {
        new Patch(Wave.Pulse25, 0, 0.965, 0.25, 0.80),             // 피아노
        new Patch(Wave.Sine, 0, 0.92, 0.0, 0.75),                  // 크로매틱 타악기
        new Patch(Wave.Organ, 0, 1.0, 1.0, 0.60),                  // 오르간
        new Patch(Wave.Pulse25, 0, 0.955, 0.30, 0.75),             // 기타
        new Patch(Wave.Bass, 0, 0.98, 0.60, 0.70),                 // 베이스
        new Patch(Wave.Saw, 6, 1.0, 0.90, 0.90, 0.15, 20),         // 현악기
        new Patch(Wave.Saw, 8, 1.0, 0.85, 0.90, 0.10, 24),         // 앙상블
        new Patch(Wave.Brass, 3, 0.99, 0.85, 0.80, 0.10, 24),      // 금관
        new Patch(Wave.Square, 2, 1.0, 0.90, 0.80, 0.12, 25),      // 리드(목관)
        new Patch(Wave.Sine, 3, 1.0, 0.90, 0.80, 0.10, 25),        // 파이프
        new Patch(Wave.Square, 0, 1.0, 1.00, 0.75, 0.15, 20),      // 신스 리드
        new Patch(Wave.Triangle, 15, 1.0, 0.90, 0.95),             // 신스 패드
        new Patch(Wave.Saw, 10, 0.995, 0.80, 0.92),                // 신스 효과
        new Patch(Wave.Pulse12, 0, 0.95, 0.30, 0.75),              // 민속 악기
        new Patch(Wave.Sine, 0, 0.88, 0.0, 0.70),                  // 타악기
        new Patch(Wave.Saw, 0, 0.97, 0.20, 0.80),                  // 효과음
    };

    // ------------------------------------------------------------ 상태

    private struct Tone
    {
        public bool Active, KeyOn, Sustained;
        public int Channel, Note, Velocity;
        public double Pitch;        // 낸 음 (반음, 조옮김 포함, 벤드 제외)
        public Patch Patch;
        public int Frame;
        public double Env;
        public long Age;
        public int LastVol, LastDiv;
        public int WaveId;
        public int Program;
    }

    private enum DrumKind { Kick, Snare, ClosedHat, OpenHat, Cymbal, Tom, Click }

    private struct DrumVoice
    {
        public bool Active;
        public int Channel;
        public DrumKind Kind;
        public int Frame;
        public double Level;        // 처음 크기 0~1 (벨로시티 · 채널)
        public double Pitch;        // 톤 드럼의 처음 음 (반음)
        public long Age;
    }

    private readonly SccEmulator[] scc = new SccEmulator[SccCount];
    private readonly PsgEmulator psg = new PsgEmulator(PsgEmulator.MsxClock, Rate, false);
    private readonly PsgEmulator[] psgTone = new PsgEmulator[PsgToneChips];
    private ChipSound sound = ChipSound.PsgScc;
    private readonly Ym2413[] opll = { new Ym2413(), new Ym2413() };
    private readonly double[] opllAcc = new double[2], opllPrev = new double[2], opllCur = new double[2];
    private int rhythmKeys;
    private readonly int[] rhythmVol = new int[3];
    private readonly Huc6280Psg[] pce = { new Huc6280Psg(Huc6280Psg.DefaultClock, Rate), new Huc6280Psg(Huc6280Psg.DefaultClock, Rate), new Huc6280Psg(Huc6280Psg.DefaultClock, Rate) };
    private readonly NesApu[] nes = { new NesApu(NesApu.NtscClock, Rate), new NesApu(NesApu.NtscClock, Rate) };
    private readonly OpnChip[] opn2 = { new OpnChip(OpnChip.Variant.Ym2612), new OpnChip(OpnChip.Variant.Ym2612) };
    private readonly double[] opn2Acc = new double[2], opn2Prev = new double[2], opn2Cur = new double[2];
    private readonly Sn76489 sn = new Sn76489(Sn76489.DefaultClock, Rate);
    private readonly GbApu[] gb = { new GbApu(GbApu.DefaultClock, Rate), new GbApu(GbApu.DefaultClock, Rate) };
    private readonly SnesSpc[] snes = { SnesSpc.DspOnly(), SnesSpc.DspOnly() };
    private readonly double[] snesAcc = new double[2], snesPrev = new double[2], snesCur = new double[2];
    private readonly Tone[] tones = new Tone[MaxTones];
    private readonly DrumVoice[] drums = new DrumVoice[MaxDrums];
    private readonly double[] gain = new double[16];
    private readonly double[] bend = new double[16];
    private readonly bool[] sustain = new bool[16];
    private readonly int[] program = new int[16];
    private long clock;
    private int frameCount;

    public ChipMidiSynth()
    {
        for (int i = 0; i < SccCount; i++) scc[i] = new SccEmulator(SccEmulator.MsxClock, Rate, true);
        for (int i = 0; i < PsgToneChips; i++) psgTone[i] = new PsgEmulator(PsgEmulator.MsxClock, Rate, false);
        Reset();
    }

    /// <summary>쓰는 칩 (다음 <see cref="Reset"/>부터).</summary>
    public ChipSound Sound { get { return sound; } set { sound = value; } }

    /// <summary>지금 소리의 멜로디 음 수 (PSG + SCC = 15, PSG = 6, FM-PAC = 15, PC엔진 = 12, NES = 6).</summary>
    public int ToneCount
    {
        get
        {
            switch (sound)
            {
                case ChipSound.Psg: return PsgToneVoices;
                case ChipSound.FmPac: return FmPacVoices;
                case ChipSound.Pce: return PceVoices;
                case ChipSound.Nes: return NesVoices;
                case ChipSound.MegaDrive: return MegaDriveVoices;
                case ChipSound.GameBoy: return GameBoyVoices;
                case ChipSound.Snes: return SnesVoices;
                default: return ToneVoices;
            }
        }
    }

    /// <summary>지금 소리의 드럼 음 수 (FM-PAC는 리듬 모드라 0).</summary>
    public int DrumCount
    {
        get
        {
            switch (sound)
            {
                case ChipSound.FmPac: return 0;
                case ChipSound.Pce: return 4;
                case ChipSound.Nes: case ChipSound.GameBoy: return 2;
                default: return DrumVoices;   // PSG · 메가드라이브 (SN76489 톤 2 + 노이즈) = 3
            }
        }
    }

    /// <summary>풍마다 출력 크기 (같은 화음이 PSG + SCC와 비슷한 크기로 들리게 맞춘 값).</summary>
    public double FmPacGain { get; set; } = 1.15;
    public double PceGain { get; set; } = 0.85;
    public double NesGain { get; set; } = 0.65;
    public double MegaDriveGain { get; set; } = 0.33;
    public double GameBoyGain { get; set; } = 1.2;
    public double SnesGain { get; set; } = 0.7;

    /// <summary>시험용: FM-PAC 리듬을 친 수.</summary>
    public int FmDrumHits { get; private set; }

    /// <summary>Jukebox · 설정에 보일 이름.</summary>
    public static string Label(ChipSound s)
    {
        switch (s)
        {
            case ChipSound.Psg: return "8-bit PSG";
            case ChipSound.FmPac: return "MSX FM-PAC";
            case ChipSound.Pce: return "PC엔진 (HES)";
            case ChipSound.Nes: return "NES (패미컴)";
            case ChipSound.MegaDrive: return "메가드라이브";
            case ChipSound.GameBoy: return "게임보이";
            case ChipSound.Snes: return "슈퍼패미컴";
            default: return "8-bit PSG + SCC";
        }
    }

    /// <summary>한 줄 설명 (상태 줄).</summary>
    public static string Description(ChipSound s)
    {
        switch (s)
        {
            case ChipSound.Psg: return "8-bit PSG · 사각파 6음 + PSG 드럼";
            case ChipSound.FmPac: return "MSX FM-PAC · YM2413 내장 악기 15음 + 리듬 모드";
            case ChipSound.Pce: return "PC엔진 · HuC6280 파형 12음 + 톤 · 노이즈 드럼";
            case ChipSound.Nes: return "NES · 2A03 사각파 4 + 삼각파 2 + 노이즈 드럼";
            case ChipSound.MegaDrive: return "메가드라이브 · YM2612 FM 12음 + SN76489 드럼";
            case ChipSound.GameBoy: return "게임보이 · 사각파 4 + 파형 2 + 노이즈 드럼";
            case ChipSound.Snes: return "슈퍼패미컴 · S-DSP BRR 샘플 13음 + 에코 + 샘플 · 노이즈 드럼";
            default: return "8-bit PSG + SCC · SCC+ 15음 + PSG 드럼";
        }
    }

    /// <summary>설정 파일 값 (MidiSynth=psg · chip · fmpac · pce · nes).</summary>
    public static string Key(ChipSound s)
    {
        switch (s)
        {
            case ChipSound.Psg: return "psg";
            case ChipSound.FmPac: return "fmpac";
            case ChipSound.Pce: return "pce";
            case ChipSound.Nes: return "nes";
            case ChipSound.MegaDrive: return "md";
            case ChipSound.GameBoy: return "gb";
            case ChipSound.Snes: return "snes";
            default: return "chip";
        }
    }

    /// <summary>설정 파일 값 → 풍. 칩 신디 값이 아니면 false.</summary>
    public static bool TryParse(string key, out ChipSound s)
    {
        s = ChipSound.PsgScc;
        foreach (ChipSound c in Enum.GetValues(typeof(ChipSound)))
            if (string.Equals(Key(c), (key ?? "").Trim(), StringComparison.OrdinalIgnoreCase)) { s = c; return true; }
        return false;
    }


    /// <summary>PSG만일 때 멜로디 PSG 출력에 곱하는 값 (사각파는 같은 볼륨에서 SCC 파형보다 크다).</summary>
    public double PsgToneGain { get; set; } = 0.6;

    /// <summary>전체 출력 크기 (SCC · PSG 합에 곱한다).</summary>
    public double MasterGain { get; set; } = 0.55;

    /// <summary>지금 울리는 멜로디 음 수 (시험용).</summary>
    public int ActiveTones { get { int n = 0; foreach (var t in tones) if (t.Active) n++; return n; } }
    public int ActiveDrums { get { int n = 0; foreach (var d in drums) if (d.Active) n++; return n; } }

    public void Reset()
    {
        foreach (var c in scc) { c.Reset(); c.WritePort(3, 0, 0x1F); }
        foreach (var t in psgTone) { t.Reset(); t.Write(7, 0x38); }   // 톤만 켬 (볼륨 0 = 무음)
        psg.Reset();
        for (int i = 0; i < 2; i++) { opll[i].Reset(); opllAcc[i] = opllPrev[i] = opllCur[i] = 0; }
        // FM-PAC: 칩 0 = 리듬 모드 (멜로디 0~5), 리듬 채널 6~8 주파수는 보통 쓰는 값
        opll[0].Write(0, 0x0E, 0x20);
        opll[0].Write(0, 0x16, 0x20); opll[0].Write(0, 0x17, 0x50); opll[0].Write(0, 0x18, 0xC0);
        opll[0].Write(0, 0x26, 0x05); opll[0].Write(0, 0x27, 0x05); opll[0].Write(0, 0x28, 0x01);
        rhythmKeys = 0;
        Array.Clear(rhythmVol, 0, rhythmVol.Length);
        foreach (var c in pce)
        {
            c.Reset();
            c.Write(0, 1, 0xFF);
            for (int ch = 0; ch < 6; ch++) { c.Write(0, 0, ch); c.Write(0, 5, 0xFF); c.Write(0, 4, 0); }
        }
        for (int ch = 0; ch < 2; ch++) PceLoadWave(pce[2], ch, (int)Wave.Sine);   // 톤 드럼 = 사인파
        foreach (var a in nes)
        {
            a.Reset();
            a.Write(0, 0x15, 0x0F);
            a.Write(0, 0x00, 0x30); a.Write(0, 0x04, 0x30);   // 사각파: 길이 멈춤 · 고정 볼륨 0
            a.Write(0, 0x08, 0x80);                            // 삼각파: 꺼짐
            a.Write(0, 0x0C, 0x30);                            // 노이즈: 고정 볼륨 0
        }
        for (int i = 0; i < 2; i++)
        {
            var o = opn2[i];
            o.Reset();
            opn2Acc[i] = opn2Prev[i] = opn2Cur[i] = 0;
            o.Write(0, 0x22, 0); o.Write(0, 0x27, 0); o.Write(0, 0x2B, 0);
            for (int c = 0; c < 7; c++) if (c != 3) o.Write(0, 0x28, c);
            for (int port = 0; port < 2; port++) for (int c = 0; c < 3; c++) o.Write(port, 0xB4 + c, 0xC0);
        }
        sn.Reset();
        foreach (int r in new[] { 0x9F, 0xBF, 0xDF, 0xFF }) sn.Write(0, 0, r);
        foreach (var a in gb)
        {
            a.Reset();
            a.Write(0, 0x16, 0x80); a.Write(0, 0x14, 0x77); a.Write(0, 0x15, 0xFF);
            a.Write(0, 0x02, 0x08); a.Write(0, 0x07, 0x08); a.Write(0, 0x11, 0x08);
            a.Write(0, 0x0A, 0x80); a.Write(0, 0x0C, 0x00);
        }
        for (int i = 0; i < 2; i++) { SnesInit(snes[i]); snesAcc[i] = snesPrev[i] = snesCur[i] = 0; snesKof[i] = 0; snesNon[i] = 0; }
        for (int v = 0; v < MaxTones; v++) tones[v] = new Tone { WaveId = -1, LastDiv = -1, LastVol = -1 };
        for (int v = 0; v < MaxDrums; v++) drums[v] = new DrumVoice();
        for (int c = 0; c < 16; c++) { gain[c] = ChannelGain(100, 127); bend[c] = 0; sustain[c] = false; program[c] = 0; }
        clock = 0;
        frameCount = 0;
        WriteMixer();
    }

    // ------------------------------------------------------------ MIDI 쪽 (MidiPlayer가 부른다)

    /// <summary>채널 크기: GM 곡선 (볼륨 · 익스프레션 각각 제곱).</summary>
    public static double ChannelGain(int volume, int expression)
    {
        double v = volume / 127.0, e = expression / 127.0;
        return v * v * e * e;
    }

    public void SetChannelGain(int c, double g)
    {
        gain[c] = g;
        for (int v = 0; v < ToneCount; v++) if (tones[v].Active && tones[v].Channel == c) WriteTone(v, true);
    }

    public void SetProgram(int c, int p) { program[c] = p & 127; }

    /// <param name="semis">벤드 (반음).</param>
    public void SetBend(int c, double semis)
    {
        bend[c] = semis;
        for (int v = 0; v < ToneCount; v++) if (tones[v].Active && tones[v].Channel == c) WriteTone(v, true);
    }

    public void SetSustain(int c, bool on)
    {
        sustain[c] = on;
        if (on) return;
        for (int v = 0; v < ToneCount; v++)
            if (tones[v].Active && tones[v].Channel == c && tones[v].Sustained) { tones[v].Sustained = false; tones[v].KeyOn = false; tones[v].Frame = 0; }
    }

    /// <param name="note">원래 MIDI 음 (키오프 짝).</param>
    /// <param name="pitch">낼 음 (조옮김 포함).</param>
    public void NoteOn(int c, int note, double pitch, int velocity, bool drum)
    {
        if (drum) { DrumOn(c, (int)Math.Round(pitch), velocity); return; }
        // 같은 채널 · 같은 음이 울리면 그 음을 다시 친다
        int v = -1;
        for (int i = 0; i < ToneCount; i++) if (tones[i].Active && tones[i].Channel == c && tones[i].Note == note) { v = i; break; }
        int family = (program[c] & 127) >> 3;
        if (v < 0) v = AllocateTone(family);
        var patch = Families[family];
        int wave = (int)patch.Wave;
        tones[v] = new Tone
        {
            Active = true, KeyOn = true, Channel = c, Note = note, Velocity = velocity, Pitch = pitch, Patch = patch,
            Frame = 0, Env = patch.Attack > 0 ? 1.0 / (patch.Attack + 1) : 1.0, Age = clock,
            LastDiv = -1, LastVol = -1, WaveId = tones[v].WaveId, Program = program[c] & 127,
        };
        if (sound == ChipSound.PsgScc && tones[v].WaveId != wave)
        {
            var chip = scc[v / SccEmulator.Channels];
            int ch = v % SccEmulator.Channels;
            for (int i = 0; i < 32; i++) chip.WriteSccPlus(ch * 32 + i, (byte)Waves[wave][i]);
            tones[v].WaveId = wave;
        }
        else if (sound == ChipSound.Pce && tones[v].WaveId != wave)
        {
            PceLoadWave(pce[v / 6], v % 6, wave);
            tones[v].WaveId = wave;
        }
        else if (sound == ChipSound.FmPac) FmKeyOff(v);   // 다시 치기: 키를 한 번 놓는다
        else if (sound == ChipSound.MegaDrive)
        {
            MdKeyOff(v);
            int fam = (program[c] & 127) >> 3;
            if (tones[v].WaveId != fam) { MdLoadVoice(v, fam); tones[v].WaveId = fam; }
        }
        else if (sound == ChipSound.GameBoy && v >= 4 && tones[v].WaveId != wave)
        {
            GbLoadWave(gb[v - 4], wave);
            tones[v].WaveId = wave;
        }
        WriteTone(v, true);
    }

    public void NoteOff(int c, int note)
    {
        for (int v = 0; v < ToneCount; v++)
        {
            if (!tones[v].Active || !tones[v].KeyOn || tones[v].Channel != c || tones[v].Note != note || tones[v].Sustained) continue;
            if (sustain[c]) { tones[v].Sustained = true; continue; }
            tones[v].KeyOn = false;
            tones[v].Frame = 0;
        }
    }

    /// <summary>채널 c(−1 = 모두)의 소리를 바로 끈다.</summary>
    public void AllOff(int c)
    {
        for (int v = 0; v < ToneCount; v++)
            if (tones[v].Active && (c < 0 || tones[v].Channel == c)) { tones[v].Active = false; WriteTone(v, true); }
        for (int v = 0; v < DrumCount; v++)
            if (drums[v].Active && (c < 0 || drums[v].Channel == c)) { drums[v].Active = false; DrumSilence(v); }
        if (sound == ChipSound.FmPac && c < 0) { rhythmKeys = 0; opll[0].Write(0, 0x0E, 0x20); }
        WriteMixer();
    }

    /// <summary>NES: 베이스 갈래는 삼각파 (4 · 5)부터, 나머지는 사각파 (0~3)부터. 다른 풍은 모든 음.</summary>
    private bool Preferred(int v, int family)
    {
        if (sound != ChipSound.Nes && sound != ChipSound.GameBoy) return true;
        return family == 4 ? v >= 4 : v < 4;
    }

    private int AllocateTone(int family)
    {
        int n = ToneCount;
        for (int v = 0; v < n; v++) if (!tones[v].Active && Preferred(v, family)) return v;
        for (int v = 0; v < n; v++) if (!tones[v].Active) return v;
        // 놓은 음(릴리스 중)을 먼저, 그다음 가장 오래된 음 (NES는 같은 칸 안에서)
        int best = -1;
        long bestAge = long.MaxValue;
        for (int v = 0; v < n; v++)
        {
            if (!Preferred(v, family)) continue;
            long age = tones[v].Age - (tones[v].KeyOn ? 0 : 1L << 40);
            if (age < bestAge) { best = v; bestAge = age; }
        }
        return best;
    }

    // ------------------------------------------------------------ 멜로디 레지스터

    /// <summary>크기(0~1, 진폭) → PSG 볼륨 4비트: 한 단계 ≈ 3dB (15 = 0dB, 0 = 무음).</summary>
    public static int PsgVolume(double level)
    {
        if (level <= 0.0005) return 0;
        return Math.Max(0, Math.Min(15, (int)Math.Round(15 + 20 * Math.Log10(Math.Min(1, level)) / 3.0)));
    }

    private void WriteTone(int v, bool force)
    {
        switch (sound)
        {
            case ChipSound.FmPac: WriteFm(v, force); return;
            case ChipSound.Pce: WritePce(v, force); return;
            case ChipSound.Nes: WriteNes(v, force); return;
            case ChipSound.MegaDrive: WriteMd(v, force); return;
            case ChipSound.GameBoy: WriteGb(v, force); return;
            case ChipSound.Snes: WriteSnes(v, force); return;
        }
        ref var t = ref tones[v];
        int vol = 0;
        int div = t.LastDiv;
        bool psgMode = sound == ChipSound.Psg;
        if (t.Active)
        {
            double vel = t.Velocity / 127.0;
            double level = t.Env * Math.Pow(vel, 1.5) * gain[t.Channel];
            vol = psgMode ? PsgVolume(level) : Math.Max(0, Math.Min(15, (int)Math.Round(level * 15)));
            double semis = t.Pitch + bend[t.Channel];
            if (t.Patch.VibDepth > 0 && t.Frame > t.Patch.VibDelay && t.KeyOn)
                semis += t.Patch.VibDepth * Math.Sin(2 * Math.PI * 6.0 * (t.Frame - t.Patch.VibDelay) / 60.0);
            double f = 440.0 * Math.Pow(2, (semis - 69) / 12.0);
            div = psgMode ? Math.Max(1, Math.Min(4095, (int)Math.Round(PsgEmulator.MsxClock / (16.0 * f))))
                : Math.Max(9, Math.Min(4095, (int)Math.Round(SccEmulator.MsxClock / (32.0 * f)) - 1));
        }
        if (psgMode)
        {
            var pc = psgTone[v / PsgEmulator.Channels];
            int pch = v % PsgEmulator.Channels;
            if ((force || div != t.LastDiv) && div >= 0) { pc.Write(pch * 2, div & 0xFF); pc.Write(pch * 2 + 1, div >> 8); }
            t.LastDiv = div;
            if (force || vol != t.LastVol) pc.Write(8 + pch, vol);
            t.LastVol = vol;
            return;
        }
        var chip = scc[v / SccEmulator.Channels];
        int ch = v % SccEmulator.Channels;
        if (force || div != t.LastDiv)
        {
            if (div >= 0)
            {
                chip.WritePort(1, ch * 2, div & 0xFF);
                chip.WritePort(1, ch * 2 + 1, div >> 8);
            }
            t.LastDiv = div;
        }
        if (force || vol != t.LastVol)
        {
            chip.WritePort(2, ch, vol);
            t.LastVol = vol;
        }
    }

    /// <summary>크기 → HuC6280 볼륨 5비트 (한 칸 1.5dB).</summary>
    public static int PceVolume(double level)
    {
        if (level <= 0.0005) return 0;
        return Math.Max(0, Math.Min(31, (int)Math.Round(31 + 20 * Math.Log10(Math.Min(1, level)) / 1.5)));
    }

    private static double Hz(double semis) { return 440.0 * Math.Pow(2, (semis - 69) / 12.0); }

    /// <summary>음의 지금 높이 (반음, 벤드 · 비브라토 포함).</summary>
    private double SoundingPitch(ref Tone t)
    {
        double semis = t.Pitch + bend[t.Channel];
        if (t.Patch.VibDepth > 0 && t.Frame > t.Patch.VibDelay && t.KeyOn)
            semis += t.Patch.VibDepth * Math.Sin(2 * Math.PI * 6.0 * (t.Frame - t.Patch.VibDelay) / 60.0);
        return semis;
    }

    // ---- FM-PAC (YM2413)

    /// <summary>GM 갈래 → OPLL 내장 악기 (1 바이올린 2 기타 3 피아노 4 플루트 5 클라리넷 6 오보에 7 트럼펫 8 오르간 9 호른 10 신스 11 하프시코드 12 비브라폰 13 신스 베이스 14 베이스 15 일렉 기타).</summary>
    private static readonly int[] OpllFamily = { 3, 12, 8, 2, 14, 1, 1, 7, 5, 4, 10, 10, 10, 2, 12, 10 };

    /// <summary>GM 프로그램 → OPLL 내장 악기 번호 (1~15).</summary>
    public static int OpllInstrument(int program)
    {
        program &= 127;
        if (program == 6) return 11;                          // 하프시코드
        if (program >= 26 && program <= 31) return 15;        // 일렉 · 오버드라이브 · 디스토션 기타
        if (program >= 38 && program <= 39) return 13;        // 신스 베이스
        if (program == 60 || program == 69) return 9;         // 프렌치 호른 · 잉글리시 호른
        if (program == 68) return 6;                          // 오보에
        return OpllFamily[program >> 3];
    }

    /// <summary>FM 음 v → 칩 · 채널 (0~5 = 칩 0 (리듬 모드), 6~14 = 칩 1).</summary>
    private void FmChip(int v, out Ym2413 chip, out int ch) { if (v < 6) { chip = opll[0]; ch = v; } else { chip = opll[1]; ch = v - 6; } }

    // LastDiv (FM) = 블록 · F번호 (12비트) | 0x1000 키 눌림

    private void FmKeyOff(int v)
    {
        FmChip(v, out var chip, out int ch);
        int d = Math.Max(0, tones[v].LastDiv);
        chip.Write(0, 0x20 + ch, (d >> 8) & 0x0F);
        if (tones[v].LastDiv >= 0) tones[v].LastDiv &= 0xFFF;
    }

    private void WriteFm(int v, bool force)
    {
        ref var t = ref tones[v];
        FmChip(v, out var chip, out int ch);
        if (!t.Active)
        {
            chip.Write(0, 0x20 + ch, (Math.Max(0, t.LastDiv) >> 8) & 0x0F);
            if (t.LastDiv >= 0) t.LastDiv &= 0xFFF;
            return;
        }
        bool key = t.KeyOn || t.Sustained;
        // 크기는 칩 엔벨로프가 내므로 벨로시티 · 채널 크기만 (3dB 칸 = PSG와 같은 표)
        double level = Math.Pow(t.Velocity / 127.0, 1.5) * gain[t.Channel];
        int att = 15 - PsgVolume(level);
        int vol = OpllInstrument(t.Program) << 4 | att;
        if (force || vol != t.LastVol) { chip.Write(0, 0x30 + ch, vol); t.LastVol = vol; }
        double f = Hz(SoundingPitch(ref t));
        int block, fnum = 0;
        for (block = 0; block < 8; block++)
        {
            fnum = (int)Math.Round(f * (1 << (19 - block)) / (Ym2413.DefaultClock / 72));
            if (fnum < 512) break;
        }
        block = Math.Min(7, block);
        fnum = Math.Max(1, Math.Min(511, fnum));
        int hi = (block << 1) | (fnum >> 8);
        int div = (hi << 8 | (fnum & 0xFF)) | (key ? 0x1000 : 0);
        if (force || div != t.LastDiv)
        {
            chip.Write(0, 0x10 + ch, fnum & 0xFF);
            chip.Write(0, 0x20 + ch, (key ? 0x10 : 0) | hi);
            t.LastDiv = div;
        }
    }

    // ---- PC엔진 (HuC6280)

    /// <summary>파형 하나를 채널 ch의 파형 메모리에 (DDA를 켰다 끄면 쓰는 자리가 처음으로).</summary>
    private static void PceLoadWave(Huc6280Psg chip, int ch, int wave)
    {
        chip.Write(0, 0, ch);
        chip.Write(0, 4, 0x40);
        chip.Write(0, 4, 0x00);
        for (int i = 0; i < 32; i++) chip.Write(0, 6, Math.Max(0, Math.Min(31, (Waves[wave][i] + 128) * 31 / 255)));
    }

    private void WritePce(int v, bool force)
    {
        ref var t = ref tones[v];
        var chip = pce[v / 6];
        int ch = v % 6;
        int vol = 0, div = t.LastDiv;
        if (t.Active)
        {
            vol = PceVolume(t.Env * Math.Pow(t.Velocity / 127.0, 1.5) * gain[t.Channel]);
            div = Math.Max(1, Math.Min(4095, (int)Math.Round(Huc6280Psg.DefaultClock / (32.0 * Hz(SoundingPitch(ref t))))));
        }
        chip.Write(0, 0, ch);
        if ((force || div != t.LastDiv) && div >= 0) { chip.Write(0, 2, div & 0xFF); chip.Write(0, 3, div >> 8); }
        t.LastDiv = div;
        if (force || vol != t.LastVol) chip.Write(0, 4, vol > 0 ? 0x80 | vol : 0);
        t.LastVol = vol;
    }

    // ---- NES (2A03)

    /// <summary>갈래 파형 → 사각파 듀티 (0 = 12.5%, 1 = 25%, 2 = 50%).</summary>
    private static int Duty(Wave w)
    {
        switch (w)
        {
            case Wave.Pulse12: return 0;
            case Wave.Square: case Wave.Organ: case Wave.Sine: case Wave.Triangle: case Wave.Bass: return 2;
            default: return 1;
        }
    }

    /// <summary>NES 음: 0~3 = 사각파 (칩 v/2, 채널 v%2), 4 · 5 = 삼각파 (칩 v−4). 사각파 볼륨 4비트 (선형이라 크기의 제곱근), 삼각파는 켜고 끄기만.</summary>
    private void WriteNes(int v, bool force)
    {
        ref var t = ref tones[v];
        double level = t.Active ? t.Env * Math.Pow(t.Velocity / 127.0, 1.5) * gain[t.Channel] : 0;
        if (v < 4)
        {
            var apu = nes[v / 2];
            int b = (v % 2) * 4;
            int vol = t.Active ? Math.Max(0, Math.Min(15, (int)Math.Round(Math.Sqrt(level) * 15))) : 0;
            int reg0 = Duty(t.Patch.Wave) << 6 | 0x30 | vol;
            if (force || reg0 != t.LastVol) { apu.Write(0, b, reg0); t.LastVol = reg0; }
            if (!t.Active) return;
            int timer = Math.Max(8, Math.Min(2047, (int)Math.Round(NesApu.NtscClock / (16.0 * Hz(SoundingPitch(ref t)))) - 1));
            if (force || timer != t.LastDiv)
            {
                apu.Write(0, b + 2, timer & 0xFF);
                // 위 바이트를 쓰면 위상이 처음으로 돌아가 딸깍거리므로 바뀔 때만
                if (force || t.LastDiv < 0 || (timer >> 8) != (t.LastDiv >> 8)) apu.Write(0, b + 3, 0x08 | (timer >> 8));
                t.LastDiv = timer;
            }
            return;
        }
        var tri = nes[v - 4];
        bool on = t.Active && level > 0.03;
        if (on)
        {
            int timer = Math.Max(2, Math.Min(2047, (int)Math.Round(NesApu.NtscClock / (32.0 * Hz(SoundingPitch(ref t)))) - 1));
            if (force || timer != t.LastDiv || t.LastVol != 1)
            {
                tri.Write(0, 0x08, 0xFF);
                tri.Write(0, 0x0A, timer & 0xFF);
                if (force || t.LastVol != 1 || t.LastDiv < 0 || (timer >> 8) != (t.LastDiv >> 8)) tri.Write(0, 0x0B, 0x08 | (timer >> 8));
                t.LastDiv = timer;
            }
        }
        else if (force || t.LastVol != 0)
        {
            tri.Write(0, 0x08, 0x80);
            tri.Write(0, 0x0B, 0x08 | (Math.Max(0, t.LastDiv) >> 8));
        }
        t.LastVol = on ? 1 : 0;
    }

    // ---- 메가드라이브 (YM2612 FM + SN76489 드럼)

    /// <summary>FM 연산자 하나: DT · MUL · TL · KS · AR · D1R · D2R · SL · RR.</summary>
    private readonly struct FmOp
    {
        public readonly int Dt, Mul, Tl, Ks, Ar, D1r, D2r, Sl, Rr;
        public FmOp(int dt, int mul, int tl, int ks, int ar, int d1r, int d2r, int sl, int rr) { Dt = dt; Mul = mul; Tl = tl; Ks = ks; Ar = ar; D1r = d1r; D2r = d2r; Sl = sl; Rr = rr; }
    }

    /// <summary>4연산자 음색: 알고리즘 · 되먹임 · 연산자 1~4 (논리 순서).</summary>
    private sealed class FmVoice
    {
        public readonly int Alg, Fb;
        public readonly FmOp[] Ops;
        public FmVoice(int alg, int fb, params FmOp[] ops) { Alg = alg; Fb = fb; Ops = ops; }
    }

    private static FmOp O(int dt, int mul, int tl, int ks, int ar, int d1r, int d2r, int sl, int rr) { return new FmOp(dt, mul, tl, ks, ar, d1r, d2r, sl, rr); }

    /// <summary>GM 갈래 16개의 메가드라이브 FM 음색 (옛 게임 사운드 드라이버 같은 단순한 음색).</summary>
    private static readonly FmVoice[] MdVoices =
    {
        new FmVoice(4, 5, O(0, 1, 30, 1, 31, 10, 2, 3, 6), O(0, 1, 0, 1, 31, 8, 3, 4, 7), O(0, 3, 40, 1, 31, 12, 2, 3, 6), O(0, 1, 0, 1, 31, 7, 3, 5, 7)),        // 피아노
        new FmVoice(4, 0, O(0, 7, 35, 1, 31, 14, 4, 5, 6), O(0, 1, 0, 1, 31, 10, 5, 6, 6), O(0, 14, 40, 1, 31, 16, 5, 5, 6), O(0, 2, 6, 1, 31, 10, 5, 6, 6)),     // 크로매틱 타악기
        new FmVoice(7, 0, O(0, 1, 8, 0, 31, 0, 0, 0, 8), O(0, 2, 14, 0, 31, 0, 0, 0, 8), O(0, 4, 22, 0, 31, 0, 0, 0, 8), O(0, 8, 30, 0, 31, 0, 0, 0, 8)),          // 오르간
        new FmVoice(2, 6, O(0, 3, 28, 1, 31, 12, 4, 3, 6), O(0, 1, 35, 1, 31, 10, 3, 3, 6), O(0, 1, 30, 1, 31, 14, 4, 3, 6), O(0, 1, 0, 1, 31, 8, 4, 5, 7)),     // 기타
        new FmVoice(0, 6, O(0, 0, 24, 2, 31, 12, 6, 4, 8), O(0, 1, 30, 2, 31, 14, 6, 4, 8), O(0, 1, 26, 2, 31, 10, 4, 3, 8), O(0, 1, 0, 2, 31, 6, 2, 3, 8)),     // 베이스
        new FmVoice(4, 3, O(1, 1, 28, 0, 14, 3, 0, 1, 5), O(0, 1, 0, 0, 12, 2, 0, 2, 5), O(7, 1, 30, 0, 14, 3, 0, 1, 5), O(3, 1, 0, 0, 12, 2, 0, 2, 5)),          // 현악기
        new FmVoice(4, 3, O(1, 1, 30, 0, 10, 3, 0, 1, 5), O(0, 1, 0, 0, 9, 2, 0, 2, 5), O(7, 2, 34, 0, 10, 3, 0, 1, 5), O(3, 1, 2, 0, 9, 2, 0, 2, 5)),            // 앙상블
        new FmVoice(4, 6, O(0, 1, 22, 0, 18, 6, 0, 2, 6), O(0, 1, 0, 0, 20, 4, 0, 2, 6), O(0, 1, 24, 0, 18, 6, 0, 2, 6), O(0, 1, 0, 0, 20, 4, 0, 2, 6)),          // 금관
        new FmVoice(4, 4, O(0, 2, 30, 0, 22, 4, 0, 1, 7), O(0, 1, 0, 0, 22, 3, 0, 1, 7), O(0, 3, 34, 0, 22, 4, 0, 1, 7), O(0, 1, 2, 0, 22, 3, 0, 1, 7)),          // 리드(목관)
        new FmVoice(4, 0, O(0, 1, 50, 0, 20, 0, 0, 0, 7), O(0, 1, 0, 0, 20, 0, 0, 0, 7), O(0, 2, 60, 0, 20, 0, 0, 0, 7), O(0, 1, 10, 0, 20, 0, 0, 0, 7)),         // 파이프
        new FmVoice(4, 7, O(0, 1, 20, 0, 31, 2, 0, 1, 7), O(0, 1, 0, 0, 31, 2, 0, 1, 7), O(0, 2, 26, 0, 31, 2, 0, 1, 7), O(0, 1, 4, 0, 31, 2, 0, 1, 7)),          // 신스 리드
        new FmVoice(5, 3, O(0, 1, 30, 0, 10, 2, 0, 2, 4), O(1, 1, 6, 0, 10, 2, 0, 2, 4), O(7, 2, 10, 0, 10, 2, 0, 2, 4), O(3, 1, 6, 0, 10, 2, 0, 2, 4)),          // 신스 패드
        new FmVoice(5, 5, O(0, 3, 26, 0, 14, 2, 0, 2, 5), O(1, 1, 6, 0, 14, 2, 0, 2, 5), O(7, 5, 12, 0, 14, 2, 0, 2, 5), O(3, 2, 6, 0, 14, 2, 0, 2, 5)),          // 신스 효과
        new FmVoice(2, 5, O(0, 4, 30, 1, 31, 14, 4, 3, 6), O(0, 1, 36, 1, 31, 12, 3, 3, 6), O(0, 2, 32, 1, 31, 14, 4, 3, 6), O(0, 1, 0, 1, 31, 9, 4, 5, 7)),     // 민속 악기
        new FmVoice(4, 0, O(0, 5, 35, 1, 31, 16, 6, 5, 7), O(0, 1, 0, 1, 31, 12, 6, 6, 7), O(0, 9, 40, 1, 31, 18, 6, 5, 7), O(0, 1, 6, 1, 31, 12, 6, 6, 7)),     // 타악기
        new FmVoice(4, 7, O(0, 1, 20, 0, 31, 4, 0, 1, 6), O(0, 1, 0, 0, 31, 4, 0, 1, 6), O(0, 3, 24, 0, 31, 4, 0, 1, 6), O(0, 1, 4, 0, 31, 4, 0, 1, 6)),          // 효과음
    };

    /// <summary>알고리즘마다 캐리어 (논리 연산자 0~3).</summary>
    private static bool Carrier(int alg, int op)
    {
        switch (alg)
        {
            case 7: return true;
            case 5: case 6: return op > 0;
            case 4: return op == 1 || op == 3;
            default: return op == 3;
        }
    }

    /// <summary>논리 연산자 1~4 → 레지스터 칸 (S1 +0, S2 +8, S3 +4, S4 +12).</summary>
    private static readonly int[] SlotOffset = { 0, 8, 4, 12 };

    private void MdChip(int v, out OpnChip chip, out int port, out int ch, out int keyCh)
    {
        chip = opn2[v / 6];
        int c = v % 6;
        port = c / 3; ch = c % 3;
        keyCh = c < 3 ? c : c + 1;
    }

    private void MdLoadVoice(int v, int family)
    {
        MdChip(v, out var chip, out int port, out int ch, out _);
        var fv = MdVoices[family];
        for (int o = 0; o < 4; o++)
        {
            var op = fv.Ops[o];
            int r = SlotOffset[o] + ch;
            chip.Write(port, 0x30 + r, op.Dt << 4 | op.Mul);
            chip.Write(port, 0x40 + r, op.Tl);
            chip.Write(port, 0x50 + r, op.Ks << 6 | op.Ar);
            chip.Write(port, 0x60 + r, op.D1r);
            chip.Write(port, 0x70 + r, op.D2r);
            chip.Write(port, 0x80 + r, op.Sl << 4 | op.Rr);
            chip.Write(port, 0x90 + r, 0);
        }
        chip.Write(port, 0xB0 + ch, fv.Fb << 3 | fv.Alg);
        chip.Write(port, 0xB4 + ch, 0xC0);
    }

    private void MdKeyOff(int v)
    {
        MdChip(v, out var chip, out _, out _, out int keyCh);
        chip.Write(0, 0x28, keyCh);
        if (tones[v].LastDiv >= 0) tones[v].LastDiv &= 0x3FFF;
    }

    /// <summary>메가드라이브 음: 크기 = 캐리어 TL (0.75dB 칸), 엔벨로프는 칩이. LastDiv = 블록 · F번호 (14비트) | 0x4000 키 눌림.</summary>
    private void WriteMd(int v, bool force)
    {
        ref var t = ref tones[v];
        MdChip(v, out var chip, out int port, out int ch, out int keyCh);
        if (!t.Active)
        {
            if (t.LastDiv < 0 || (t.LastDiv & 0x4000) != 0 || force) chip.Write(0, 0x28, keyCh);
            if (t.LastDiv >= 0) t.LastDiv &= 0x3FFF;
            return;
        }
        bool key = t.KeyOn || t.Sustained;
        double level = Math.Pow(t.Velocity / 127.0, 1.5) * gain[t.Channel];
        int att = level <= 0.0001 ? 127 : Math.Min(127, (int)Math.Round(-20 * Math.Log10(Math.Min(1, level)) / 0.75));
        if (force || att != t.LastVol)
        {
            var fv = MdVoices[Math.Max(0, t.WaveId)];
            for (int o = 0; o < 4; o++)
                if (Carrier(fv.Alg, o)) chip.Write(port, 0x40 + SlotOffset[o] + ch, Math.Min(127, fv.Ops[o].Tl + att));
            t.LastVol = att;
        }
        double f = Hz(SoundingPitch(ref t));
        double fs = OpnChip.Ym2612Clock / 144;
        int block, fnum = 0;
        for (block = 0; block < 8; block++)
        {
            fnum = (int)Math.Round(f * (1 << 20) / (fs * Math.Pow(2, block - 1)));
            if (fnum < 2048) break;
        }
        block = Math.Min(7, block);
        fnum = Math.Max(1, Math.Min(2047, fnum));
        int freq = block << 11 | fnum;
        int div = freq | (key ? 0x4000 : 0);
        if (force || div != t.LastDiv)
        {
            if (force || (t.LastDiv & 0x3FFF) != freq || t.LastDiv < 0)
            {
                chip.Write(port, 0xA4 + ch, freq >> 8);
                chip.Write(port, 0xA0 + ch, freq & 0xFF);
            }
            if (force || t.LastDiv < 0 || ((t.LastDiv & 0x4000) != 0) != key) chip.Write(0, 0x28, (key ? 0xF0 : 0) | keyCh);
            t.LastDiv = div;
        }
    }

    /// <summary>SN76489 감쇠 (2dB 칸, 15 = 끔).</summary>
    private static int SnAtt(double level)
    {
        if (level <= 0.0005) return 15;
        return Math.Max(0, Math.Min(15, (int)Math.Round(-20 * Math.Log10(Math.Min(1, level)) / 2)));
    }

    /// <summary>메가드라이브 드럼: 톤 (킥 · 탐 · 클릭) = SN76489 톤 0 · 1 쓸어내림, 그 밖 = 노이즈.</summary>
    private void MdDrum(int v, DrumKind k, double level, double tonePitch, int frame)
    {
        int att = SnAtt(level);
        if (ToneKind(k))
        {
            int per = Math.Max(1, Math.Min(1023, (int)Math.Round(Sn76489.DefaultClock / (32.0 * Hz(Math.Max(20, tonePitch))))));
            sn.Write(0, 0, 0x80 | v << 5 | (per & 15));
            sn.Write(0, 0, (per >> 4) & 0x3F);
            sn.Write(0, 0, 0x90 | v << 5 | att);
            return;
        }
        if (frame == 0)
        {
            int rate = k == DrumKind.Snare ? 1 : 0;
            sn.Write(0, 0, 0xE0 | 0x04 | rate);
        }
        sn.Write(0, 0, 0xF0 | att);
    }

    // ---- 게임보이 (DMG)

    /// <summary>게임보이 음: 0~3 = 사각파 (칩 v/2, 채널 v%2), 4 · 5 = 파형 채널 (칩 v−4). 사각파 볼륨이 바뀌면 다시 트리거한다 (옛 드라이버처럼).</summary>
    private void WriteGb(int v, bool force)
    {
        ref var t = ref tones[v];
        double level = t.Active ? t.Env * Math.Pow(t.Velocity / 127.0, 1.5) * gain[t.Channel] : 0;
        if (v < 4)
        {
            var apu = gb[v / 2];
            int b = (v % 2) * 5;
            int vol = t.Active ? Math.Max(0, Math.Min(15, (int)Math.Round(Math.Sqrt(level) * 15))) : 0;
            int x = t.LastDiv;
            if (t.Active) x = Math.Max(0, Math.Min(2047, (int)Math.Round(2048 - 131072.0 / Hz(SoundingPitch(ref t)))));
            if (force || vol != t.LastVol)
            {
                apu.Write(0, b + 1, Duty(t.Patch.Wave) << 6);
                apu.Write(0, b + 2, vol << 4 | 0x08);
                apu.Write(0, b + 3, Math.Max(0, x) & 0xFF);
                apu.Write(0, b + 4, 0x80 | (Math.Max(0, x) >> 8));
                t.LastVol = vol;
                t.LastDiv = x;
                return;
            }
            if (t.Active && x != t.LastDiv)
            {
                apu.Write(0, b + 3, x & 0xFF);
                apu.Write(0, b + 4, x >> 8);
                t.LastDiv = x;
            }
            return;
        }
        var w = gb[v - 4];
        int code = !t.Active ? 0 : level > 0.5 ? 1 : level > 0.2 ? 2 : level > 0.05 ? 3 : 0;
        if (force || code != t.LastVol) { w.Write(0, 0x0C, code << 5); t.LastVol = code; }
        if (!t.Active) return;
        int wx = Math.Max(0, Math.Min(2047, (int)Math.Round(2048 - 65536.0 / Hz(SoundingPitch(ref t)))));
        if (force || wx != t.LastDiv)
        {
            w.Write(0, 0x0D, wx & 0xFF);
            w.Write(0, 0x0E, (force ? 0x80 : 0) | (wx >> 8));
            t.LastDiv = wx;
        }
    }

    /// <summary>게임보이 파형 채널에 갈래 파형 (4비트 32칸).</summary>
    private static void GbLoadWave(GbApu apu, int wave)
    {
        apu.Write(0, 0x0A, 0x00);
        for (int i = 0; i < 16; i++)
        {
            int hi = Math.Max(0, Math.Min(15, (Waves[wave][i * 2] + 128) * 15 / 255));
            int lo = Math.Max(0, Math.Min(15, (Waves[wave][i * 2 + 1] + 128) * 15 / 255));
            apu.Write(0, 0x20 + i, hi << 4 | lo);
        }
        apu.Write(0, 0x0A, 0x80);
    }

    /// <summary>게임보이 드럼: 노이즈 채널, 처음 한 번만 (칩 엔벨로프로 줄어든다).</summary>
    private void GbDrum(int v, DrumKind k, double level, int frame)
    {
        if (frame != 0) return;
        var apu = gb[v];
        int vol = Math.Max(1, Math.Min(15, (int)Math.Round(Math.Sqrt(level) * 15)));
        int period, nr43;
        switch (k)
        {
            case DrumKind.Kick: period = 2; nr43 = 0x63; break;
            case DrumKind.Tom: period = 2; nr43 = 0x52; break;
            case DrumKind.Snare: period = 2; nr43 = 0x31; break;
            case DrumKind.Cymbal: period = 5; nr43 = 0x10; break;
            case DrumKind.OpenHat: period = 3; nr43 = 0x00; break;
            case DrumKind.ClosedHat: period = 1; nr43 = 0x00; break;
            default: period = 1; nr43 = 0x08; break;
        }
        apu.Write(0, 0x11, vol << 4 | period);
        apu.Write(0, 0x12, nr43);
        apu.Write(0, 0x13, 0x80);
    }

    // ---- 슈퍼패미컴 (S-DSP 둘: 멜로디 13 + 드럼 3)

    /// <summary>S-DSP RAM 배치: 디렉터리 0200h, 샘플 0400h~, 에코 C000h~ (EDL 3 = 6KB).</summary>
    private const int SnesDir = 0x0200, SnesSampleBase = 0x0400, SnesEcho = 0xC000;
    /// <summary>샘플 번호: 0~8 = 갈래 파형 (한 주기 32칸, 반복), 9 = 킥, 10 = 스네어 (한 번).</summary>
    private const int SnesKick = 9, SnesSnare = 10;
    /// <summary>파형 샘플 한 주기 칸 수 (피치 = f × 32 / 32000 × 4096).</summary>
    private const int SnesCycle = 32;

    private static readonly byte[] SnesImage = BuildSnesImage(out SnesDirectory);
    private static byte[] SnesDirectory;

    /// <summary>16비트 PCM → BRR (필터 0만: 반복이 어긋나지 않게). 마지막 블록에 END (반복이면 LOOP도).</summary>
    public static byte[] EncodeBrr(short[] pcm, bool loop)
    {
        int blocks = (pcm.Length + 15) / 16;
        var o = new byte[blocks * 9];
        for (int b = 0; b < blocks; b++)
        {
            int max = 0;
            for (int i = 0; i < 16; i++) { int at = b * 16 + i; if (at < pcm.Length) max = Math.Max(max, Math.Abs((int)pcm[at])); }
            int shift = 0;
            while (shift < 12 && (int)Math.Round(max / (double)(1 << shift)) > 7) shift++;   // 풀 때 (n << shift) >> 1 을 다시 ×2 하므로 n ≈ pcm / 2^shift
            int head = shift << 4;
            if (b == blocks - 1) head |= 1 | (loop ? 2 : 0);
            o[b * 9] = (byte)head;
            for (int i = 0; i < 16; i += 2)
            {
                int Nib(int at)
                {
                    int s = at < pcm.Length ? pcm[at] : 0;
                    int n = (int)Math.Round(s / (double)(1 << shift));
                    return Math.Max(-8, Math.Min(7, n)) & 15;
                }
                o[b * 9 + 1 + i / 2] = (byte)(Nib(b * 16 + i) << 4 | Nib(b * 16 + i + 1));
            }
        }
        return o;
    }

    /// <summary>샘플 RAM 그림 (0400h부터)과 디렉터리 (샘플마다 시작 · 반복 주소).</summary>
    private static byte[] BuildSnesImage(out byte[] dir)
    {
        var parts = new System.Collections.Generic.List<(byte[] data, bool loop)>();
        for (int w = 0; w < Waves.Length; w++)
        {
            var pcm = new short[SnesCycle];
            for (int i = 0; i < SnesCycle; i++) pcm[i] = (short)(Waves[w][i] * 180);
            parts.Add((EncodeBrr(pcm, true), true));
        }
        // 킥: 사인을 쓸어내림 (0.25초), 스네어: 노이즈 + 짧은 톤 (0.2초)
        var rnd = new Random(1234);
        var kick = new short[8000];
        double ph = 0;
        for (int i = 0; i < kick.Length; i++)
        {
            double t = i / 32000.0;
            ph += 2 * Math.PI * (45 + 110 * Math.Exp(-t * 30)) / 32000.0;
            kick[i] = (short)(Math.Sin(ph) * 26000 * Math.Exp(-t * 12));
        }
        parts.Add((EncodeBrr(kick, false), false));
        var snare = new short[6400];
        ph = 0;
        for (int i = 0; i < snare.Length; i++)
        {
            double t = i / 32000.0;
            ph += 2 * Math.PI * 190 / 32000.0;
            double n = rnd.NextDouble() * 2 - 1;
            snare[i] = (short)((n * 0.75 * Math.Exp(-t * 18) + Math.Sin(ph) * 0.45 * Math.Exp(-t * 35)) * 24000);
        }
        parts.Add((EncodeBrr(snare, false), false));
        int total = 0;
        foreach (var p in parts) total += p.data.Length;
        var img = new byte[total];
        dir = new byte[parts.Count * 4];
        int at = 0;
        for (int k = 0; k < parts.Count; k++)
        {
            int addr = SnesSampleBase + at;
            Array.Copy(parts[k].data, 0, img, at, parts[k].data.Length);
            dir[k * 4] = (byte)addr; dir[k * 4 + 1] = (byte)(addr >> 8);
            dir[k * 4 + 2] = (byte)addr; dir[k * 4 + 3] = (byte)(addr >> 8);   // 반복 = 처음부터 (한 주기)
            at += parts[k].data.Length;
        }
        return img;
    }

    /// <summary>S-DSP 하나를 세운다: 샘플 · 디렉터리, 주 볼륨, 에코 (48ms · 되먹임 50% · 작게 16%), 키 오프 모두.</summary>
    private static void SnesInit(SnesSpc d)
    {
        d.Reset();
        d.LoadRam(SnesSampleBase, SnesImage);
        d.LoadRam(SnesDir, SnesDirectory);
        d.WriteDsp(0x5D, SnesDir >> 8);
        d.WriteDsp(0x0C, 0x7F); d.WriteDsp(0x1C, 0x7F);           // MVOL
        d.WriteDsp(0x6D, SnesEcho >> 8); d.WriteDsp(0x7D, 3);      // ESA · EDL
        d.WriteDsp(0x0D, 0x40);                                   // EFB
        d.WriteDsp(0x2C, 0x14); d.WriteDsp(0x3C, 0x14);           // EVOL
        d.WriteDsp(0x0F, 0x7F);
        for (int i = 1; i < 8; i++) d.WriteDsp(i * 0x10 + 0x0F, 0);  // FIR = 그대로
        d.WriteDsp(0x2D, 0); d.WriteDsp(0x3D, 0); d.WriteDsp(0x4D, 0);
        d.WriteDsp(0x5C, 0);
        d.WriteDsp(0x6C, 0x00);                                   // FLG: 소리 · 에코 켬
    }

    private readonly int[] snesKof = new int[2];
    private readonly int[] snesNon = new int[2];

    private void SnesKey(int chip, int voice, bool on)
    {
        var d = snes[chip];
        int bit = 1 << voice;
        if (on)
        {
            snesKof[chip] &= ~bit;
            d.WriteDsp(0x5C, snesKof[chip]);
            d.WriteDsp(0x4C, bit);
        }
        else
        {
            snesKof[chip] |= bit;
            d.WriteDsp(0x5C, snesKof[chip]);
        }
    }

    /// <summary>갈래 → ADSR: 어택 (프레임) · 서스테인 · 감쇠를 칩 ADSR 칸으로.</summary>
    private static void SnesAdsr(Patch p, out int adsr1, out int adsr2)
    {
        int ar = p.Attack <= 0 ? 15 : p.Attack < 4 ? 13 : p.Attack < 10 ? 11 : 9;
        int sl = Math.Max(0, Math.Min(7, (int)Math.Round(p.Sustain * 8) - 1));
        // 감쇠 빠르기를 다른 풍의 프레임 감쇠 (0.965 ≈ 0.5초)에 맞추고, 서스테인이 있으면 그 크기에서 멈춘다 (다른 풍처럼)
        int dr = p.Decay >= 0.999 ? 7 : p.Decay >= 0.96 ? 1 : p.Decay >= 0.9 ? 3 : 4;
        int sr = p.Sustain > 0 ? 0 : 14;
        adsr1 = 0x80 | dr << 4 | ar;
        adsr2 = sl << 5 | sr;
    }

    private void SnesVoice(int v, out int chip, out int voice) { chip = v < 8 ? 0 : 1; voice = v < 8 ? v : v - 8; }

    /// <summary>슈퍼패미컴 음: 샘플 = 갈래 파형, 엔벨로프 = 칩 ADSR (키 오프 = 칩 놓음), 크기 = 보이스 볼륨 (선형 7비트), 에코 켬.</summary>
    private void WriteSnes(int v, bool force)
    {
        ref var t = ref tones[v];
        SnesVoice(v, out int chip, out int voice);
        var d = snes[chip];
        int b = voice << 4;
        if (!t.Active)
        {
            if (force || t.LastVol != 0) { SnesKey(chip, voice, false); t.LastVol = 0; }
            return;
        }
        bool key = t.KeyOn || t.Sustained;
        double level = Math.Pow(t.Velocity / 127.0, 1.5) * gain[t.Channel];
        // 보이스 볼륨은 선형 7비트: 서스테인 음이 PSG + SCC (4비트 볼륨)와 비슷한 크기가 되게 0.62배
        int vol = Math.Max(0, Math.Min(127, (int)Math.Round(level * 127 * 0.62)));
        int p = Math.Max(1, Math.Min(0x3FFF, (int)Math.Round(Hz(SoundingPitch(ref t)) * SnesCycle / 32000.0 * 4096)));
        if (force || p != t.LastDiv) { d.WriteDsp(b + 2, p & 0xFF); d.WriteDsp(b + 3, p >> 8); t.LastDiv = p; }
        int state = vol << 1 | (key ? 1 : 0);
        if (force)
        {
            SnesAdsr(t.Patch, out int a1, out int a2);
            d.WriteDsp(b + 0, vol); d.WriteDsp(b + 1, vol);
            d.WriteDsp(b + 4, (int)t.Patch.Wave);
            d.WriteDsp(b + 5, a1); d.WriteDsp(b + 6, a2);
            snesNon[chip] &= ~(1 << voice);
            d.WriteDsp(0x3D, snesNon[chip]);
            d.WriteDsp(0x4D, 0xFF & ~(chip == 1 ? 0xE0 : 0));   // 에코: 멜로디만
            SnesKey(chip, voice, true);
            t.LastVol = state;
            return;
        }
        if (state == t.LastVol) return;
        if ((t.LastVol >> 1) != vol) { d.WriteDsp(b + 0, vol); d.WriteDsp(b + 1, vol); }
        if ((t.LastVol & 1) != 0 && !key) SnesKey(chip, voice, false);
        t.LastVol = state;
    }

    /// <summary>슈퍼패미컴 드럼 (칩 1의 보이스 5~7): 킥 · 탐 = 킥 샘플 (탐은 높여서), 스네어 = 스네어 샘플, 하이햇 · 심벌 · 클릭 = DSP 노이즈. 처음 한 번 키 온, 끝은 칩 엔벨로프.</summary>
    private void SnesDrum(int v, DrumKind k, double level, double pitch, int frame)
    {
        if (frame != 0) return;
        var d = snes[1];
        int voice = 5 + v, b = voice << 4;
        int vol = Math.Max(0, Math.Min(127, (int)Math.Round(level * 64)));   // 샘플 드럼은 PSG 드럼보다 크다: 반으로
        int srcn = SnesKick, p = 0x1000, sr = 18, noiseRate = -1;
        switch (k)
        {
            case DrumKind.Kick: break;
            case DrumKind.Tom: p = (int)(0x1000 * Math.Pow(2, (pitch - 40) / 12.0)); sr = 16; break;
            case DrumKind.Snare: srcn = SnesSnare; break;
            case DrumKind.ClosedHat: noiseRate = 0x1F; sr = 27; break;
            case DrumKind.OpenHat: noiseRate = 0x1F; sr = 20; break;
            case DrumKind.Cymbal: noiseRate = 0x1D; sr = 16; break;
            default: noiseRate = 0x1C; sr = 28; break;
        }
        p = Math.Max(1, Math.Min(0x3FFF, p));
        d.WriteDsp(b + 0, vol); d.WriteDsp(b + 1, vol);
        d.WriteDsp(b + 2, p & 0xFF); d.WriteDsp(b + 3, p >> 8);
        d.WriteDsp(b + 4, noiseRate >= 0 ? 0 : srcn);
        d.WriteDsp(b + 5, 0x8F | 7 << 4);   // AR 15 · DR 7
        d.WriteDsp(b + 6, sr);              // SL 0 · SR
        if (noiseRate >= 0) { snesNon[1] |= 1 << voice; d.WriteDsp(0x6C, noiseRate); }
        else snesNon[1] &= ~(1 << voice);
        d.WriteDsp(0x3D, snesNon[1]);
        SnesKey(1, voice, true);
    }

    /// <summary>1/60초: 엔벨로프 · 비브라토.</summary>
    private void FrameTones()
    {
        for (int v = 0; v < ToneCount; v++)
        {
            ref var t = ref tones[v];
            if (!t.Active) continue;
            t.Frame++;
            var p = t.Patch;
            if (t.KeyOn || t.Sustained)
            {
                if (t.Frame <= p.Attack) t.Env = (t.Frame + 1.0) / (p.Attack + 1);
                else if (t.Env > p.Sustain) t.Env = Math.Max(p.Sustain, t.Env * p.Decay);
                if (t.Env < 0.02 && p.Sustain <= 0) t.Active = false;   // 사라지는 소리 (피아노 · 타악기)
            }
            else
            {
                t.Env *= p.Release;
                if (t.Env < 0.02) t.Active = false;
            }
            WriteTone(v, false);
        }
    }

    // ------------------------------------------------------------ 드럼 (PSG)

    private static DrumKind KindOf(int note, out double pitch)
    {
        pitch = note;
        switch (note)
        {
            case 35: case 36: pitch = 40; return DrumKind.Kick;
            case 38: case 40: case 39: pitch = 55; return DrumKind.Snare;
            case 37: pitch = 79; return DrumKind.Click;
            case 42: case 44: return DrumKind.ClosedHat;
            case 46: return DrumKind.OpenHat;
            case 49: case 51: case 52: case 53: case 55: case 57: case 59: return DrumKind.Cymbal;
            case 41: case 43: case 45: case 47: case 48: case 50: pitch = 43 + (note - 41) * 1.6; return DrumKind.Tom;
        }
        if (note >= 60 && note <= 68) { pitch = note; return DrumKind.Tom; }   // 봉고 · 콩가 · 팀발레스
        if (note == 54 || note == 69 || note == 70 || note == 82) return DrumKind.ClosedHat;   // 탬버린 · 카바사 · 마라카스 · 셰이커
        pitch = Math.Max(60, note + 12);
        return DrumKind.Click;
    }

    private static bool ToneKind(DrumKind k) { return k == DrumKind.Kick || k == DrumKind.Tom || k == DrumKind.Click; }

    /// <summary>드럼 음 v가 이 종류를 낼 수 있는가 (PC엔진 · 메가드라이브: 0 · 1 = 톤 채널, 그 뒤 = 노이즈 채널).</summary>
    private bool DrumAllowed(int v, DrumKind k) { return (sound != ChipSound.Pce && sound != ChipSound.MegaDrive) || (v < 2) == ToneKind(k); }

    private void DrumOn(int c, int note, int velocity)
    {
        var kind = KindOf(note, out double pitch);
        if (sound == ChipSound.FmPac) { FmDrum(kind, Math.Pow(velocity / 127.0, 1.5) * gain[c]); return; }
        // 같은 종류의 하이햇은 같은 채널을 이어 쓴다 (열린 → 닫힌 하이햇이 끊기게)
        int v = -1;
        if (kind == DrumKind.ClosedHat || kind == DrumKind.OpenHat)
            for (int i = 0; i < DrumCount; i++) if (drums[i].Active && (drums[i].Kind == DrumKind.ClosedHat || drums[i].Kind == DrumKind.OpenHat) && DrumAllowed(i, kind)) { v = i; break; }
        if (v < 0)
        {
            long best = long.MaxValue;
            for (int i = 0; i < DrumCount; i++)
            {
                if (!DrumAllowed(i, kind)) continue;
                long age = drums[i].Active ? drums[i].Age : long.MinValue + i;
                if (age < best) { best = age; v = i; }
            }
        }
        double vel = velocity / 127.0;
        drums[v] = new DrumVoice { Active = true, Channel = c, Kind = kind, Frame = 0, Level = Math.Pow(vel, 1.5) * gain[c], Pitch = pitch, Age = clock };
        WriteDrum(v);
        WriteMixer();
    }

    /// <summary>드럼 하나의 지금 프레임 소리: 톤 주기 · 노이즈 주기 · 볼륨.</summary>
    private void WriteDrum(int v)
    {
        ref var d = ref drums[v];
        int f = d.Frame;
        double env;
        double tonePitch = -1;
        int noise = -1;
        switch (d.Kind)
        {
            case DrumKind.Kick: env = 1 - f / 9.0; tonePitch = d.Pitch - f * 2.5; break;
            case DrumKind.Snare: env = 1 - f / 10.0; tonePitch = f < 3 ? d.Pitch - f * 3 : -1; noise = 7; break;
            case DrumKind.ClosedHat: env = 0.75 - f / 5.0; noise = 1; break;
            case DrumKind.OpenHat: env = 0.75 - f / 20.0; noise = 1; break;
            case DrumKind.Cymbal: env = 0.8 - f / 50.0; noise = 2; break;
            case DrumKind.Tom: env = 1 - f / 14.0; tonePitch = d.Pitch - f * 0.7; break;
            default: env = 0.8 - f / 4.0; tonePitch = d.Pitch; break;
        }
        if (env <= 0) { d.Active = false; DrumSilence(v); return; }
        if (sound == ChipSound.Pce) { PceDrum(v, d.Kind, env * d.Level, tonePitch, f); return; }
        if (sound == ChipSound.Nes) { NesDrum(v, d.Kind, env * d.Level, f); return; }
        if (sound == ChipSound.MegaDrive) { MdDrum(v, d.Kind, env * d.Level, tonePitch, f); return; }
        if (sound == ChipSound.GameBoy) { GbDrum(v, d.Kind, env * d.Level, f); return; }
        if (sound == ChipSound.Snes) { SnesDrum(v, d.Kind, d.Level, d.Pitch, f); return; }
        int vol = Math.Max(0, Math.Min(15, (int)Math.Round(env * d.Level * 15)));
        if (tonePitch > 0)
        {
            double fr = 440.0 * Math.Pow(2, (tonePitch - 69) / 12.0);
            int period = Math.Max(1, Math.Min(4095, (int)Math.Round(PsgEmulator.MsxClock / (16.0 * fr))));
            psg.Write(v * 2, period & 0xFF);
            psg.Write(v * 2 + 1, period >> 8);
        }
        if (noise >= 0 && f == 0) psg.Write(6, noise);
        psg.Write(8 + v, vol);
    }

    private void DrumSilence(int v)
    {
        switch (sound)
        {
            case ChipSound.Pce: pce[2].Write(0, 0, v < 2 ? v : v + 2); pce[2].Write(0, 4, 0); break;
            case ChipSound.Nes: nes[v].Write(0, 0x0C, 0x30); break;
            case ChipSound.MegaDrive: sn.Write(0, 0, v < 2 ? 0x9F | v << 5 : 0xFF); break;
            case ChipSound.GameBoy: gb[v].Write(0, 0x11, 0x08); gb[v].Write(0, 0x13, 0x80); break;
            case ChipSound.Snes: SnesKey(1, 5 + v, false); break;
            case ChipSound.FmPac: break;
            default: psg.Write(8 + v, 0); break;
        }
    }

    /// <summary>PC엔진 드럼: 톤 (킥 · 탐 · 클릭) = 채널 0 · 1 사인파를 쓸어내림, 그 밖 = 채널 4 · 5 노이즈.</summary>
    private void PceDrum(int v, DrumKind k, double level, double tonePitch, int frame)
    {
        var chip = pce[2];
        int ch = v < 2 ? v : v + 2;
        chip.Write(0, 0, ch);
        int vol = PceVolume(level);
        if (ToneKind(k))
        {
            int per = Math.Max(1, Math.Min(4095, (int)Math.Round(Huc6280Psg.DefaultClock / (32.0 * Hz(Math.Max(20, tonePitch))))));
            chip.Write(0, 2, per & 0xFF); chip.Write(0, 3, per >> 8);
        }
        else if (frame == 0)
        {
            int nf = k == DrumKind.ClosedHat || k == DrumKind.OpenHat ? 0x1F : k == DrumKind.Cymbal ? 0x1D : 0x17;
            chip.Write(0, 7, 0x80 | nf);
        }
        chip.Write(0, 4, vol > 0 ? 0x80 | vol : 0);
    }

    /// <summary>NES 드럼: 노이즈 채널 (주기 번호: 작을수록 높음; 킥 · 탐 = 낮은 노이즈).</summary>
    private void NesDrum(int v, DrumKind k, double level, int frame)
    {
        var apu = nes[v];
        int vol = Math.Max(0, Math.Min(15, (int)Math.Round(Math.Sqrt(level) * 15)));
        apu.Write(0, 0x0C, 0x30 | vol);
        if (frame == 0)
        {
            int idx;
            switch (k)
            {
                case DrumKind.Kick: idx = 13; break;
                case DrumKind.Tom: idx = 10; break;
                case DrumKind.Snare: idx = 6; break;
                case DrumKind.Cymbal: idx = 3; break;
                case DrumKind.ClosedHat: case DrumKind.OpenHat: idx = 1; break;
                default: idx = 4; break;
            }
            apu.Write(0, 0x0E, idx);
            apu.Write(0, 0x0F, 0x08);
        }
    }

    /// <summary>FM-PAC 리듬: 종류 → BD · SD · TOM · CYM · HH 키 (놓았다 다시 눌러 새로 친다), 크기 = 리듬 볼륨 (3dB 칸).</summary>
    private void FmDrum(DrumKind k, double level)
    {
        int bit, reg, shift;
        switch (k)
        {
            case DrumKind.Kick: bit = 0x10; reg = 0x36; shift = 0; break;
            case DrumKind.Snare: bit = 0x08; reg = 0x37; shift = 0; break;
            case DrumKind.Tom: bit = 0x04; reg = 0x38; shift = 4; break;
            case DrumKind.Cymbal: bit = 0x02; reg = 0x38; shift = 0; break;
            default: bit = 0x01; reg = 0x37; shift = 4; break;   // 하이햇 · 클릭
        }
        int att = 15 - PsgVolume(level);
        int nv = (rhythmVol[reg - 0x36] & ~(0x0F << shift)) | (att << shift);
        rhythmVol[reg - 0x36] = nv;
        opll[0].Write(0, reg, nv);
        opll[0].Write(0, 0x0E, 0x20 | (rhythmKeys & ~bit));
        rhythmKeys |= bit;
        opll[0].Write(0, 0x0E, 0x20 | rhythmKeys);
        FmDrumHits++;
    }

    /// <summary>R7: 드럼마다 톤 · 노이즈 켜기 (0 = 켬, PSG 풍만).</summary>
    private void WriteMixer()
    {
        if (sound != ChipSound.PsgScc && sound != ChipSound.Psg) return;
        int m = 0x3F;
        for (int v = 0; v < DrumVoices; v++)
        {
            if (!drums[v].Active) continue;
            var k = drums[v].Kind;
            bool tone = k == DrumKind.Kick || k == DrumKind.Tom || k == DrumKind.Click || (k == DrumKind.Snare && drums[v].Frame < 3);
            bool noise = k == DrumKind.Snare || k == DrumKind.ClosedHat || k == DrumKind.OpenHat || k == DrumKind.Cymbal;
            if (tone) m &= ~(1 << v);
            if (noise) m &= ~(8 << v);
        }
        psg.Write(7, m);
    }

    private void FrameDrums()
    {
        bool any = false;
        for (int v = 0; v < DrumCount; v++)
        {
            if (!drums[v].Active) continue;
            drums[v].Frame++;
            WriteDrum(v);
            any = true;
        }
        if (any) WriteMixer();
    }

    // ------------------------------------------------------------ 소리

    /// <summary>샘플 하나 (44100Hz, 모노를 좌우 같게).</summary>
    public void Generate(out double left, out double right)
    {
        if (++frameCount >= FrameSamples)
        {
            frameCount = 0;
            FrameTones();
            FrameDrums();
        }
        clock++;
        double s = 0;
        switch (sound)
        {
            case ChipSound.FmPac:
                for (int i = 0; i < 2; i++)
                {
                    // OPLL은 클럭/72 (약 49.7kHz)로 낸다: 44.1kHz로 줄여 (두 샘플 사이를 곧게)
                    opllAcc[i] += opll[i].NativeRate / (double)Rate;
                    while (opllAcc[i] >= 1)
                    {
                        opllAcc[i] -= 1;
                        opllPrev[i] = opllCur[i];
                        opll[i].Generate(out int l, out int r);
                        opllCur[i] = (l + r) * 0.5;
                    }
                    s += (opllPrev[i] + (opllCur[i] - opllPrev[i]) * opllAcc[i]) * FmPacGain;
                }
                break;
            case ChipSound.Pce:
                foreach (var c in pce) { c.Generate(out int l, out int r); s += (l + r) * 0.5 * PceGain; }
                break;
            case ChipSound.Nes:
                foreach (var a in nes) { a.Generate(out int l, out int r); s += (l + r) * 0.5 * NesGain; }
                break;
            case ChipSound.MegaDrive:
                for (int i = 0; i < 2; i++)
                {
                    // YM2612는 클럭/144 (약 53kHz)로 낸다: 44.1kHz로 줄여 (두 샘플 사이를 곧게)
                    opn2Acc[i] += opn2[i].NativeRate / (double)Rate;
                    while (opn2Acc[i] >= 1)
                    {
                        opn2Acc[i] -= 1;
                        opn2Prev[i] = opn2Cur[i];
                        opn2[i].Generate(out int l, out int r);
                        opn2Cur[i] = (l + r) * 0.5;
                    }
                    s += (opn2Prev[i] + (opn2Cur[i] - opn2Prev[i]) * opn2Acc[i]) * MegaDriveGain;
                }
                { sn.Generate(out int l, out int r); s += (l + r) * 0.5 * MegaDriveGain * 0.8; }
                break;
            case ChipSound.GameBoy:
                foreach (var a in gb) { a.Generate(out int l, out int r); s += (l + r) * 0.5 * GameBoyGain; }
                break;
            case ChipSound.Snes:
                for (int i = 0; i < 2; i++)
                {
                    // S-DSP는 32kHz: 44.1kHz로 늘려 (두 샘플 사이를 곧게)
                    snesAcc[i] += SnesSpc.Rate / (double)Rate;
                    while (snesAcc[i] >= 1)
                    {
                        snesAcc[i] -= 1;
                        snesPrev[i] = snesCur[i];
                        snes[i].Generate(out int l, out int r);
                        snesCur[i] = (l + r) * 0.5;
                    }
                    s += (snesPrev[i] + (snesCur[i] - snesPrev[i]) * snesAcc[i]) * SnesGain;
                }
                break;
            case ChipSound.Psg:
                foreach (var t in psgTone) s += t.Generate() * PsgToneGain;
                s += psg.Generate() * 0.8;
                break;
            default:
                foreach (var c in scc) s += c.Generate();
                s += psg.Generate() * 0.8;
                break;
        }
        left = right = s * MasterGain;
    }
}
