using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// 4-op FM 엔진: YM2151 (OPM) · YM2203 (OPN) · YM2608 (OPNA) · YM2612 (OPN2)가 같이 쓴다.
/// <para>방식은 ymfm (Aaron Giles, BSD-3)의 구조를 따르되 정밀도보다 단순함을 고른 것: 위상은 실수(주기 단위), 엔벨로프는 10비트 감쇠(0.09375dB)를
/// EG 클럭(샘플 3개마다)마다 비율 표의 평균 증가량으로 움직인다. 변조 · 되먹임 크기는 칩과 같다 (변조 = 출력 ±4주기, 되먹임 7 = ±2주기).</para>
/// <para>오퍼레이터 번호는 레지스터 순서 (0 = M1, 1 = M2, 2 = C1, 3 = C2). 알고리즘 0~7은 OPN/OPM 공통.</para>
/// </summary>
public sealed class FmEngine
{
    public sealed class Op
    {
        // 값 (레지스터)
        public int Dt1, Mul, Tl, Ks, Ar, D1r, D2r, Rr, D1l, Dt2;
        public bool AmOn;
        // 상태
        public double Phase;        // 주기
        public double Att = 1023;   // 0 = 최대, 1023 = 무음
        public int Stage = 3;       // 0 공격 1 감쇠 2 지속 3 놓음
        public bool Key;
        public double Out0, Out1;   // 지난 출력 두 개 (되먹임)
        /// <summary>칩 계산식으로 정한 위상 증가 (주기 / 샘플, 음높이 배수 · LFO 전).</summary>
        public double Inc;
        /// <summary>OPN 채널 3 따로 주파수 모드에서 쓰는 이 오퍼레이터의 주파수 (−1 = 채널 것).</summary>
        public double OwnHz = -1;
        public int OwnKeyCode;
    }

    public sealed class Channel
    {
        public readonly Op[] Ops = { new Op(), new Op(), new Op(), new Op() };
        public int Algo, Fb;
        public bool Left = true, Right = true;
        public int Ams, Pms;
        /// <summary>채널 주파수 (Hz, 칩 계산식) · 키코드 (0~31, KS · 디튠).</summary>
        public double Hz;
        public int KeyCode;
        public bool Mute;
        public double Pitch = 1.0;
        /// <summary>true = 오퍼레이터 4 대신 노이즈 (OPM 채널 8).</summary>
        public bool Noise;
        /// <summary>true = 출력 대신 DAC (OPN2 채널 6).</summary>
        public bool Dac;
        public double DacValue;
    }

    public enum Kind { Opm, Opn }

    public readonly Channel[] Ch;
    public readonly Kind Type;
    public readonly int Rate;
    /// <summary>디튠 · 위상 계산의 기준 (샘플레이트, OPN = 위상 단위 1 = fs / 2^20 Hz).</summary>
    private readonly double fs;

    // LFO
    public bool LfoOn;
    public double LfoHz;
    /// <summary>OPM: 파형 0 톱니 1 사각 2 삼각 3 노이즈, AMD · PMD (0~127).</summary>
    public int LfoWave, Amd, Pmd;
    private double lfoPhase;
    private double lfoAm, lfoPm;   // AM 0~1, PM −1~1
    private int lfoNoise = 1;
    private double lfoNoiseVal;

    // 노이즈 (OPM)
    public double NoiseHz;
    private double noisePhase;
    private int noiseLfsr = 1;
    private double noiseOut = 1;

    private int egDiv;
    private static readonly double[] ExpTab = BuildExp();
    private const double TwoPi = Math.PI * 2;

    public FmEngine(Kind type, int channels, int rate)
    {
        Type = type;
        Rate = rate;
        fs = rate;
        Ch = new Channel[channels];
        for (int i = 0; i < channels; i++) Ch[i] = new Channel();
    }

    public void Reset()
    {
        foreach (var c in Ch)
        {
            c.Algo = c.Fb = c.Ams = c.Pms = 0; c.Left = c.Right = true; c.Hz = 0; c.KeyCode = 0; c.Noise = false; c.Dac = false; c.DacValue = 0;
            foreach (var o in c.Ops)
            {
                o.Dt1 = o.Mul = o.Tl = o.Ks = o.Ar = o.D1r = o.D2r = o.Rr = o.D1l = o.Dt2 = 0; o.AmOn = false;
                o.Phase = 0; o.Att = 1023; o.Stage = 3; o.Key = false; o.Out0 = o.Out1 = 0; o.Inc = 0; o.OwnHz = -1;
            }
        }
        LfoOn = false; LfoHz = 0; LfoWave = Amd = Pmd = 0; lfoPhase = 0; lfoAm = lfoPm = 0;
        NoiseHz = 0; egDiv = 0;
    }

