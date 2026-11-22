using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// SGC (세가 마스터 시스템 · 게임 기어 · 콜레코비전 사운드 드라이버 덤프): Z80 코드 + 데이터. 드라이버를 Z80으로 돌려
/// SN76489 (+ 게임 기어 스테레오, 마스터 시스템 FM 유닛 YM2413) 쓰기를 VGM(<see cref="VgmSong"/>)으로 모은다 (참고: modizer libnez <c>m_sgc.c</c>).
/// <para>머리말 (0xA0바이트): "SGC" 1Ah, 0x05 PAL(bit0), 0x08 읽어 둘 주소, 0x0A init, 0x0C play, 0x0E 스택, 0x12~0x1F RST 08h~38h 주소 7개,
/// 0x20~0x23 매퍼 처음 값 (FFFCh~FFFFh), 0x24 첫 곡 (0부터), 0x25 곡 수, 0x26 · 0x27 첫 · 끝 효과음, 0x28 기종 (0 SMS · 1 GG · 2 콜레코비전),
/// 0x40 제목 · 0x60 작곡 · 0x80 저작권 (32바이트씩).</para>
/// <para>SMS · GG: 0000~03FF = 작은 BIOS (RST = 머리말 주소로 JP), 0400~3FFF · 4000~7FFF · 8000~BFFF = 16KB 뱅크 (FFFDh~FFFFh에 쓰면 바뀜, FFFCh bit3 = 8000h에 RAM),
/// C000~FFFF = RAM 8KB (반복). 포트 7Eh/7Fh = SN76489, 06h = GG 스테레오, F0h/F1h = FM 유닛 (SMS).
/// 콜레코비전: 0000~1FFF = BIOS (<c>resource\coleco.rom</c> 있으면, 없으면 RET), 6000~7FFF = RAM, 8000~FFFF = 롬, 포트 E0h~FFh = SN76489.</para>
/// <para>돌리는 방법 (libnez와 같이): 0000h에 CALL init · HALT · JR $ 틀, A = 곡 번호, SP = 머리말 스택 → HALT까지. 그 뒤 화면마다 (NTSC 342 × 262 / 1.5 · PAL 313줄 사이클)
/// HALT 중이면 CALL play 틀로 다시. 곡 길이 = 같은 이름 .m3u (종류 SGC), 없으면 2분.</para>
/// </summary>
public static class Sgc
{
    public static readonly string[] Extensions = { ".sgc" };
    public const double DefaultSeconds = 120, DefaultFade = 4;
    public const double CpuClock = 3579545;
    public static readonly string[] SystemNames = { "Sega Master System", "Game Gear", "ColecoVision" };

    public sealed class Header
    {
        public bool Pal;
        public int LoadAddr, InitAddr, PlayAddr, StackAddr;
        public readonly int[] Rst = new int[7];
        public readonly byte[] Mapper = new byte[4];
        public int StartSong, Songs, FirstSfx, LastSfx, System;
        public string Title = "", Artist = "", Copyright = "";
        /// <summary>곡 + 효과음 수.</summary>
        public int TotalSongs { get { return Songs + (FirstSfx > 0 ? LastSfx - FirstSfx + 1 : 0); } }
        public byte[] Data;
    }

    public sealed class Track
    {
        /// <summary>init에 넘기는 번호 (효과음은 FirstSfx부터).</summary>
        public int Song;
        public string Title = "";
        public double Seconds = DefaultSeconds, Fade = DefaultFade;
    }

    public static Header ReadHeader(byte[] d)
    {
        if (d.Length < 0xA0 || d[0] != 'S' || d[1] != 'G' || d[2] != 'C') throw new InvalidDataException("SGC 파일이 아닙니다 (머리말 SGC가 없음).");
        var h = new Header
        {
            Pal = (d[5] & 1) != 0,
            LoadAddr = U16(d, 8), InitAddr = U16(d, 0x0A), PlayAddr = U16(d, 0x0C), StackAddr = U16(d, 0x0E),
            StartSong = d[0x24], Songs = Math.Max(1, (int)d[0x25]), FirstSfx = d[0x26], LastSfx = d[0x27], System = d[0x28],
            Title = Text(d, 0x40), Artist = Text(d, 0x60), Copyright = Text(d, 0x80),
        };
        for (int i = 0; i < 7; i++) h.Rst[i] = U16(d, 0x12 + i * 2);
        Array.Copy(d, 0x20, h.Mapper, 0, 4);
        if (h.System > 2) throw new InvalidDataException("SGC 기종 번호가 이상합니다: " + h.System);
        if (h.FirstSfx < h.Songs || h.FirstSfx > h.LastSfx || h.FirstSfx == 0) h.FirstSfx = h.LastSfx = 0;
        h.Data = new byte[d.Length - 0xA0];
        Array.Copy(d, 0xA0, h.Data, 0, h.Data.Length);
        return h;
    }

    private static string Text(byte[] d, int at)
    {
        int n = 0;
        while (n < 32 && d[at + n] != 0) n++;
        string s = KoreanText.ShiftJis.GetString(d, at, n).Trim();
        return s == "<?>" ? "" : s;
    }

