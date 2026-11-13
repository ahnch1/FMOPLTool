using System;

namespace AdlibComposer.Audio;

/// <summary>
/// 재생 중에 바꿀 수 있는 조절값 (Jukebox): 속도, 키(조옮김), 보이스별 켜기/끄기와 음높이, 출력 크기.
/// AudioEngine의 Set... 메서드로 바꾸면 오디오 스레드와 같은 락 안에서 바로 반영된다.
/// </summary>
public sealed class PlaybackOptions
{
    public const int Voices = 20;   // ROL·IMS 11보이스, SOP 20트랙

    /// <summary>재생 속도 배수 (1.0 = 원래 속도).</summary>
    public double Speed = 1.0;
    /// <summary>멜로디 보이스 전체 조옮김 (반음). 리듬 모드의 드럼(BD SD TO CY HH)에는 적용하지 않는다.</summary>
    public int Transpose;
    /// <summary>보이스별 음높이 (반음, 드럼 포함).</summary>
    public readonly int[] VoiceTranspose = new int[Voices];
    /// <summary>true면 그 보이스를 소리 내지 않는다.</summary>
    public readonly bool[] VoiceMute = new bool[Voices];
    /// <summary>출력 크기 배수 (0 ~ 2).</summary>
    public double Gain = 1.0;
}

/// <summary>보이스 하나의 지금 상태 (Jukebox 채널 표시용).</summary>
public struct VoiceState
{
    /// <summary>지금 울리는 음 번호 (조옮김 전), 없으면 -1.</summary>
    public int Note;
    /// <summary>보이스 볼륨 (0~1, 곡의 볼륨 이벤트).</summary>
    public float Volume;
    /// <summary>노트온이 일어날 때마다 1씩 는다 (같은 음이 다시 쳐진 것을 알 수 있게).</summary>
    public int NoteOnCount;
    /// <summary>지금 보이스의 악기 이름 (곡의 악기 이벤트).</summary>
    public string Instrument;
    /// <summary>악기를 뱅크에서 찾았는지.</summary>
    public bool InstrumentFound;
}
