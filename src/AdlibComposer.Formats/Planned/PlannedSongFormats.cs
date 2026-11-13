using System;
using System.IO;
using AdlibComposer.Core;

namespace AdlibComposer.Formats.Planned;

/// <summary>확장 예정: SOP. 구현 예시는 Formats/ImsFormat.cs. Read/Write를 채우고 FormatRegistry.CreateDefault()에 등록한다.</summary>
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
