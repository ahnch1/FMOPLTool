using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// QSF · MINIQSF (캡콤 QSound 사운드 덤프, PSF 판 41h): Z80 + QSound. <see cref="PsfFile"/>로 _lib까지 읽어 프로그램 영역의 구역을 모은다
/// (참고: modizer HighlyQuixotic · ModizMusicPlayer qsf_loader).
/// <para>프로그램 = 구역 잇달아: 이름 3바이트 ("KEY" · "Z80" · "SMP") + 시작 (4) + 크기 (4) + 자료. 같은 이름은 시작 자리에 겹쳐 쓴다 (라이브러리 위에 미니 파일이 곡 번호만 고침).
/// KEY 11바이트 = Kabuki 열쇠 (swap1 · swap2 빅 엔디언 32비트, 주소 16비트, XOR 8비트 — 모두 0이면 암호 없음), Z80 = 사운드 롬 (8000h~ 뱅크), SMP = QSound 샘플 롬.</para>
/// <para>기계는 hoot CPS-2와 같은 것 (<see cref="HootCps2.Machine"/>: Z80 8MHz · IRQ 250Hz · D000h~D003h QSound · 뱅크), 공유 RAM C000h~는 0에서 시작하고
/// 68000 흉내는 하지 않는다 (덤프한 롬이 곡을 스스로 고른다). 길이 = 태그 length · fade, 없으면 3분.</para>
/// </summary>
public static class Qsf
{
    public static readonly string[] Extensions = { ".qsf", ".miniqsf" };
    public const int PsfVersion = 0x41;
    public const double DefaultSeconds = 180, DefaultFade = 10;

    public sealed class Image
    {
        public byte[] Key = new byte[0], Z80 = new byte[0], Sample = new byte[0];
        public string Title = "", Artist = "", Game = "", Year = "", Copyright = "", Ripper = "", Comment = "";
        public double Seconds, Fade = -1;
        public int Parts;
    }

    /// <summary>프로그램 영역 구역들을 image에 올린다.</summary>
    public static void Upload(Image image, byte[] exe)
    {
        int p = 0;
        while (exe.Length - p >= 11)
        {
            string name = System.Text.Encoding.ASCII.GetString(exe, p, 3);
            long start = (uint)(exe[p + 3] | (exe[p + 4] << 8) | (exe[p + 5] << 16) | (exe[p + 6] << 24));
            long size = (uint)(exe[p + 7] | (exe[p + 8] << 8) | (exe[p + 9] << 16) | (exe[p + 10] << 24));
            p += 11;
            if (size > exe.Length - p) throw new InvalidDataException("QSF 구역이 프로그램 밖입니다: " + name);
            switch (name)
            {
                case "KEY": if (start + size > 11) throw new InvalidDataException("QSF KEY가 11바이트보다 깁니다."); image.Key = Put(image.Key, start, exe, p, size); break;
                case "Z80": image.Z80 = Put(image.Z80, start, exe, p, size); break;
                case "SMP": image.Sample = Put(image.Sample, start, exe, p, size); break;
                default: throw new InvalidDataException("QSF 구역 이름을 모릅니다: " + name);
            }
            p += (int)size;
        }
    }

    private static byte[] Put(byte[] arr, long start, byte[] src, int at, long size)
    {
        if (start + size > 64L << 20) throw new InvalidDataException("QSF 구역이 너무 큽니다.");
        if (start + size > arr.Length) Array.Resize(ref arr, (int)(start + size));
        Array.Copy(src, at, arr, start, size);
        return arr;
    }

    public static Image Read(string path)
    {
        var chain = PsfFile.LoadChain(path, PsfVersion, out var main);
        var img = new Image { Parts = chain.Count };
        foreach (var part in chain) Upload(img, part.Program);
        img.Title = main.Tag("title"); img.Artist = main.Tag("artist"); img.Game = main.Tag("game");
        img.Year = main.Tag("year"); img.Copyright = main.Tag("copyright"); img.Ripper = main.Tag("qsfby"); img.Comment = main.Tag("comment");
        if (img.Ripper.Length == 0) img.Ripper = main.Tag("psfby");
        img.Seconds = PsfFile.ParseLength(main.Tag("length"));
        string fade = main.Tag("fade");
        if (fade.Length > 0) img.Fade = PsfFile.ParseLength(fade);
        return img;
    }

    /// <summary>KEY 11바이트 → Kabuki 열쇠 (모두 0이면 null = 암호 없음).</summary>
    public static HootCps2.Kabuki.Key? KeyOf(byte[] key)
    {
        if (key.Length < 11) return null;
        int swap1 = (key[0] << 24) | (key[1] << 16) | (key[2] << 8) | key[3];
        int swap2 = (key[4] << 24) | (key[5] << 16) | (key[6] << 8) | key[7];
        int addr = (key[8] << 8) | key[9];
        if (swap1 == 0 && swap2 == 0) return null;
        return new HootCps2.Kabuki.Key("QSF", swap1, swap2, addr, key[10]);
    }

    public static VgmSong Render(Image img, double seconds, double fade)
    {
        if (img.Z80.Length == 0) throw new InvalidDataException("QSF에 Z80 롬이 없습니다.");
        var rom = (byte[])img.Z80.Clone();
        if (rom.Length < 0x8000) Array.Resize(ref rom, 0x8000);
        byte[] ops = null;
        var k = KeyOf(img.Key);
        if (k != null) ops = HootCps2.Kabuki.Decode(rom, k.Value, 0x8000);
        var m = new HootCps2.Machine(rom, img.Sample, HootCps2.Board.Cps2, ops) { PlainShared = true };
        Array.Clear(m.Shared, 0, m.Shared.Length);
        m.RunSeconds(seconds);
        var v = m.ToVgm(seconds, fade);
        v.Notes = "QSF (QSound HLE)";
        v.SystemEn = "Capcom QSound (QSF)";
        if (m.Log.Count == 0 && !v.UnsupportedChips.Contains("(칩 쓰기 없음)")) v.UnsupportedChips.Add("(칩 쓰기 없음)");
        return v;
    }

    public static VgmSong Load(string path)
    {
        var img = Read(path);
        double seconds = img.Seconds > 0 ? img.Seconds : DefaultSeconds;
        double fade = img.Fade >= 0 ? img.Fade : DefaultFade;
        // length = 페이드 앞까지 (PSF 관례) → 페이드를 붙여 돌린다
        var v = Render(img, seconds + fade, fade);
        v.FilePath = path;
        v.TitleEn = img.Title.Length > 0 ? img.Title : Path.GetFileNameWithoutExtension(path);
        v.GameEn = img.Game;
        v.AuthorEn = img.Artist;
        v.ReleaseDate = img.Year;
        v.Ripper = img.Ripper;
        v.SourceFormat = "QSF" + (img.Parts > 1 ? " (+ 라이브러리 " + (img.Parts - 1) + ")" : "") + (KeyOf(img.Key) != null ? " · Kabuki" : "");
        return v;
    }

    /// <summary>목록 제목 (돌리지 않고 태그만).</summary>
    public static string Title(string path)
    {
        try { var p = PsfFile.Read(File.ReadAllBytes(path), path); return p.Tag("title"); }
        catch (Exception) { return ""; }
    }
}
