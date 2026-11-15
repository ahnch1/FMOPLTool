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
        (Kss.Extensions, Kss.Load),
        (AdlibStreamFormats.ImfExtensions, LoadImf),
        (AdlibStreamFormats.HscExtensions, AdlibStreamFormats.LoadHsc),
        (AdlibStreamFormats.CmfExtensions, AdlibStreamFormats.LoadCmf),
    };

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
    /// 파일 하나에 곡이 여럿이면 (KSS) 곡마다 "경로::번호"와 제목, 아니면 그 경로 하나.
    /// </summary>
    public static List<(string path, string title)> Tracks(string path)
    {
        var l = new List<(string, string)>();
        if (Kss.Extensions.Any(x => string.Equals(x, Path.GetExtension(path), StringComparison.OrdinalIgnoreCase)))
        {
            var t = Kss.Tracks(path);
            for (int i = 0; i < t.Count; i++) l.Add((path + "::" + i, t[i].Title.Length > 0 ? t[i].Title : Path.GetFileNameWithoutExtension(path) + " #" + t[i].Song));
            return l;
        }
        l.Add((path, ""));
        return l;
    }

    /// <summary>목록에 보일 제목 (KSS는 돌리지 않고 m3u에서).</summary>
    public static string Title(string path)
    {
        if (Kss.Extensions.Any(x => string.Equals(x, Path.GetExtension(FilePart(path)), StringComparison.OrdinalIgnoreCase)))
        {
            var t = Kss.Tracks(FilePart(path));
            return t.Count > 0 ? t[0].Title : "";
        }
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
