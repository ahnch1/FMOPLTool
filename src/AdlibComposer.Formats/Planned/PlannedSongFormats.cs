using System;
using System.IO;
using AdlibComposer.Core;

namespace AdlibComposer.Formats.Planned;

/// <summary>
/// 확장 예정: IMS (AdLib 계열 MIDI 파생 포맷).
/// 구현 방법: Read/Write를 채우고 FormatRegistry.CreateDefault()에 Songs.Add(new ImsFormat()) 한 줄을 추가.
/// Song 모델(틱 기반 보이스 트랙)로 변환하면 편집기/재생/악보 변환이 그대로 동작한다.
/// </summary>
public sealed class ImsFormat : ISongFormat
{
    public string Id { get { return "ims"; } }
    public string DisplayName { get { return "IMS song (planned)"; } }
    public string[] Extensions { get { return new[] { ".ims" }; } }
    public bool CanRead { get { return false; } }
    public bool CanWrite { get { return false; } }
    public Song Read(Stream stream) { throw new NotSupportedException("IMS 포맷은 아직 구현되지 않았습니다."); }
    public void Write(Song song, Stream stream) { throw new NotSupportedException("IMS 포맷은 아직 구현되지 않았습니다."); }
}

/// <summary>확장 예정: SOP. 구현/등록 방법은 ImsFormat과 같다.</summary>
public sealed class SopFormat : ISongFormat
{
    public string Id { get { return "sop"; } }
    public string DisplayName { get { return "SOP song (planned)"; } }
    public string[] Extensions { get { return new[] { ".sop" }; } }
    public bool CanRead { get { return false; } }
    public bool CanWrite { get { return false; } }
    public Song Read(Stream stream) { throw new NotSupportedException("SOP 포맷은 아직 구현되지 않았습니다."); }
    public void Write(Song song, Stream stream) { throw new NotSupportedException("SOP 포맷은 아직 구현되지 않았습니다."); }
}
