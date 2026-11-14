using System;

namespace AdlibComposer.Audio;

/// <summary>"8-bit" 신디가 쓰는 칩.</summary>
public enum ChipSound
{
    /// <summary>멜로디 = SCC+ 15음 (파형 메모리), 드럼 = PSG.</summary>
    PsgScc,
    /// <summary>PSG만: 멜로디 = PSG 2개 6음 (사각파), 드럼 = PSG 1개.</summary>
    Psg,
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
/// 음이 모자라면 가장 오래된 음을 뺏는다 (드럼 채널은 PSG만 쓴다). 출력은 모노.</para>
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
    private readonly Tone[] tones = new Tone[ToneVoices];
    private readonly DrumVoice[] drums = new DrumVoice[DrumVoices];
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

    /// <summary>지금 소리의 멜로디 음 수 (PSG + SCC = 15, PSG = 6).</summary>
    public int ToneCount { get { return sound == ChipSound.Psg ? PsgToneVoices : ToneVoices; } }

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
        for (int v = 0; v < ToneVoices; v++) tones[v] = new Tone { WaveId = -1, LastDiv = -1, LastVol = -1 };
        for (int v = 0; v < DrumVoices; v++) drums[v] = new DrumVoice();
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
        if (v < 0) v = AllocateTone();
        var patch = Families[(program[c] & 127) >> 3];
        int wave = (int)patch.Wave;
        tones[v] = new Tone
        {
            Active = true, KeyOn = true, Channel = c, Note = note, Velocity = velocity, Pitch = pitch, Patch = patch,
            Frame = 0, Env = patch.Attack > 0 ? 1.0 / (patch.Attack + 1) : 1.0, Age = clock,
            LastDiv = -1, LastVol = -1, WaveId = tones[v].WaveId,
        };
        if (sound == ChipSound.PsgScc && tones[v].WaveId != wave)
        {
            var chip = scc[v / SccEmulator.Channels];
            int ch = v % SccEmulator.Channels;
            for (int i = 0; i < 32; i++) chip.WriteSccPlus(ch * 32 + i, (byte)Waves[wave][i]);
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
        for (int v = 0; v < DrumVoices; v++)
            if (drums[v].Active && (c < 0 || drums[v].Channel == c)) { drums[v].Active = false; psg.Write(8 + v, 0); }
        WriteMixer();
    }

    private int AllocateTone()
    {
        int best = -1;
        long bestAge = long.MaxValue;
        for (int v = 0; v < ToneCount; v++) if (!tones[v].Active) return v;
        // 놓은 음(릴리스 중)을 먼저, 그다음 가장 오래된 음
        for (int v = 0; v < ToneCount; v++)
        {
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

    private void DrumOn(int c, int note, int velocity)
    {
        var kind = KindOf(note, out double pitch);
        // 같은 종류의 하이햇은 같은 채널을 이어 쓴다 (열린 → 닫힌 하이햇이 끊기게)
        int v = -1;
        if (kind == DrumKind.ClosedHat || kind == DrumKind.OpenHat)
            for (int i = 0; i < DrumVoices; i++) if (drums[i].Active && (drums[i].Kind == DrumKind.ClosedHat || drums[i].Kind == DrumKind.OpenHat)) { v = i; break; }
        if (v < 0)
        {
            long best = long.MaxValue;
            for (int i = 0; i < DrumVoices; i++)
            {
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
        if (env <= 0) { d.Active = false; psg.Write(8 + v, 0); return; }
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

    /// <summary>R7: 드럼마다 톤 · 노이즈 켜기 (0 = 켬).</summary>
    private void WriteMixer()
    {
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
        for (int v = 0; v < DrumVoices; v++)
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
        if (sound == ChipSound.Psg) foreach (var t in psgTone) s += t.Generate() * PsgToneGain;
        else foreach (var c in scc) s += c.Generate();
        s += psg.Generate() * 0.8;
        left = right = s * MasterGain;
    }
}