    // ------------------------------------------------------------ 키

    public void KeyOn(int ch, int op, bool on)
    {
        var o = Ch[ch].Ops[op];
        if (on && !o.Key)
        {
            o.Key = true;
            o.Phase = 0;
            o.Stage = 0;
            if (AttackRate(Ch[ch], o) >= 62) { o.Att = 0; o.Stage = 1; }
        }
        else if (!on && o.Key)
        {
            o.Key = false;
            o.Stage = 3;
        }
    }

    // ------------------------------------------------------------ 디튠 · 위상

    // ymfm / fm.c 디튠 표 [키코드][DT1 & 3] (위상 단위, fs / 2^20 Hz)
    private static readonly byte[,] DetuneTab =
    {
        {0,0,1,2},{0,0,1,2},{0,0,1,2},{0,0,1,2},{0,1,2,2},{0,1,2,3},{0,1,2,3},{0,1,2,3},
        {0,1,2,4},{0,1,3,4},{0,1,3,4},{0,1,3,5},{0,2,4,5},{0,2,4,6},{0,2,4,6},{0,2,5,7},
        {0,2,5,8},{0,3,6,8},{0,3,6,9},{0,3,7,10},{0,4,8,11},{0,4,8,12},{0,4,9,13},{0,5,10,14},
        {0,5,11,16},{0,6,12,17},{0,6,13,19},{0,7,14,20},{0,8,16,22},{0,8,16,22},{0,8,16,22},{0,8,16,22},
    };
    private static readonly double[] Dt2Ratio = { 1.0, Math.Pow(2, 600 / 1200.0), Math.Pow(2, 781 / 1200.0), Math.Pow(2, 950 / 1200.0) };

    /// <summary>채널 주파수(또는 오퍼레이터 따로 주파수)로 네 오퍼레이터의 위상 증가를 다시 계산한다.</summary>
    public void UpdateIncrements(int ch)
    {
        var c = Ch[ch];
        foreach (var o in c.Ops)
        {
            double hz = o.OwnHz >= 0 ? o.OwnHz : c.Hz;
            int kc = o.OwnHz >= 0 ? o.OwnKeyCode : c.KeyCode;
            double det = DetuneTab[kc & 31, o.Dt1 & 3] * fs / 1048576.0;
            if ((o.Dt1 & 4) != 0) det = -det;
            double f = (hz * Dt2Ratio[o.Dt2 & 3] + det) * (o.Mul == 0 ? 0.5 : o.Mul);
            o.Inc = Math.Max(0, f) / fs;
        }
    }

    // ------------------------------------------------------------ 엔벨로프

    private static int Rate64(int r, int ksv) { return r == 0 ? 0 : Math.Min(63, r + ksv); }
    private static int KsValue(Channel c, Op o, int kc) { return kc >> (3 - o.Ks); }
    private int AttackRate(Channel c, Op o) { return Rate64(o.Ar * 2, KsValue(c, o, KeyCodeOf(c, o))); }
    private static int KeyCodeOf(Channel c, Op o) { return o.OwnHz >= 0 ? o.OwnKeyCode : c.KeyCode; }

    /// <summary>EG 클럭 하나에 감쇠가 움직이는 평균 양 (rate 0~63).</summary>
    private static double EgStep(int rate)
    {
        if (rate <= 0) return 0;
        if (rate >= 60) return 8;
        return (4 + (rate & 3)) / 8.0 * Math.Pow(2, (rate >> 2) - 11);
    }

    private static readonly double[] EgStepTab = BuildEgSteps();
    private static double[] BuildEgSteps() { var t = new double[64]; for (int r = 0; r < 64; r++) t[r] = EgStep(r); return t; }

    private void ClockEnvelope(Channel c, Op o)
    {
        int kc = KeyCodeOf(c, o);
        int ks = KsValue(c, o, kc);
        switch (o.Stage)
        {
            case 0:
                {
                    int r = Rate64(o.Ar * 2, ks);
                    if (r >= 62) { o.Att = 0; o.Stage = 1; break; }
                    o.Att -= (o.Att + 1) * EgStepTab[r] / 16.0;
                    if (o.Att <= 0) { o.Att = 0; o.Stage = 1; }
                    break;
                }
            case 1:
                {
                    double sl = (o.D1l == 15 ? 31 : o.D1l) * 32;
                    o.Att += EgStepTab[Rate64(o.D1r * 2, ks)];
                    if (o.Att >= sl) { o.Att = Math.Max(o.Att, sl); o.Stage = 2; }
                    break;
                }
            case 2:
                o.Att += EgStepTab[Rate64(o.D2r * 2, ks)];
                break;
            default:
                o.Att += EgStepTab[Rate64(o.Rr * 4 + 2, ks)];
                break;
        }
        if (o.Att > 1023) o.Att = 1023;
    }

