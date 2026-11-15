using System;

namespace AdlibComposer.Audio;

/// <summary>
/// 소리 칩 공통 인터페이스: 레지스터를 쓰고, 칩 고유 속도로 스테레오 샘플을 하나씩 만든다.
/// VGM · S98 · KSS 재생기는 어느 칩이든 이것만 보고 돌린다 (속도 맞추기 · 섞기는 재생기 몫).
/// </summary>
public interface ISoundChip
{
    /// <summary>칩 이름 (예: "YM2413 (OPLL)").</summary>
    string Name { get; }
    /// <summary><see cref="Generate"/> 한 번 = 이 속도의 샘플 하나 (Hz).</summary>
    int NativeRate { get; }
    /// <summary>믹서에 보일 채널 수.</summary>
    int ChannelCount { get; }
    /// <summary>채널 이름 (믹서 칸).</summary>
    string ChannelLabel(int ch);
    void Reset();
    /// <summary>레지스터 쓰기. port = 칩의 포트(뱅크) 번호, 포트가 하나인 칩은 0.</summary>
    void Write(int port, int reg, int value);
    /// <summary>스테레오 샘플 하나 (16bit 범위 안팎).</summary>
    void Generate(out int left, out int right);
    /// <summary>채널 끄기 (재생 조절).</summary>
    void SetMute(int ch, bool mute);
    /// <summary>채널 음높이 배수 (조옮김: 2^(반음/12)). 1 = 그대로.</summary>
    void SetPitch(int ch, double ratio);
    /// <summary>채널이 지금 내는 음 (Hz, 조옮김 전). 소리가 없으면 0.</summary>
    double ChannelFrequency(int ch);
    /// <summary>채널 음량 0~1 (표시용).</summary>
    float ChannelLevel(int ch);
    /// <summary>음높이 없는 소리(드럼 · 노이즈 · PCM)가 지금 울리는가 (표시용).</summary>
    bool ChannelActive(int ch);
}

/// <summary>칩 공통 도우미.</summary>
public static class SoundChips
{
    public static short Clip(double v) { return (short)(v > 32767 ? 32767 : v < -32768 ? -32768 : v); }
    public static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
}
