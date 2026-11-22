using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// 칩 기록 곡을 한 곳에서 연다: VGM · VGZ는 그대로, S98 등 다른 형식은 같은 내용의 VGM(<see cref="VgmSong"/>)으로 바꿔
/// VGM 재생기(<see cref="VgmPlayer"/>)로 낸다. Jukebox · 파일 목록은 이것만 본다.
/// </summary>
public static class ChipSongs
{
    private static readonly List<(string[] ext, Func<string, VgmSong> load)> loaders = new List<(string[], Func<string, VgmSong>)>
    {
        (VgmFormat.Extensions, VgmFormat.Load),
        (S98Format.Extensions, S98Format.Load),
        (GymFormat.Extensions, GymFormat.Load),
        (Kss.Extensions, Kss.Load),
        (KssDrivers.Extensions, KssDrivers.Load),
        (Nsf.Extensions, Nsf.Load),
        (Nsfe.Extensions, Nsfe.Load),
        (Sap.Extensions, Sap.Load),
        (Hes.Extensions, Hes.Load),
        (Gbs.Extensions, Gbs.Load),
        (Mdx.Extensions, Mdx.Load),
        (Spc.Extensions, Spc.Load),
        (HootSongs.Extensions, HootSongs.Load),
        (AdlibStreamFormats.ImfExtensions, LoadImf),
        (AdlibStreamFormats.HscExtensions, AdlibStreamFormats.LoadHsc),
        (AdlibStreamFormats.CmfExtensions, AdlibStreamFormats.LoadCmf),
        (AdlibStreamFormats.DroExtensions, AdlibStreamFormats.LoadDro),
        (AdlibStreamFormats.RawExtensions, AdlibStreamFormats.LoadRaw),
    };

    /// <summary>곡이 여럿 든 형식: 확장자 → 곡 제목 목록 (목록 안 번호 = "경로::번호").</summary>
    private static readonly List<(string[] ext, Func<string, List<string>> titles)> multi = new List<(string[], Func<string, List<string>>)>
    {
        (Kss.Extensions, p => Kss.Tracks(p).Select(t => t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(p) + " #" + t.Song).ToList()),
        (Nsf.Extensions, p => Nsf.Tracks(p).Select(t => t.Title).ToList()),
        (Nsfe.Extensions, p => Nsfe.Tracks(p).Select(t => t.Title).ToList()),
        (Sap.Extensions, p => Sap.Tracks(p).Select(t => t.Title).ToList()),
        (Hes.Extensions, p => Hes.Tracks(p).Select(t => t.Title).ToList()),
        (Gbs.Extensions, p => Gbs.Tracks(p).Select(t => t.Title).ToList()),
        (HootSongs.Extensions, p => HootSongs.Tracks(p).Select(t => t.Title).ToList()),
        // XMI는 MIDI로 재생하지만 곡이 여럿 든 파일이라 목록만 같이 쓴다
        (new[] { ".xmi" }, p => Enumerable.Range(1, MidiFiles.SequenceCount(p)).Select(i => Path.GetFileNameWithoutExtension(p) + " #" + i).ToList()),
    };

    /// <summary>돌리지 않고 머리말에서 제목을 읽는 형식 (불러오기가 무거운 것: 드라이버를 돌려 길이를 재는 MSX 드라이버 곡).</summary>
    private static readonly List<(string[] ext, Func<string, string> title)> quickTitles = new List<(string[], Func<string, string>)>
    {
        (KssDrivers.Extensions, KssDrivers.Title),
    };

    /// <summary>곡이 여럿 들 수 있는 형식인가 (KSS · NSF · HES): 파일 목록이 곡마다 줄을 펼친다.</summary>
    public static bool HasTracks(string path)
    {
        if (MultiOf(path) == null) return false;
        // XMI는 곡이 둘 이상일 때만 펼친다 (대부분 하나)
        if (string.Equals(Path.GetExtension(FilePart(path)), ".xmi", StringComparison.OrdinalIgnoreCase)) return MidiFiles.SequenceCount(FilePart(path)) > 1;
        return true;
    }

