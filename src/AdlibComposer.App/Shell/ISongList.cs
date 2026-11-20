using System.Collections.Generic;

namespace AdlibComposer.App.Shell;

/// <summary>
/// Jukebox 페이지가 왼쪽 목록에서 쓰는 것: 다음 곡 목록 · 고른 곡 · 재생 중 표시 · 포커스.
/// Jukebox = 공용 폴더 / 파일 목록 (<see cref="FileBrowser"/>), Hoot = hoot 목록 (<see cref="HootBrowser"/>) — 같은 JukeboxView가 둘 다에 붙는다.
/// </summary>
internal interface ISongList
{
    /// <summary>다음 곡 목록이 될 곡 파일 (보이는 순서, 곡이 여럿 든 파일은 Jukebox가 곡마다 펼친다).</summary>
    List<string> Paths { get; }
    /// <summary>목록에서 고른 곡 (없으면 null).</summary>
    string SelectedPath { get; }
    /// <summary>지금 재생하는 곡을 목록에 표시 ("파일::n"이면 그 곡 줄).</summary>
    void MarkCurrent(string path);
    void FocusList();
    /// <summary>목록에 포커스가 있다 (PageUp · PageDown은 목록이 쓴다).</summary>
    bool ListFocused { get; }
}
