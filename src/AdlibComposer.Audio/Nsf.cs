using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// NSF (NES 사운드 드라이버 덤프, NESM): 6502 코드 + 데이터. 드라이버를 2A03(<see cref="Cpu6502"/>)으로 돌려
/// APU 쓰기($4000~$4017)를 시간과 함께 모아 VGM(<see cref="VgmSong"/>, NES APU 0xB4)으로 바꾼다.
/// <para>머리말 (0x80바이트): 0x06 곡 수, 0x07 첫 곡(1부터), 0x08 읽어 둘 주소, 0x0A init, 0x0C play, 0x0E 제목 · 0x2E 작곡 · 0x4E 저작권 (32바이트씩),
/// 0x6E NTSC 재생 간격(µs), 0x70 뱅크 8개 (0이 아니면 4KB 뱅크 전환: $5FF8~$5FFF), 0x7A PAL/NTSC, 0x7B 확장 음원.</para>
/// <para>돌리는 방법 (nesdev 위키 "NSF"): RAM · APU를 지우고 $4015 = $0F, $4017 = $40, init을 A = 곡 번호(0부터) · X = 0(NTSC)으로 부르고,
/// 그 뒤 재생 간격마다 play를 부른다. DPCM 샘플은 시작할 때 CPU 메모리에서 VGM 데이터 블록(0xC2)으로 옮긴다.
/// 확장 음원(VRC6 · FDS · N163 …)은 소리 내지 않는다.</para>
/// <para>곡 길이 = 같은 이름의 .m3u (<see cref="NezM3u"/>, 종류 NSF, 번호는 1부터), 없으면 2분.</para>
/// </summary>
public static class Nsf
{
    public static readonly string[] Extensions = { ".nsf" };
    public const double DefaultSeconds = 120, DefaultFade = 4;

    public sealed class Header
    {
        public int Songs, StartSong, LoadAddr, InitAddr, PlayAddr, NtscSpeed, Expansion;
        public string Title = "", Artist = "", Copyright = "";
        public readonly byte[] Banks = new byte[8];
        public bool Banked, Pal;
        public byte[] Data;
    }

    public sealed class Track
    {
        /// <summary>곡 번호 (0부터, init에 넘기는 값).</summary>
        public int Song;
        public string Title = "";
        public double Seconds = DefaultSeconds, Fade = DefaultFade;
    }

    public static Header ReadHeader(byte[] d)
    {
        if (d.Length < 0x80 || Encoding.ASCII.GetString(d, 0, 5) != "NESM\x1A") throw new InvalidDataException("NSF 파일이 아닙니다 (머리말 NESM이 없음).");
        var h = new Header
        {
            Songs = Math.Max(1, (int)d[6]), StartSong = Math.Max(1, (int)d[7]),
            LoadAddr = U16(d, 8), InitAddr = U16(d, 0x0A), PlayAddr = U16(d, 0x0C),
            Title = Text(d, 0x0E), Artist = Text(d, 0x2E), Copyright = Text(d, 0x4E),
            NtscSpeed = U16(d, 0x6E), Pal = (d[0x7A] & 3) == 1, Expansion = d[0x7B],
        };
        Array.Copy(d, 0x70, h.Banks, 0, 8);
        h.Banked = h.Banks.Any(b => b != 0);
        h.Data = new byte[d.Length - 0x80];
        Array.Copy(d, 0x80, h.Data, 0, h.Data.Length);
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
        foreach (var e in NezM3u.Read(path, "NSF"))
        {
            var t = new Track { Song = Math.Max(0, e.Number - 1), Title = e.Title };
            if (e.Seconds > 0) t.Seconds = e.Seconds;
            if (e.Fade >= 0) t.Fade = e.Fade;
            list.Add(t);
        }
        if (list.Count > 0) return list;
        Header h;
        try { h = ReadHeader(File.ReadAllBytes(path)); }
        catch (Exception) { list.Add(new Track()); return list; }
        string name = h.Title.Length > 0 ? h.Title : Path.GetFileNameWithoutExtension(path);
        for (int s = 0; s < h.Songs; s++) list.Add(new Track { Song = s, Title = name + " #" + (s + 1) });
        return list;
    }