    private static Func<string, List<string>> MultiOf(string path)
    {
        string ext = Path.GetExtension(FilePart(path) ?? "");
        foreach (var (exts, titles) in multi)
            if (exts.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase))) return titles;
        return null;
    }

    /// <summary>IMF 속도: 시스템 정의 adlib (.imf) / wolf3d (.wlf)의 tickRate.</summary>
    private static VgmSong LoadImf(string path)
    {
        bool wlf = string.Equals(Path.GetExtension(path), ".wlf", StringComparison.OrdinalIgnoreCase);
        var sys = SystemDefinitions.Get(wlf ? "wolf3d" : "adlib");
        int hz = sys != null && sys.TickRate > 0 ? (int)Math.Round(sys.TickRate) : wlf ? 700 : 560;
        var s = AdlibStreamFormats.ReadImf(File.ReadAllBytes(path), hz, Path.GetFileNameWithoutExtension(path));
        s.FilePath = path;
        return s;
    }

    /// <summary>형식 하나 더하기 (확장자 → VGM으로 바꿔 읽기).</summary>
    public static void Register(string[] extensions, Func<string, VgmSong> load) { loaders.Add((extensions, load)); }

    public static IEnumerable<string> Extensions { get { return loaders.SelectMany(l => l.ext); } }

    public static bool IsChipSong(string path) { return Find(path) != null; }

    public static VgmSong Load(string path)
    {
        var f = Find(path);
        if (f == null) throw new InvalidDataException("칩 기록 곡이 아닙니다: " + Path.GetFileName(path));
        return f(path);
    }

    /// <summary>
    /// 파일 하나에 곡이 여럿이면 (KSS · NSF · HES) 곡마다 "경로::번호"와 제목, 아니면 그 경로 하나.
    /// </summary>
    public static List<(string path, string title)> Tracks(string path)
    {
        var l = new List<(string, string)>();
        var titles = MultiOf(path);
        if (titles != null)
        {
            var t = titles(FilePart(path));
            for (int i = 0; i < t.Count; i++) l.Add((FilePart(path) + "::" + i, t[i]));
            return l;
        }
        l.Add((path, ""));
        return l;
    }

    /// <summary>목록에 보일 제목 (곡이 여럿인 형식은 돌리지 않고 머리말 · m3u에서).</summary>
    public static string Title(string path)
    {
        var titles = MultiOf(path);
        if (titles != null)
        {
            // HES는 곡 목록을 찾으려면 곡마다 돌려 봐야 한다: 아직 모르면 파일 목록에서는 돌리지 않는다
            if (Hes.Extensions.Any(x => string.Equals(x, Path.GetExtension(FilePart(path)), StringComparison.OrdinalIgnoreCase)) && !Hes.TracksKnown(FilePart(path))) return "";
            if (!Gbs.TracksKnown(FilePart(path))) return "";
            NezM3u.SplitTrack(path, out int n);
            var t = titles(FilePart(path));
            return t.Count > 0 ? t[Math.Max(0, Math.Min(n, t.Count - 1))] : "";
        }
        string qext = Path.GetExtension(FilePart(path) ?? "");
        foreach (var (exts, quick) in quickTitles)
            if (exts.Any(x => string.Equals(x, qext, StringComparison.OrdinalIgnoreCase))) return quick(FilePart(path));
        var v = Load(path);
        return v.TitleEn.Length > 0 ? v.TitleEn : v.TitleJp;
    }

    /// <summary>"경로::번호"에서 실제 파일 경로.</summary>
    public static string FilePart(string path)
    {
        int sep = path == null ? -1 : path.LastIndexOf("::", StringComparison.Ordinal);
        return sep > 1 ? path.Substring(0, sep) : path;
    }

    private static Func<string, VgmSong> Find(string path)
    {
        string ext = Path.GetExtension(FilePart(path) ?? "");
        foreach (var (exts, load) in loaders)
            if (exts.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase))) return load;
        return null;
    }
}