    // ------------------------------------------------------------ 곡 목록 · 열기

    public static List<Track> Tracks(string path)
    {
        var list = new List<Track>();
        foreach (var e in NezM3u.Read(path, "SGC"))
        {
            var t = new Track { Song = e.Number, Title = e.Title };
            if (e.Seconds > 0) t.Seconds = e.Seconds;
            if (e.Fade >= 0) t.Fade = e.Fade;
            list.Add(t);
        }
        if (list.Count > 0) return list;
        Header h;
        try { h = ReadHeader(File.ReadAllBytes(path)); }
        catch (Exception) { list.Add(new Track()); return list; }
        return Tracks(h, Path.GetFileNameWithoutExtension(path));
    }

    public static List<Track> Tracks(Header h, string fallbackName)
    {
        var list = new List<Track>();
        string name = h.Title.Length > 0 ? h.Title : fallbackName;
        for (int s = 0; s < h.Songs; s++) list.Add(new Track { Song = s, Title = name + " #" + (s + 1) });
        if (h.FirstSfx > 0)
            for (int s = h.FirstSfx; s <= h.LastSfx; s++) list.Add(new Track { Song = s, Title = name + " SFX " + (s - h.FirstSfx + 1), Seconds = 10, Fade = 0.5 });
        return list;
    }

    /// <summary>"파일.sgc" 또는 "파일.sgc::n" (n = 곡 목록 안 번호, 0부터).</summary>
    public static VgmSong Load(string path)
    {
        path = NezM3u.SplitTrack(path, out int track);
        var tracks = Tracks(path);
        var t = tracks[Math.Max(0, Math.Min(track, tracks.Count - 1))];
        var bytes = File.ReadAllBytes(path);
        var h = ReadHeader(bytes);
        var v = Render(bytes, t.Song, t.Seconds, t.Fade, ColecoBios());
        v.FilePath = path;
        v.TitleEn = t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(path) + " #" + (t.Song + 1);
        v.GameEn = h.Title;
        v.AuthorEn = h.Artist;
        v.ReleaseDate = h.Copyright;
        v.SystemEn = SystemNames[h.System];
        v.SourceFormat = "SGC " + SystemNames[h.System] + " · 곡 " + (t.Song + 1) + " (" + (track + 1) + "/" + tracks.Count + ")";
        return v;
    }

    /// <summary>콜레코비전 BIOS (resource\coleco.rom 등, 8KB) — 없으면 null.</summary>
    public static byte[] ColecoBios()
    {
        foreach (var dir in new[] { Path.Combine(AppContext.BaseDirectory, "resource"), AppContext.BaseDirectory })
            foreach (var name in new[] { "coleco.rom", "colecovision.rom", "coleco.bin" })
            {
                string p = Path.Combine(dir, name);
                if (File.Exists(p)) return File.ReadAllBytes(p);
            }
        return null;
    }

    // ------------------------------------------------------------ 돌리기

    private sealed class Machine : IZ80Bus
    {
        private readonly Header h;
        private readonly bool cv;
        private readonly byte[] rom;          // 뱅크 이미지 (SMS · GG: 16KB 단위, 데이터는 LoadAddr부터 / CV: 8000h부터 32KB)
        private readonly byte[] bios;         // SMS · GG 0000~03FF, CV 0000~1FFF
        private readonly byte[] ram = new byte[0x2000];
        private readonly byte[] cartRam = new byte[0x4000];
        private readonly byte[] mapper = new byte[4];
        private readonly int banks;
        public Z80 Cpu;
        public readonly List<(double t, byte[] cmd)> Log = new List<(double, byte[])>();
        public bool UsedSn, UsedFm, UsedStereo;
        private int fmAddr;

        public Machine(Header h, byte[] colecoBios)
        {
            this.h = h;
            cv = h.System == 2;
            if (cv)
            {
                rom = new byte[0x8000];
                int at = h.LoadAddr - 0x8000;
                if (at >= 0) Array.Copy(h.Data, 0, rom, at, Math.Min(h.Data.Length, rom.Length - at));
                bios = new byte[0x2000];
                for (int i = 0; i < bios.Length; i++) bios[i] = 0xC9;
                if (colecoBios != null) Array.Copy(colecoBios, bios, Math.Min(colecoBios.Length, bios.Length));
            }
            else
            {
                banks = Math.Max(3, ((h.Data.Length + h.LoadAddr) >> 14) + 1);
                rom = new byte[banks * 0x4000];
                Array.Copy(h.Data, 0, rom, h.LoadAddr, Math.Min(h.Data.Length, rom.Length - h.LoadAddr));
                bios = new byte[0x400];
                for (int i = 1; i < 8; i++) { bios[i * 8] = 0xC3; bios[i * 8 + 1] = (byte)h.Rst[i - 1]; bios[i * 8 + 2] = (byte)(h.Rst[i - 1] >> 8); }
                Array.Copy(h.Mapper, mapper, 4);
            }
        }

