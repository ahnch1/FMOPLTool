using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.Player;

/// <summary>목록 한 줄의 종류.</summary>
public enum EntryKind { Up, Folder, Zip, Song, Multi, Track }

/// <summary>폴더 목록 한 줄.</summary>
public sealed class FolderEntry
{
    public EntryKind Kind { get; set; }
    /// <summary>폴더 · zip · 곡 파일 경로, 파일 안 곡이면 "파일::번호".</summary>
    public string Path { get; set; }
    public string Name { get; set; }
    /// <summary>오른쪽에 작게 (확장자 · 곡 제목).</summary>
    public string Detail { get; set; } = "";
    public bool IsPlayable { get { return Kind == EntryKind.Song || Kind == EntryKind.Track; } }
    public override string ToString() { return Kind + " " + Name; }
}

/// <summary>
/// 화면 없는 폴더 목록 (Jukebox 파일 목록과 같은 기준): 하위 폴더 · zip · 재생할 수 있는 곡 파일.
/// zip은 언제나 캐시 폴더에 풀어서 폴더처럼 연다 (<see cref="ZipFolder.Extract"/>: hoot 아카이브면 .hoot 항목도 만든다).
/// 곡이 여럿 든 파일(KSS · NSF · HES · GBS · hoot …)은 <see cref="EntryKind.Multi"/>로 두고, 열면 <see cref="Tracks"/>로 곡마다 줄.
/// </summary>
public static class SongFolder
{
    /// <summary>Jukebox가 재생하는 확장자 (Windows Jukebox와 같다).</summary>
    public static readonly string[] SongExtensions = new[] { ".rol", ".ims", ".mus", ".sop", ".s3m", ".stm", ".mod", ".nst", ".xm", ".it", ".mid", ".midi", ".wrk", ".xmi", ".mdi" }
        .Concat(ChipSongs.Extensions).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsSong(string path)
    {
        string ext = System.IO.Path.GetExtension(ChipSongs.FilePart(path) ?? "") ?? "";
        return SongExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 폴더 안 목록: [..] · 폴더 · zip · 곡 (이름 순, 대소문자 무시). 숨은 폴더(점으로 시작)는 뺀다.
    /// </summary>
    /// <param name="showUp">맨 위에 위 폴더 줄을 넣는가 (뿌리 폴더면 false).</param>
    public static List<FolderEntry> List(string dir, bool showUp)
    {
        var l = new List<FolderEntry>();
        if (showUp)
        {
            string parent = System.IO.Path.GetDirectoryName(dir.TrimEnd('/', '\\'));
            if (parent != null) l.Add(new FolderEntry { Kind = EntryKind.Up, Path = parent, Name = ".." });
        }
        var cmp = StringComparer.OrdinalIgnoreCase;
        foreach (var d in Directory.GetDirectories(dir).OrderBy(x => System.IO.Path.GetFileName(x), cmp))
        {
            string name = System.IO.Path.GetFileName(d);
            if (name.StartsWith(".")) continue;
            l.Add(new FolderEntry { Kind = EntryKind.Folder, Path = d, Name = name });
        }
        var files = Directory.GetFiles(dir).OrderBy(x => System.IO.Path.GetFileName(x), cmp).ToList();
        foreach (var z in files.Where(ZipFolder.IsArchive))
            l.Add(new FolderEntry { Kind = EntryKind.Zip, Path = z, Name = System.IO.Path.GetFileName(z), Detail = ZipFolder.IsZip(z) ? "ZIP" : "LZH" });
        foreach (var f in files)
        {
            if (!IsSong(f)) continue;
            bool multi;
            try { multi = ChipSongs.HasTracks(f); } catch (Exception) { multi = false; }
            l.Add(new FolderEntry
            {
                Kind = multi ? EntryKind.Multi : EntryKind.Song,
                Path = f,
                Name = System.IO.Path.GetFileName(f),
                Detail = System.IO.Path.GetExtension(f).TrimStart('.').ToUpperInvariant(),
            });
        }
        return l;
    }

    /// <summary>곡이 여럿 든 파일의 곡마다 한 줄 ("파일::번호").</summary>
    public static List<FolderEntry> Tracks(string file)
    {
        var l = new List<FolderEntry>();
        var tracks = ChipSongs.Tracks(file);
        for (int i = 0; i < tracks.Count; i++)
        {
            var (path, title) = tracks[i];
            // 제목이 "이름 #n"뿐이면 (m3u 없음) 번호만
            bool plain = System.Text.RegularExpressions.Regex.IsMatch(title ?? "", @" #\d+$");
            l.Add(new FolderEntry { Kind = EntryKind.Track, Path = path, Name = (i + 1).ToString("00") + (plain || string.IsNullOrEmpty(title) ? "" : "  " + title), Detail = "" });
        }
        return l;
    }

    /// <summary>zip을 (아직이면) 캐시 폴더에 풀고 그 폴더. 실패하면 예외.</summary>
    public static string OpenZip(string zipPath) { return ZipFolder.Extract(zipPath); }

    /// <summary>목록에서 재생할 수 있는 줄의 경로 (순서대로) = 재생 목록.</summary>
    public static List<string> Playlist(IEnumerable<FolderEntry> entries)
    {
        return entries.Where(e => e.IsPlayable).Select(e => e.Path).ToList();
    }
}
