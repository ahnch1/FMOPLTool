using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 항목 파일 (<see cref="HootArchive.Extension"/> = 항목 XML 하나, 같은 폴더 = zip을 푼 내용) → 곡. 곡 목록 = titlelist (range 뺌), "파일::n" = n번째 제목.
/// <para>드라이버별로: msx generic = MSX 기계 흉내 (<see cref="HootMsx"/>), pc98dos = PC-98 DOS 흉내 (<see cref="Dos.HootPc98"/>), snes spc = rom type="data" offset이 곡 코드인 .spc,
/// msx kss · nes nsf · gb gbs = rom의 그 파일에서 곡 코드 번호의 곡 (길이 · 페이드는 같은 이름 m3u에 그 곡이 있으면 그것, 없으면 그 형식 기본값).
/// 제목은 hoot 목록의 것 (트리 · 파일 목록과 같게).</para>
/// </summary>
public static class HootSongs
{
    public static readonly string[] Extensions = { HootArchive.Extension };

    /// <summary>MIDI로 내는 hoot 항목 (pc98dos · pcatdos · x68k + midiout): Jukebox는 MIDI 재생으로 (<see cref="LoadMidi"/>).</summary>
    public static bool IsMidi(string path)
    {
        string file = ChipSongs.FilePart(path);
        if (file == null || !Extensions.Any(x => string.Equals(Path.GetExtension(file), x, StringComparison.OrdinalIgnoreCase)) || !File.Exists(file)) return false;
        try { return Dos.HootPc98.IsMidiEntry(HootArchive.Load(file)); }
        catch (Exception) { return false; }
    }

    public static MidiSong LoadMidi(string path)
    {
        var e = HootArchive.Load(ChipSongs.FilePart(path));
        return e.Driver.Equals("x68k", StringComparison.OrdinalIgnoreCase) ? HootX68k.LoadMidi(path) : Dos.HootPc98.LoadMidi(path);
    }

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
        if (driver == "pc98dos" || driver == "pcatdos" || HootArchive.IsPc98Generic(driver)) return Dos.HootPc98.Load(path);
        if (!HootArchive.Playable(e.Driver, e.Type)) throw new InvalidDataException("아직 재생하지 못하는 hoot 항목입니다: " + e.Driver + " / " + e.Type);
        if (e.Titles.Count == 0) throw new InvalidDataException("hoot 항목에 곡 목록(titlelist)이 없습니다.");
        n = Math.Max(0, Math.Min(n, e.Titles.Count - 1));
        var (code, title) = e.Titles[n];
        string dir = Path.GetDirectoryName(Path.GetFullPath(file));
        VgmSong v;
        string what;
        if (driver == "msx" && e.Type.Equals("generic", StringComparison.OrdinalIgnoreCase))
        {
            // hoot MSX 범용 드라이버: 시작 코드 · 드라이버 · 곡 데이터를 MSX 기계에서 돌려 미리 렌더
            v = HootMsx.Render(e, dir, code);
            what = "MSX 기계 · 곡 0x" + code.ToString("X");
        }
        else if (driver == "pc88")
        {
            // hoot PC-8801: 시작 코드 · 드라이버 · 곡 데이터를 PC-88 기계에서 돌려 미리 렌더
            v = HootPc88.Render(e, dir, code);
            what = "PC-88 기계 · 곡 0x" + code.ToString("X");
        }
        else if (driver == "x1")
        {
            // hoot X1: 시작 코드 · 드라이버 · 곡 데이터를 X1 기계(Z80 · CTC · PSG · OPM)에서 돌려 미리 렌더
            v = HootX1.Render(e, dir, code);
            what = "X1 기계 · 곡 0x" + code.ToString("X");
        }
        else if (driver == "neogeo")
        {
            // hoot 네오지오: 사운드 보드 (Z80 + YM2610)에서 M1 ROM을 돌려 미리 렌더 (ADPCM ROM은 VGM 데이터 블록으로)
            v = HootNeoGeo.Render(e, dir, code);
            what = "네오지오 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootDeco.Handles(driver, e.Type))
        {
            // hoot 데이터 이스트: 사운드 보드 (HuC6280 + YM2151 (+ YM2203) + OKIM6295 둘)에서 사운드 ROM을 돌려 미리 렌더
            v = HootDeco.Render(e, dir, code);
            what = "데이터 이스트 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootSegaSystem1.Handles(driver, e.Type))
        {
            // hoot 세가 System 1: 사운드 보드 (Z80 + SN76489 2MHz · 4MHz)에서 사운드 ROM을 돌려 미리 렌더
            v = HootSegaSystem1.Render(e, dir, code);
            what = "세가 System 1 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootM72.Handles(driver, e.Type))
        {
            // hoot 아이렘 M72: 사운드 보드 (Z80 + YM2151)에서 사운드 프로그램을 돌려 미리 렌더 (DAC 샘플 빠짐)
            v = HootM72.Render(e, dir, code);
            what = "아이렘 M72 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootOpmBoard.Handles(driver, e.Type))
        {
            // hoot 아케이드 Z80 + YM2151 + OKIM6295 보드 (CPS-1 · Double Dragon 2 · 3): 사운드 보드에서 사운드 ROM을 돌려 미리 렌더
            v = HootOpmBoard.Render(e, dir, code);
            what = HootOpmBoard.LayoutOf(driver, e.Type).Name + " 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (driver == "taito")
        {
            // hoot 타이토 YM2610 보드: 사운드 보드 (Z80 + YM2610 + TC0140SYT)에서 사운드 ROM을 돌려 미리 렌더
            v = HootTaito.Render(e, dir, code);
            what = "타이토 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (driver == "megadrive")
        {
            // hoot 메가드라이브 Z80 드라이버: 카트리지 ROM의 Z80 드라이버를 Z80 RAM에 올려 사운드 쪽만 흉내 내 미리 렌더
            v = HootMegaDrive.Render(e, dir, code);
            what = "메가드라이브 Z80 · 곡 0x" + code.ToString("X2");
        }
        else if (driver == "x68k")
        {
            // hoot X68000: 시작 코드 · 드라이버 (.x 재배치) · 곡 데이터를 X68000 기계 (MC68000 · OPM · ADPCM · DMA · MFP)에서 돌려 미리 렌더
            v = HootX68k.Render(e, dir, code);
            what = "X68000 기계 · 곡 0x" + code.ToString("X");
        }
        else if (driver == "snes")
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
