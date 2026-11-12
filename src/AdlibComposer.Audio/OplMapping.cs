using AdlibComposer.Core;

namespace AdlibComposer.Audio;

/// <summary>
/// 파일 값 ↔ OPL 레지스터 값 변환 규칙. 원본과 소리가 다르면 여기서만 고치면 된다.
/// </summary>
public sealed class OplMapping
{
    /// <summary>
    /// true: 악기의 Output Level이 "클수록 큰 소리"(63=최대)이고 OPL TL 레지스터는 63 - 값.
    /// false: Output Level 값이 OPL TL 레지스터 값 그대로(0=최대).
    /// 원본 화면(캐리어 63)을 근거로 기본값을 true로 두었으며 실제 BNK로 검증이 필요하다.
    /// </summary>
    public bool InvertOutputLevel { get; set; } = true;

    /// <summary>ROL 피치 이벤트 1.0 기준, 0.0~2.0 → 이 값(반음)만큼 위아래. 원본 규칙 미확인(가정).</summary>
    public double PitchBendSemitones { get; set; } = 2.0;

    /// <summary>악기의 Output Level(UI값)과 볼륨(0~1)으로 TL 레지스터 값(0~63)을 만든다.</summary>
    public int TlRegister(int outputLevel, double volume)
    {
        int level = Calc.Clamp(outputLevel, 0, 63);
        double loud = InvertOutputLevel ? level : 63 - level;
        loud *= Calc.Clamp(volume, 0.0, 1.0);
        int tl = 63 - (int)System.Math.Round(loud);
        return Calc.Clamp(tl, 0, 63);
    }
}
