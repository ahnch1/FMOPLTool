using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 항목 파일 (<see cref="HootArchive.Extension"/> = 항목 XML 하나, 같은 폴더 = zip을 푼 내용) → 곡. 곡 목록 = titlelist (range 뺌), "파일::n" = n번째 제목.
/// <para>드라이버별로: pc98dos = PC-98 DOS 흉내 (<see cref="Dos.HootPc98"/>), snes spc = rom type="data" offset이 곡 코드인 .spc,
/// msx kss · nes nsf · gb gbs = rom의 그 파일에서 곡 코드 번호의 곡 (길이 · 페이드는 같은 이름 m3u에 그 곡이 있으면 그것, 없으면 그 형식 기본값).
/// 제목은 hoot 목록의 것 (트리 · 파일 목록과 같게).</para>
/// </summary>
public static class HootSongs
{
    public static readonly string[] Extensions = { HootArchive.Extension };

    public static List<(int Code, string Title)> Tracks(string path)
    {
        return HootArchive.Load(ChipSongs.FilePart(path)).Titles.ToList();
    }

    public static VgmSong Load(string path)
    {
        string file = ChipSongs.FilePart(path);
        NezM3u.SplitTrack(path, out int n);
        var e = HootArchive.Load(file);
        string driver = e.Driver.ToLowerInvariant();
        if (driver == "pc98dos") return Dos.HootPc98.Load(path);
        if (!HootArchive.Playable(e.Driver, e.Type)) throw new InvalidDataException("아직 재생하지 못하는 hoot 항목입니다: " + e.Driver + " / " + e.Type);
        if (e.Titles.Count == 0) throw new InvalidDataException("hoot 항목에 곡 목록(titlelist)이 없습니다.");
        n = Math.Max(0, Math.Min(n, e.Titles.Count - 1));
        var (code, title) = e.Titles[n];
        string dir = Path.GetDirectoryName(Path.GetFullPath(file));
        VgmSong v;
        string what;
        if (driver == "snes")
        {
            var rom = e.Roms.FirstOrDefault(r => r.Offset == code && r.Text.Length > 0);
            if (rom.Text == null) throw new InvalidDataException("곡 코드 0x" + code.ToString("X") + "의 SPC 파일이 목록에 없습니다.");
            string spc = FindFile(dir, rom.Text) ?? throw new FileNotFoundException("SPC 파일이 zip에 없습니다: " + rom.Text);
            v = Spc.Load(spc);
            what = "SPC " + Path.GetFileName(spc);
        }
        else
        {
            string ext = driver == "msx" ? ".kss" : driver == "nes" ? ".nsf" : null;
            var rom = e.Roms.FirstOrDefault(r => r.Text.Length > 0 && (ext != null
                ? string.Equals(Path.GetExtension(r.Text), ext, StringComparison.OrdinalIgnoreCase)
                : Gbs.Extensions.Any(x => string.Equals(Path.GetExtension(r.Text), x, StringComparison.OrdinalIgnoreCase))));
            if (rom.Text == null) rom = e.Roms.FirstOrDefault(r => r.Text.Length > 0);
            if (rom.Text == null) throw new InvalidDataException("hoot 항목에 곡 파일(rom)이 없습니다.");
            string f = FindFile(dir, rom.Text) ?? throw new FileNotFoundException("곡 파일이 zip에 없습니다: " + rom.Text);
            var bytes = File.ReadAllBytes(f);
            if (driver == "msx")
            {
                var t = Kss.Tracks(f).FirstOrDefault(x => x.Song == code);
                v = Kss.Render(bytes, code, t?.Seconds ?? Kss.DefaultSeconds, t?.Fade ?? Kss.DefaultFade);
                what = "KSS";
            }
            else if (driver == "nes")
            {
                var t = Nsf.Tracks(f).FirstOrDefault(x => x.Song == code);
                v = Nsf.Render(bytes, code, t?.Seconds ?? Nsf.DefaultSeconds, t?.Fade ?? Nsf.DefaultFade);
                var h = Nsf.ReadHeader(bytes);
                v.GameEn = h.Title; v.AuthorEn = h.Artist;
                what = "NSF";
            }
            else
            {
                var t = Gbs.TracksKnown(f) ? Gbs.Tracks(f).FirstOrDefault(x => x.Song == code) : null;
                v = Gbs.Render(bytes, code, t?.Seconds ?? Gbs.DefaultSeconds, t?.Fade ?? Gbs.DefaultFade);
                what = "GBS";
            }

            what += " " + Path.GetFileName(f) + " · 곡 0x" + code.ToString("X");
        }
        v.FilePath = path;
        if (title.Length > 0) v.TitleEn = title;
        if (v.GameEn.Length == 0) v.GameEn = e.Name;
        v.SourceFormat = "hoot " + e.Driver + " / " + e.Type + " · " + what + " (" + (n + 1) + "/" + e.Titles.Count + ")";
        return v;
    }

    public static string FindFile(string dir, string name) { return HootArchive.FindFile(dir, name); }
}