    /// <summary>"파일.nsf" 또는 "파일.nsf::n" (n = 곡 목록 안 번호, 0부터).</summary>
    public static VgmSong Load(string path)
    {
        path = NezM3u.SplitTrack(path, out int track);
        var tracks = Tracks(path);
        var t = tracks[Math.Max(0, Math.Min(track, tracks.Count - 1))];
        var bytes = File.ReadAllBytes(path);
        var h = ReadHeader(bytes);
        var v = Render(bytes, t.Song, t.Seconds, t.Fade);
        v.FilePath = path;
        v.TitleEn = t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(path) + " #" + (t.Song + 1);
        v.GameEn = h.Title;
        v.AuthorEn = h.Artist;
        v.SourceFormat = "NSF · 곡 " + (t.Song + 1) + " (" + (track + 1) + "/" + tracks.Count + ")" + (h.Expansion != 0 ? " · 확장 음원 없이" : "");
        return v;
    }

    // ------------------------------------------------------------ 돌리기

    private sealed class Machine : ICpu6502Bus
    {
        public readonly byte[] Ram = new byte[0x800];
        public readonly byte[] Sram = new byte[0x2000];
        private readonly byte[] rom;          // 뱅크 이미지 (4KB 단위) 또는 $8000~$FFFF 그대로
        private readonly int[] bankAt = new int[8];
        private readonly bool banked;
        public Cpu6502 Cpu;
        public double Clock;
        public readonly List<(double t, byte[] cmd)> Log = new List<(double, byte[])>();
        public bool UsedApu;
        private readonly byte[] apu = new byte[0x18];
        // DPCM: VGM에 옮긴 메모리 (같으면 다시 옮기지 않는다)
        private readonly byte[] shadow = new byte[0x10000];
        private readonly bool[] shadowValid = new bool[0x10000];

        public Machine(Header h)
        {
            banked = h.Banked;
            if (banked)
            {
                int pad = h.LoadAddr & 0x0FFF;
                int size = ((pad + h.Data.Length + 0xFFF) / 0x1000) * 0x1000;
                rom = new byte[Math.Max(size, 0x1000)];
                Array.Copy(h.Data, 0, rom, pad, h.Data.Length);
                for (int i = 0; i < 8; i++) SetBank(i, h.Banks[i]);
            }
            else
            {
                rom = new byte[0x8000];
                int at = h.LoadAddr - 0x8000;
                if (at < 0) at = 0;
                Array.Copy(h.Data, 0, rom, at, Math.Min(h.Data.Length, 0x8000 - at));
            }
        }

