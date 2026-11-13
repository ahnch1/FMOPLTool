using AdlibComposer.Core;

namespace AdlibComposer.Audio;

/// <summary>
/// 파일 값 ↔ OPL 레지스터 값 변환 규칙. 원본과 소리가 다르면 여기서만 고치면 된다.
/// </summary>
public sealed class OplMapping
{
    /// <summary>
    /// ROL 피치 이벤트 1.0 기준, 0.0~2.0 → 이 값(반음)만큼 위아래.
    /// adlib.c 기본 pitchRange = 1 반음 (피치벤드 0x0000~0x3FFF, 중앙 0x2000).
    /// </summary>
    public double PitchBendSemitones { get; set; } = 1.0;

    /// <summary>
    /// 악기 TL(파일 값 = OPL 레지스터, 0 = 최대)과 볼륨(0~1)으로 실제 TL 레지스터 값을 만든다.
    /// adlib.c SndSKslLevel과 같은 방식: 진폭(63 - TL)에 상대 볼륨을 곱한다.
    /// </summary>
    public int TlRegister(int totalLevel, double volume)
    {
        int amplitude = 63 - (totalLevel & 0x3F);
        double scaled = amplitude * Calc.Clamp(volume, 0.0, 1.0);
        return Calc.Clamp(63 - (int)System.Math.Round(scaled), 0, 63);
    }
}