    // ------------------------------------------------------------ LFO

    private static readonly double[] OpnPmCents = { 0, 3.4, 6.7, 10, 14, 20, 40, 80 };
    private static readonly double[] OpmPmCents = { 0, 5, 10, 20, 50, 100, 400, 700 };
    private static readonly int[] OpnAmShift = { 8, 3, 1, 0 };   // 8 = 없음

    private void ClockLfo()
    {
        if (!LfoOn || LfoHz <= 0) { lfoAm = 0; lfoPm = 0; return; }
        double prev = lfoPhase;
        lfoPhase += LfoHz / Rate;
        if (lfoPhase >= 1) lfoPhase -= Math.Floor(lfoPhase);
        double p = lfoPhase;
        if (Type == Kind.Opn) { lfoAm = p < 0.5 ? 1 - 2 * p : 2 * p - 1; lfoPm = p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4; return; }
        switch (LfoWave & 3)
        {
            case 0: lfoAm = 1 - p; lfoPm = p < 0.5 ? 2 * p : 2 * p - 2; break;
            case 1: lfoAm = p < 0.5 ? 1 : 0; lfoPm = p < 0.5 ? 1 : -1; break;
            case 2: lfoAm = p < 0.5 ? 1 - 2 * p : 2 * p - 1; lfoPm = p < 0.25 ? 4 * p : p < 0.75 ? 2 - 4 * p : 4 * p - 4; break;
            default:
                if (p < prev) { int b = (lfoNoise ^ (lfoNoise >> 3)) & 1; lfoNoise = (lfoNoise >> 1) | (b << 16); lfoNoiseVal = (lfoNoise & 0xFF) / 255.0; }
                lfoAm = lfoNoiseVal; lfoPm = lfoNoiseVal * 2 - 1; break;
        }
    }

    private double PmRatio(Channel c)
    {
        if (c.Pms == 0 || lfoPm == 0) return 1.0;
        double cents = Type == Kind.Opn ? OpnPmCents[c.Pms & 7] : OpmPmCents[c.Pms & 7] * Pmd / 127.0;
        return Math.Pow(2, cents * lfoPm / 1200.0);
    }

    private double AmAtt(Channel c)
    {
        if (c.Ams == 0) return 0;
        if (Type == Kind.Opn) return (126 * lfoAm) / (1 << OpnAmShift[c.Ams & 3]) * 1.0;
        return lfoAm * Amd / 127.0 * 255.0 * (c.Ams == 1 ? 0.25 : c.Ams == 2 ? 0.5 : 1.0);
    }

    // ------------------------------------------------------------ 소리

    private static double[] BuildExp()
    {
        // 감쇠 0~4095 (0.09375dB 단위) → 배율
        var t = new double[4096];
        for (int i = 0; i < t.Length; i++) t[i] = Math.Pow(10, -i * 0.09375 / 20.0);
        t[t.Length - 1] = 0;
        return t;
    }

    private static double Gain(double att)
    {
        int i = (int)att;
        return i >= 4095 ? 0 : i <= 0 ? 1 : ExpTab[i];
    }

    /// <summary>오퍼레이터 하나 (출력 −1~1).</summary>
    private static double OpOut(Op o, double modCycles, double am)
    {
        double att = o.Att + o.Tl * 8 + am;
        if (att >= 1023) return 0;
        return Math.Sin(TwoPi * (o.Phase + modCycles)) * Gain(att);
    }