        private void SetBank(int slot, int bank)
        {
            int n = rom.Length / 0x1000;
            bankAt[slot] = (bank % Math.Max(1, n)) * 0x1000;
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x2000) return Ram[a & 0x7FF];
            if (a >= 0x8000) return banked ? rom[bankAt[(a - 0x8000) >> 12] + (a & 0xFFF)] : rom[a - 0x8000];
            if (a >= 0x6000) return Sram[a - 0x6000];
            if (a == 0x4015) return (byte)0;
            return 0;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0x2000) { Ram[a & 0x7FF] = v; return; }
            if (a >= 0x6000 && a < 0x8000) { Sram[a - 0x6000] = v; return; }
            if (a >= 0x5FF8 && a <= 0x5FFF) { if (banked) SetBank(a - 0x5FF8, v); return; }
            if (a >= 0x4000 && a <= 0x4017 && a != 0x4014 && a != 0x4016)
            {
                int r = a - 0x4000;
                apu[r] = v;
                if (r == 0x15 && (v & 0x10) != 0) CopyDpcm();
                Emit(0xB4, (byte)r, v);
                UsedApu = true;
            }
        }

        /// <summary>DPCM을 켤 때: 읽을 메모리를 VGM 데이터 블록으로 (앞서 옮긴 것과 다를 때만).</summary>
        private void CopyDpcm()
        {
            int start = 0xC000 | (apu[0x12] << 6);
            int len = (apu[0x13] << 4) | 1;
            bool same = true;
            var buf = new byte[len];
            for (int i = 0; i < len; i++)
            {
                int ad = start + i;
                if (ad > 0xFFFF) ad = 0x8000 + (ad - 0x10000);
                buf[i] = Read(ad);
                if (!shadowValid[ad] || shadow[ad] != buf[i]) same = false;
            }
            if (same) return;
            for (int i = 0; i < len; i++)
            {
                int ad = start + i;
                if (ad > 0xFFFF) ad = 0x8000 + (ad - 0x10000);
                shadow[ad] = buf[i]; shadowValid[ad] = true;
            }
            int size = len + 2;
            var cmd = new byte[7 + size];
            cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0xC2;
            cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = 0;
            cmd[7] = (byte)start; cmd[8] = (byte)(start >> 8);
            Array.Copy(buf, 0, cmd, 9, len);
            Log.Add((Cpu.Cycles * VgmSong.Rate / Clock, cmd));
        }

        private void Emit(params byte[] cmd) { Log.Add((Cpu.Cycles * VgmSong.Rate / Clock, cmd)); }
    }

    /// <summary>돌아올 곳 (함정): 여기로 RTS하면 호출이 끝난 것.</summary>
    private const int Trap = 0x4100;

    /// <summary>곡 하나(song = 0부터)를 seconds초 돌려 VGM으로.</summary>
    public static VgmSong Render(byte[] file, int song, double seconds, double fade = DefaultFade)
    {
        var h = ReadHeader(file);
        var sys = SystemDefinitions.Get("nes");
        var m = new Machine(h);
        var cpu = new Cpu6502(m, Cpu6502.Variant.Nes2A03);
        m.Cpu = cpu;
        m.Clock = sys != null && sys.CpuClock > 0 ? sys.CpuClock : 1789773;
        double apuClock = sys?.Chip("2A03")?.Clock ?? 0;
        if (apuClock <= 0) apuClock = m.Clock;

        // APU 처음 상태
        for (int r = 0x4000; r <= 0x4013; r++) m.Write(r, 0);
        m.Write(0x4015, 0); m.Write(0x4015, 0x0F); m.Write(0x4017, 0x40);
        double hz = h.NtscSpeed > 0 ? 1e6 / h.NtscSpeed : sys != null && sys.FrameRate > 0 ? sys.FrameRate : 60.0988;
        long framePeriod = (long)(m.Clock / hz);

        Call(cpu, m, h.InitAddr, song, 0, (long)m.Clock * 2);
        double total = seconds * VgmSong.Rate;
        long totalCycles = (long)(seconds * m.Clock);
        long next = cpu.Cycles;
        while (next < totalCycles)
        {
            cpu.Cycles = Math.Max(cpu.Cycles, next);
            Call(cpu, m, h.PlayAddr, -1, -1, framePeriod);
            next += framePeriod;
        }

        var w = new VgmWriter { System = "NES (NSF)", Notes = "NSF", Title = h.Title, Author = h.Artist };
        w.SetClock(VgmWriter.ClockNesApu, apuClock);
        long at = 0;
        foreach (var (time, cmd) in m.Log)
        {
            long ts = (long)Math.Min(total, Math.Max(0, time));
            if (ts > at) { w.Wait(ts - at); at = ts; }
            w.Write(cmd);
        }
        if ((long)total > at) w.Wait((long)total - at);
        var v = VgmFormat.Read(w.ToArray());
        v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
        if (!m.UsedApu) v.UnsupportedChips.Add("(칩 쓰기 없음)");
        if (h.Expansion != 0) v.UnsupportedChips.Add(ExpansionName(h.Expansion));
        if (cpu.UnknownOps > 0 || cpu.Jammed) v.UnsupportedChips.Add("(6502 알 수 없는 명령 " + cpu.UnknownOps + (cpu.Jammed ? ", 멈춤" : "") + ")");
        return v;
    }

    public static string ExpansionName(int bits)
    {
        var l = new List<string>();
        if ((bits & 1) != 0) l.Add("VRC6");
        if ((bits & 2) != 0) l.Add("VRC7");
        if ((bits & 4) != 0) l.Add("FDS");
        if ((bits & 8) != 0) l.Add("MMC5");
        if ((bits & 0x10) != 0) l.Add("N163");
        if ((bits & 0x20) != 0) l.Add("Sunsoft 5B");
        return "NSF 확장 음원 " + string.Join(" · ", l);
    }

    /// <summary>addr를 부르고 함정으로 돌아오거나 한도까지 돌린다. a ≥ 0이면 A = a, x ≥ 0이면 X = x.</summary>
    private static void Call(Cpu6502 cpu, Machine m, int addr, int a, int x, long maxCycles)
    {
        if (a >= 0) cpu.A = a & 0xFF;
        if (x >= 0) cpu.X = x & 0xFF;
        cpu.S = 0xFD;
        cpu.Jammed = false;
        int ret = Trap - 1;
        cpu.Push(ret >> 8); cpu.Push(ret & 0xFF);
        cpu.PC = addr;
        long end = cpu.Cycles + maxCycles;
        while (cpu.Cycles < end && cpu.PC != Trap && !cpu.Jammed) cpu.Step();
    }

    private static int U16(byte[] d, int o) { return d[o] | (d[o + 1] << 8); }
}
