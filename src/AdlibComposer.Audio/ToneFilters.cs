using System;

namespace AdlibComposer.Audio;

/// <summary>
/// 1-pole 로우패스 (바이리니어 변환, 프리워핑): 차단 주파수에서 정확히 −3dB, 나이퀴스트에서 0.
/// <c>y[n] = B·(x[n] + x[n−1]) + A·y[n−1]</c>. Jukebox '표준' 음색이 칩 출력(49716Hz)의 12kHz 위를 깎는 데 쓴다
/// (재생 필터가 사실상 없던 옛 카드의 DAC가 남기는 에일리어싱을 덜어 낸다).
/// </summary>
internal struct OnePoleLowPass
{
    private double b, a, x1, y1;

    public static OnePoleLowPass Create(double cutoffHz, double rate)
    {
        double w = Math.Tan(Math.PI * cutoffHz / rate);
        return new OnePoleLowPass { b = w / (1 + w), a = (1 - w) / (1 + w) };
    }

    public double Process(double x)
    {
        y1 = b * (x + x1) + a * y1;
        x1 = x;
        return y1;
    }

    public void Reset() { x1 = y1 = 0; }
}

/// <summary>2차 IIR 필터 (RBJ Audio EQ Cookbook 계수, Direct Form I).</summary>
internal struct Biquad
{
    private double b0, b1, b2, a1, a2, x1, x2, y1, y2;

    public static Biquad HighPass(double f, double q, double rate)
    {
        double w = 2 * Math.PI * f / rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
        return Make((1 + cs) / 2, -(1 + cs), (1 + cs) / 2, 1 + al, -2 * cs, 1 - al);
    }

    public static Biquad LowPass(double f, double q, double rate)
    {
        double w = 2 * Math.PI * f / rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
        return Make((1 - cs) / 2, 1 - cs, (1 - cs) / 2, 1 + al, -2 * cs, 1 - al);
    }

    public static Biquad Peaking(double f, double q, double gainDb, double rate)
    {
        double A = Math.Pow(10, gainDb / 40), w = 2 * Math.PI * f / rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * q);
        return Make(1 + al * A, -2 * cs, 1 - al * A, 1 + al / A, -2 * cs, 1 - al / A);
    }

    private static Biquad Make(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        return new Biquad { b0 = b0 / a0, b1 = b1 / a0, b2 = b2 / a0, a1 = a1 / a0, a2 = a2 / a0 };
    }

    public double Process(double x)
    {
        double y = b0 * x + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
        x2 = x1; x1 = x; y2 = y1; y1 = y;
        return y;
    }

    public void Reset() { x1 = x2 = y1 = y2 = 0; }
}

/// <summary>
/// '그 시절' 스피커: 모니터 옆에 두던 작은 PC 스피커 흉내. 실측 임펄스 응답 대신 필터 4개로 근사한다.
/// <list type="bullet">
/// <item>하이패스 200Hz × 2 (24dB/옥타브): 작은 콘이라 저음이 거의 없다 (90Hz에서 약 −28dB).</item>
/// <item>피킹 +12dB @ 3.5kHz (Q 1.2): 중고음이 튀는 작은 스피커 특유의 소리.</item>
/// <item>로우패스 9kHz: 고음은 금방 떨어진다.</item>
/// </list>
/// <see cref="Gain"/>으로 기본값과 들리는 크기를 맞춘다.
/// </summary>
internal struct SmallSpeaker
{
    /// <summary>크기 맞춤: 저음을 깎아 RMS는 줄지만 귀는 중고음에 민감하므로 RMS로 약 −2dB가 되게 (demo.rol 기준, 들리는 크기는 비슷).</summary>
    public const double Gain = 0.9;
    private Biquad hp1, hp2, peak, lp;

    public static SmallSpeaker Create(double rate)
    {
        return new SmallSpeaker
        {
            hp1 = Biquad.HighPass(200, 0.54, rate),
            hp2 = Biquad.HighPass(200, 1.31, rate),   // 두 단 Q = 4차 Butterworth
            peak = Biquad.Peaking(3500, 1.2, 12, rate),
            lp = Biquad.LowPass(9000, 0.707, rate),
        };
    }

    public double Process(double x)
    {
        return lp.Process(peak.Process(hp2.Process(hp1.Process(x)))) * Gain;
    }

    public void Reset() { hp1.Reset(); hp2.Reset(); peak.Reset(); lp.Reset(); }
}
