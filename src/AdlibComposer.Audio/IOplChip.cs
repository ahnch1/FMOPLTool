namespace AdlibComposer.Audio;

/// <summary>OPL 칩 추상화. 내장 간이 에뮬레이터 대신 Nuked-OPL3 포팅 등으로 교체할 수 있다.</summary>
public interface IOplChip
{
    /// <summary>칩 고유 샘플레이트 (OPL2 = 49716Hz).</summary>
    int NativeRate { get; }
    /// <summary>되먹임(feedback) 세기 배율 (1 = 칩 그대로).</summary>
    double FeedbackScale { get; set; }
    void Reset();
    void Write(int register, int value);
    /// <summary>모노 샘플 1개 생성.</summary>
    short Generate();
}