        /// <summary>0000h에 CALL addr · HALT · JR $ 틀을 놓고 거기서 시작.</summary>
        public void Setup(int addr)
        {
            bios[0] = 0xCD; bios[1] = (byte)addr; bios[2] = (byte)(addr >> 8);
            bios[3] = 0x76; bios[4] = 0x18; bios[5] = 0xFE;
            Cpu.PC = 0;
            Cpu.Halted = false;
        }

        private byte Bank(int slot, int a)
        {
            int b = mapper[slot] % banks;
            return rom[b * 0x4000 + (a & 0x3FFF)];
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (cv)
            {
                if (a < 0x2000) return bios[a];
                if (a >= 0x6000 && a < 0x8000) return ram[a & 0x3FF];
                if (a >= 0x8000) return rom[a - 0x8000];
                return 0xFF;
            }
            if (a < 0x400) return bios[a];
            if (a >= 0xFFFC) return mapper[a & 3];
            if (a < 0x4000) return Bank(1, a);
            if (a < 0x8000) return Bank(2, a);
            if (a < 0xC000) return (mapper[0] & 0x08) != 0 ? cartRam[a & 0x3FFF] : Bank(3, a);
            return ram[a & 0x1FFF];
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (cv)
            {
                if (a >= 0x6000 && a < 0x8000) ram[a & 0x3FF] = v;
                return;
            }
            if (a >= 0xFFFC) mapper[a & 3] = v;
            if (a >= 0xC000) { ram[a & 0x1FFF] = v; return; }
            if (a >= 0x8000 && (mapper[0] & 0x08) != 0) cartRam[a & 0x3FFF] = v;
        }

        public byte In(int port) { return 0xFF; }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            if (cv)
            {
                if ((port & 0xE0) == 0xE0) { UsedSn = true; Emit(0x50, v); }
                return;
            }
            if ((port & 0xFE) == 0x7E) { UsedSn = true; Emit(0x50, v); }
            else if (port == 0x06 && h.System == 1) { UsedStereo = true; Emit(0x4F, v); }
            else if (h.System == 0 && port == 0xF0) fmAddr = v;
            else if (h.System == 0 && port == 0xF1) { UsedFm = true; Emit(0x51, (byte)fmAddr, v); }
        }

        private void Emit(params byte[] cmd) { Log.Add((Cpu.Cycles * VgmSong.Rate / CpuClock, cmd)); }
    }

    /// <summary>곡 하나(song = init에 넘기는 번호)를 seconds초 돌려 VGM으로.</summary>
    public static VgmSong Render(byte[] file, int song, double seconds, double fade = DefaultFade, byte[] colecoBios = null)
    {
        var h = ReadHeader(file);
        var m = new Machine(h, colecoBios);
        var cpu = new Z80(m);
        m.Cpu = cpu;
        cpu.A = song & 0xFF; cpu.H = (song >> 8) & 0xFF;
        cpu.SP = h.StackAddr;
        cpu.Im = 1; cpu.Iff1 = cpu.Iff2 = false;
        m.Setup(h.InitAddr);
        long limit = (long)(CpuClock * 5);
        while (!cpu.Halted && cpu.Cycles < limit) cpu.Step();

        long frameCycles = h.Pal ? 4 * 342 * 313 / 6 : 4 * 342 * 262 / 6;
        long total = (long)(seconds * CpuClock);
        long next = cpu.Cycles;
        while (next < total)
        {
            if (cpu.Halted) m.Setup(h.PlayAddr);
            next += frameCycles;
            while (cpu.Cycles < next && !cpu.Halted) cpu.Step();
            if (cpu.Halted && cpu.Cycles < next) cpu.Cycles = next;
        }

        var w = new VgmWriter { System = SystemNames[h.System] + " (SGC)", Notes = "SGC", Title = h.Title, Author = h.Artist };
        w.SetClock(VgmWriter.ClockSn76489, CpuClock);
        if (h.System == 2) { w.SetHeaderByte(0x28, 0x03); w.SetHeaderByte(0x29, 0x00); w.SetHeaderByte(0x2A, 15); }   // SN76489AN
        else { w.SetHeaderByte(0x28, 0x09); w.SetHeaderByte(0x29, 0x00); w.SetHeaderByte(0x2A, 16); }                 // 세가 VDP 안 PSG
        if (m.UsedFm) w.SetClock(VgmWriter.ClockYm2413, CpuClock);
        double totalSamples = seconds * VgmSong.Rate;
        long at = 0;
        foreach (var (time, cmd) in m.Log)
        {
            long ts = (long)Math.Min(totalSamples, Math.Max(0, time));
            if (ts > at) { w.Wait(ts - at); at = ts; }
            w.Write(cmd);
        }
        if ((long)totalSamples > at) w.Wait((long)totalSamples - at);
        var v = VgmFormat.Read(w.ToArray());
        v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
        if (!m.UsedSn && !m.UsedFm) v.UnsupportedChips.Add("(칩 쓰기 없음)");
        if (h.System == 2 && colecoBios == null) v.Notes = "콜레코비전 BIOS 없음 (resource\\coleco.rom) — BIOS를 부르는 곡은 소리가 안 날 수 있음";
        return v;
    }

    private static int U16(byte[] d, int o) { return d[o] | (d[o + 1] << 8); }
}