    /// <summary>한 샘플 (칩 속도). 반환은 −1~1 근처 채널 합 (왼 · 오).</summary>
    public void Generate(out double left, out double right)
    {
        bool eg = ++egDiv >= 3;
        if (eg) egDiv = 0;
        ClockLfo();
        if (NoiseHz > 0)
        {
            noisePhase += NoiseHz / Rate;
            while (noisePhase >= 1) { noisePhase -= 1; int b = (noiseLfsr ^ (noiseLfsr >> 3)) & 1; noiseLfsr = (noiseLfsr >> 1) | (b << 16); noiseOut = (noiseLfsr & 1) != 0 ? 1 : -1; }
        }
        double l = 0, r = 0;
        for (int ci = 0; ci < Ch.Length; ci++)
        {
            var c = Ch[ci];
            var o = c.Ops;
            if (eg) for (int k = 0; k < 4; k++) ClockEnvelope(c, o[k]);
            double pm = PmRatio(c) * c.Pitch;
            for (int k = 0; k < 4; k++) { o[k].Phase += o[k].Inc * pm; if (o[k].Phase >= 1) o[k].Phase -= Math.Floor(o[k].Phase); }
            if (c.Dac) { if (!c.Mute) { if (c.Left) l += c.DacValue; if (c.Right) r += c.DacValue; } continue; }
            double am = AmAtt(c);
            double am0 = o[0].AmOn ? am : 0, am1 = o[1].AmOn ? am : 0, am2 = o[2].AmOn ? am : 0, am3 = o[3].AmOn ? am : 0;

            // M1 되먹임
            double fb = c.Fb > 0 ? (o[0].Out0 + o[0].Out1) * 8191.0 / (1 << (10 - c.Fb)) / 1024.0 : 0;
            double m1 = OpOut(o[0], fb, am0);
            o[0].Out1 = o[0].Out0; o[0].Out0 = m1;
            const double M = 4.0;   // 변조: 출력 ±1 → ±4주기
            double c1, m2, c2, outv;
            switch (c.Algo & 7)
            {
                case 0: c1 = OpOut(o[2], m1 * M, am2); m2 = OpOut(o[1], c1 * M, am1); c2 = Last(c, o[3], m2 * M, am3); outv = c2; break;
                case 1: c1 = OpOut(o[2], 0, am2); m2 = OpOut(o[1], (m1 + c1) * M, am1); c2 = Last(c, o[3], m2 * M, am3); outv = c2; break;
                case 2: c1 = OpOut(o[2], 0, am2); m2 = OpOut(o[1], c1 * M, am1); c2 = Last(c, o[3], (m1 + m2) * M, am3); outv = c2; break;
                case 3: c1 = OpOut(o[2], m1 * M, am2); m2 = OpOut(o[1], 0, am1); c2 = Last(c, o[3], (c1 + m2) * M, am3); outv = c2; break;
                case 4: c1 = OpOut(o[2], m1 * M, am2); m2 = OpOut(o[1], 0, am1); c2 = Last(c, o[3], m2 * M, am3); outv = c1 + c2; break;
                case 5: c1 = OpOut(o[2], m1 * M, am2); m2 = OpOut(o[1], m1 * M, am1); c2 = Last(c, o[3], m1 * M, am3); outv = c1 + m2 + c2; break;
                case 6: c1 = OpOut(o[2], m1 * M, am2); m2 = OpOut(o[1], 0, am1); c2 = Last(c, o[3], 0, am3); outv = c1 + m2 + c2; break;
                default: c1 = OpOut(o[2], 0, am2); m2 = OpOut(o[1], 0, am1); c2 = Last(c, o[3], 0, am3); outv = m1 + c1 + m2 + c2; break;
            }
            if (c.Mute) continue;
            if (outv > 1) outv = 1; else if (outv < -1) outv = -1;
            if (c.Left) l += outv;
            if (c.Right) r += outv;
        }
        left = l; right = r;
    }

    /// <summary>오퍼레이터 4 (C2): OPM 노이즈 채널이면 노이즈.</summary>
    private double Last(Channel c, Op o, double mod, double am)
    {
        if (!c.Noise) return OpOut(o, mod, am);
        double att = o.Att + o.Tl * 8 + am;
        return att >= 1023 ? 0 : noiseOut * Gain(att);
    }

    // ------------------------------------------------------------ 표시

    /// <summary>나오는 오퍼레이터 (알고리즘별 캐리어).</summary>
    public static bool IsCarrier(int algo, int op)
    {
        switch (algo & 7)
        {
            case 0: case 1: case 2: case 3: return op == 3;
            case 4: return op == 2 || op == 3;
            case 5: case 6: return op != 0;
            default: return true;
        }
    }

    /// <summary>캐리어 중 가장 큰 소리 (0~1).</summary>
    public float Level(int ch)
    {
        var c = Ch[ch];
        double best = 0;
        for (int k = 0; k < 4; k++)
            if (IsCarrier(c.Algo, k)) best = Math.Max(best, Gain(c.Ops[k].Att + c.Ops[k].Tl * 8));
        return (float)best;
    }

    /// <summary>키가 눌린 캐리어가 있는가.</summary>
    public bool Keyed(int ch)
    {
        var c = Ch[ch];
        for (int k = 0; k < 4; k++) if (IsCarrier(c.Algo, k) && c.Ops[k].Key) return true;
        return false;
    }
}
