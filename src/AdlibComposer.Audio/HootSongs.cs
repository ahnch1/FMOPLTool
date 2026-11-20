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
        else if (HootSegaPcmBoard.Handles(driver, e.Type))
        {
            // hoot 세가 PCM 보드 (Out Run · X · Y · Hang-On 계열): 사운드 보드 (Z80 + YM2151 / YM2203 + 세가 PCM)에서 사운드 ROM을 돌려 미리 렌더
            v = HootSegaPcmBoard.Render(e, dir, code);
            what = "세가 PCM 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootCps2.Handles(driver, e.Type))
        {
            // hoot 캡콤 CPS-2: 사운드 보드 (Z80 + QSound)에서 사운드 ROM을 돌려 미리 렌더 (QSound는 DSP 없는 HLE)
            v = HootCps2.Render(e, dir, code);
            what = "CPS-2 사운드 보드 (QSound) · 명령 0x" + code.ToString("X2");
        }
        else if (HootSegaSystem16.Handles(driver, e.Type))
        {
            // hoot 세가 System 16A · 16B: 사운드 보드 (Z80 + YM2151 + uPD7759)에서 사운드 ROM을 돌려 미리 렌더
            v = HootSegaSystem16.Render(e, dir, code);
            what = "세가 System 16 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootMegaSys1.Handles(driver, e.Type))
        {
            // hoot 잘레코 Mega System 1: 사운드 보드 (68000 + YM2151 + OKIM6295 둘)에서 사운드 프로그램을 돌려 미리 렌더
            v = HootMegaSys1.Render(e, dir, code);
            what = "Mega System 1 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootDeco6502.Handles(driver, e.Type))
        {
            // hoot 데이터 이스트 6502 보드: 사운드 보드 (6502 + YM2203 + YM3812/YM3526 (+ OKIM6295))에서 사운드 ROM을 돌려 미리 렌더
            v = HootDeco6502.Render(e, dir, code);
            what = "데이터 이스트 6502 사운드 보드 · 명령 0x" + code.ToString("X2");
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
        else if (HootTecmoAdpcm.Handles(driver, e.Type))
        {
            // hoot 테크모 Rygar · Gemini Wing 계열: 사운드 보드 (Z80 + YM3812 + MSM5205)에서 사운드 ROM을 돌려 미리 렌더
            v = HootTecmoAdpcm.Render(e, dir, code);
            what = HootTecmoAdpcm.LayoutOf(driver, e.Type).Name + " 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootGaiden.Handles(driver, e.Type))
        {
            // hoot 테크모 Ninja Gaiden: 사운드 보드 (Z80 + YM2203 × 2 + OKIM6295)에서 사운드 ROM을 돌려 미리 렌더
            v = HootGaiden.Render(e, dir, code);
            what = "Ninja Gaiden 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootYm2610Board.Handles(driver, e.Type))
        {
            // hoot 비디오시스템 · 사이쿄: 사운드 CPU (Z80 + YM2610)에서 ROM을 돌려 미리 렌더
            v = HootYm2610Board.Render(e, dir, code);
            what = HootYm2610Board.LayoutOf(driver, e.Type).Name + " 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootKonamiGx.Handles(driver, e.Type))
        {
            // hoot 코나미 System GX: 사운드 CPU (68000 + K054539 × 2)에서 ROM을 돌려 미리 렌더
            v = HootKonamiGx.Render(e, dir, code);
            what = "Konami System GX 사운드 · 명령 0x" + code.ToString("X3");
        }
        else if (HootSegaSystem18.Handles(driver, e.Type))
        {
            // hoot 세가 System 18: 사운드 CPU (Z80 + YM3438 × 2 + RF5C68)에서 ROM을 돌려 미리 렌더
            v = HootSegaSystem18.Render(e, dir, code);
            what = "Sega System 18 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootKonami54539.Handles(driver, e.Type))
        {
            // hoot 코나미 054539: 사운드 CPU (Z80 + YM2151 + K054539 × 1~2)에서 ROM을 돌려 미리 렌더
            v = HootKonami54539.Render(e, dir, code);
            what = "Konami K054539 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootKonami53260.Handles(driver, e.Type))
        {
            // hoot 코나미 053260: 사운드 CPU (Z80 + YM2151 + K053260)에서 ROM을 돌려 미리 렌더
            v = HootKonami53260.Render(e, dir, code);
            what = "Konami K053260 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootNamcoSys2.Handles(driver, e.Type))
        {
            // hoot 남코 System 2: 사운드 CPU (6809 + YM2151 + C140)에서 ROM을 돌려 미리 렌더
            v = HootNamcoSys2.Render(e, dir, code);
            what = "Namco System 2 사운드 보드 · 곡 0x" + code.ToString("X2");
        }
        else if (HootTaitoSj.Handles(driver, e.Type))
        {
            // hoot 타이토 SJ: 사운드 CPU (Z80 + AY × 3 + DAC)에서 ROM을 돌려 미리 렌더
            v = HootTaitoSj.Render(e, dir, code);
            what = "Taito SJ 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootKonamiAy.Handles(driver, e.Type))
        {
            // hoot 코나미 scramble · timeplt: 사운드 CPU (Z80 + AY-3-8910 × 2)에서 ROM을 돌려 미리 렌더
            v = HootKonamiAy.Render(e, dir, code);
            what = "Konami Z80 + AY × 2 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootIremM62.Handles(driver, e.Type))
        {
            // hoot 아이렘 M62 / M52: 사운드 CPU (M6803 + AY-3-8910 × 2)에서 ROM을 돌려 미리 렌더
            v = HootIremM62.Render(e, dir, code);
            what = "Irem M62 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootNamcoSys86.Handles(driver, e.Type))
        {
            // hoot 남코 System 86: 사운드 MCU (HD63701 + YM2151 + CUS30)에서 ROM을 돌려 미리 렌더
            v = HootNamcoSys86.Render(e, dir, code);
            what = "Namco System 86 MCU · 곡 0x" + code.ToString("X2");
        }
        else if (HootNamcoWsg6809.Handles(driver, e.Type))
        {
            // hoot 남코 매피 계열: 사운드 CPU (6809 + 15XX)에서 사운드 ROM을 돌려 미리 렌더
            v = HootNamcoWsg6809.Render(e, dir, code);
            what = "Namco 6809 + 15XX 사운드 CPU · 소리 0x" + code.ToString("X2");
        }
        else if (HootNamcoSys1.Handles(driver, e.Type))
        {
            // hoot 남코 System 1: 사운드 보드 (6809 + YM2151 + CUS30)에서 사운드 ROM을 돌려 미리 렌더 (CUS30 파형은 아직)
            v = HootNamcoSys1.Render(e, dir, code);
            what = "Namco System 1 사운드 보드 · 곡 0x" + code.ToString("X2");
        }
        else if (HootDeco6809.Handles(driver, e.Type))
        {
            // hoot 데이터 이스트 brkthru · exprraid: 사운드 보드 (6809 + YM2203 + YM3526)에서 사운드 ROM을 돌려 미리 렌더
            v = HootDeco6809.Render(e, dir, code);
            what = HootDeco6809.LayoutOf(driver, e.Type).Name + " 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootAtariSys1.Handles(driver, e.Type))
        {
            // hoot 아타리 System 1: 사운드 보드 (6502 + YM2151 + POKEY)에서 사운드 ROM을 돌려 미리 렌더 (TMS5220 음성 없음)
            v = HootAtariSys1.Render(e, dir, code);
            what = "Atari System 1 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootCapcomOpn.Handles(driver, e.Type))
        {
            // hoot 캡콤 opn2 · rushcrsh · tigerroad: 사운드 보드 (Z80 + YM2203 × 2)에서 사운드 ROM을 돌려 미리 렌더
            v = HootCapcomOpn.Render(e, dir, code);
            what = HootCapcomOpn.LayoutOf(driver, e.Type, e.Name).Name + " 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootUpl.Handles(driver, e.Type))
        {
            // hoot UPL robokid 계열: 사운드 보드 (Z80 + YM2203 × 2)에서 사운드 ROM을 돌려 미리 렌더
            v = HootUpl.Render(e, dir, code);
            what = "UPL 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootAlpha68k.Handles(driver, e.Type))
        {
            // hoot 알파 Alpha 68k II / V: 사운드 보드 (Z80 + YM2203 + YM2413 + DAC)에서 사운드 ROM을 돌려 미리 렌더
            v = HootAlpha68k.Render(e, dir, code);
            what = "Alpha 68k 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootNichibutsu.Handles(driver, e.Type))
        {
            // hoot 니치부츠 terracre · terraf: 사운드 보드 (Z80 + YM3526/YM3812 + DAC)에서 사운드 ROM을 돌려 미리 렌더
            v = HootNichibutsu.Render(e, dir, code);
            what = "니치부츠 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootSnk68.Handles(driver, e.Type))
        {
            // hoot SNK68: 사운드 보드 (Z80 + YM3812 + uPD7759)에서 사운드 ROM을 돌려 미리 렌더
            v = HootSnk68.Render(e, dir, code);
            what = "SNK68 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootToaplan.Handles(driver, e.Type))
        {
            // hoot 토아플랜 1 · Twin Cobra: 사운드 보드 (Z80 + YM3812, 같이 쓰는 RAM 명령)에서 사운드 ROM을 돌려 미리 렌더
            v = HootToaplan.Render(e, dir, code);
            what = "토아플랜 사운드 보드 · 명령 0x" + code.ToString("X2");
        }
        else if (HootTaitoOpm.Handles(driver, e.Type))
        {
            // hoot 타이토 YM2151 보드 (fullt): 사운드 보드 (Z80 + YM2151 + MSM5205 + TC0140SYT)에서 사운드 ROM을 돌려 미리 렌더
            v = HootTaitoOpm.Render(e, dir, code);
            what = "타이토 YM2151 사운드 보드 · 명령 0x" + code.ToString("X2");
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
