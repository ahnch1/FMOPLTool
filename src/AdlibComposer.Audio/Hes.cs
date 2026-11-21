using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// HES (PC엔진 사운드 드라이버 덤프, HESM): HuC6280 코드 + 데이터. 드라이버를 HuC6280(<see cref="Cpu6502"/>)으로 돌려
/// PSG 쓰기($0800~$0809)를 시간과 함께 모아 VGM(<see cref="VgmSong"/>, HuC6280 0xB9)으로 바꾼다.
/// <para>머리말: "HESM", 0x04 판, 0x05 첫 곡, 0x06 init, 0x08 MPR 8개, 0x10부터 DATA 덩어리 ("DATA", 크기, 실제 주소, 0 — 16바이트 뒤 데이터).</para>
/// <para>돌리는 방법 (Game_Music_Emu Hes_Emu와 같은 관례): RAM(페이지 F8)을 지우고 MPR을 머리말 값으로, A = 곡 번호로 init을 부른다.
/// init이 돌아오는 곳($1FFF)에서 CPU는 쉬고, 타이머(IRQ $FFFA) · VDC 수직 귀선(IRQ1 $FFF8, 1/60초)이 드라이버를 깨운다.</para>
/// <para>곡 목록 = 같은 이름의 .m3u (<see cref="NezM3u"/>, 종류 HES, 번호 0부터). 없으면 곡 0~255를 잠깐씩 돌려 소리 나는 것만.
/// 곡 길이 = m3u, 없으면 2분.</para>
/// </summary>
public static class Hes
{
    public static readonly string[] Extensions = { ".hes" };
    public const double DefaultSeconds = 120, DefaultFade = 4;
    public const double CpuClock = 7159090;
    private const int Idle = 0x1FFF;

    public sealed class Header
    {
        public int Version, FirstSong, InitAddr;
        public readonly byte[] Banks = new byte[8];
        public byte[] Rom;
    }

    public sealed class Track
    {
        public int Song;
        public string Title = "";
        public double Seconds = DefaultSeconds, Fade = DefaultFade;
    }

    public static Header ReadHeader(byte[] d)
    {
        if (d.Length < 0x20 || Encoding.ASCII.GetString(d, 0, 4) != "HESM") throw new InvalidDataException("HES 파일이 아닙니다 (머리말 HESM이 없음).");
        var h = new Header { Version = d[4], FirstSong = d[5], InitAddr = d[6] | (d[7] << 8) };
        Array.Copy(d, 8, h.Banks, 0, 8);
        // DATA 덩어리 (16바이트 머리: "DATA", 크기, 실제 주소, 쓰지 않음) — 데이터를 그 실제 주소에 놓는다
        var chunks = new List<(int addr, int at, int len)>();
        int p = 0x10, end = 0;
        while (p + 0x10 <= d.Length && Encoding.ASCII.GetString(d, p, 4) == "DATA")
        {
            int size = (int)Math.Min(VgmFormat.U32(d, p + 4), (uint)(d.Length - p - 0x10));
            int addr = (int)(VgmFormat.U32(d, p + 8) & 0x1FFFFF);
            chunks.Add((addr, p + 0x10, size));
            end = Math.Max(end, addr + size);
            p += 0x10 + size;
        }
        if (chunks.Count == 0) { chunks.Add((0, 0x20, d.Length - 0x20)); end = d.Length - 0x20; }
        h.Rom = new byte[Math.Min(end, 0x200000)];
        foreach (var (addr, at, len) in chunks) Array.Copy(d, at, h.Rom, addr, Math.Min(len, h.Rom.Length - addr));
        return h;
    }

    // ------------------------------------------------------------ 곡 목록 · 열기

    private static readonly Dictionary<string, List<int>> probed = new Dictionary<string, List<int>>();

    public static List<Track> Tracks(string path)
    {
        var list = new List<Track>();
        foreach (var e in NezM3u.Read(path, "HES"))
        {
            var t = new Track { Song = e.Number, Title = e.Title };
            if (e.Seconds > 0) t.Seconds = e.Seconds;
            if (e.Fade >= 0) t.Fade = e.Fade;
            list.Add(t);
        }
        if (list.Count > 0) return list;
        string name = Path.GetFileNameWithoutExtension(path);
        foreach (int s in SoundingSongs(path)) list.Add(new Track { Song = s, Title = name + " #" + s });
        if (list.Count == 0) list.Add(new Track { Title = name });
        return list;
    }

    private static string CacheKey(string path)
    {
        var fi = new FileInfo(path);
        return fi.FullName + "|" + (fi.Exists ? fi.Length + "|" + fi.LastWriteTimeUtc.Ticks : "");
    }

    /// <summary>곡 목록을 이미 찾아 두었거나 m3u가 있어 바로 알 수 있는지 (파일 목록이 제목을 물을 때 돌리지 않게).</summary>
    public static bool TracksKnown(string path)
    {
        if (File.Exists(Path.ChangeExtension(path, ".m3u"))) return true;
        lock (probed) return probed.ContainsKey(CacheKey(path));
    }

    /// <summary>
    /// 곡 0~255를 2초씩 돌려 소리를 내는 곡만 (파일마다 한 번 찾고 기억). 드라이버가 곡 번호를 접어 같은 곡이 되풀이되는 일이 많아,
    /// 앞 2초의 PSG 쓰기가 앞서 나온 곡과 똑같으면 뺀다. 곡마다 따로 돌리므로 나눠서 함께 돌린다.
    /// </summary>
    public static List<int> SoundingSongs(string path)
    {
        string key = CacheKey(path);
        lock (probed)
            if (probed.TryGetValue(key, out var hit)) return hit;
        var l = new List<int>();
        try
        {
            var bytes = File.ReadAllBytes(path);
            var sig = new ulong?[256];
            System.Threading.Tasks.Parallel.For(0, 256, s => { sig[s] = Signature(bytes, s, 2.0); });
            var seen = new HashSet<ulong>();
            for (int s = 0; s < 256; s++)
                if (sig[s].HasValue && seen.Add(sig[s].Value)) l.Add(s);
        }
        catch (Exception) { }
        lock (probed) probed[key] = l;
        return l;
    }

    /// <summary>"파일.hes" 또는 "파일.hes::n" (n = 곡 목록 안 번호, 0부터).</summary>
    public static VgmSong Load(string path)
    {
        path = NezM3u.SplitTrack(path, out int track);
        var tracks = Tracks(path);
        var t = tracks[Math.Max(0, Math.Min(track, tracks.Count - 1))];
        var v = Render(File.ReadAllBytes(path), t.Song, t.Seconds, t.Fade);
        v.FilePath = path;
        v.TitleEn = t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(path) + " #" + t.Song;
        v.SourceFormat = "HES · 곡 " + t.Song + " (" + (track + 1) + "/" + tracks.Count + ")";
        return v;
    }

    // ------------------------------------------------------------ 기계

    private sealed class Machine : ICpu6502Bus
    {
        private readonly byte[] rom;
        public readonly byte[] Ram = new byte[0x2000];
        private readonly Dictionary<int, byte[]> extraRam = new Dictionary<int, byte[]>();
        public Cpu6502 Cpu;
        public double Clock = CpuClock;
        public readonly List<(double t, byte[] cmd)> Log = new List<(double, byte[])>();
        public bool Sounded, UsedPsg;
        public long Steps, IdleSkips, Irqs;
        /// <summary>인터럽트를 막은 채 1초 넘게 쉬는 곳으로 돌아오지 않음 (없는 곡 번호로 드라이버가 길을 잃은 것).</summary>
        public bool Crashed;
        private int psgSelect;

        // 인터럽트
        public int Disables;
        public bool TimerOn, TimerPending, VdcPending;
        public int TimerLoad = 1;
        public long NextTimer = long.MaxValue;
        public int VdcLatch, VdcControl;
        /// <summary>VDC 레지스터 5를 쓴 적이 있는가 (안 쓴 곡은 hoot hes.cpp처럼 VBlank IRQ1을 늘 낸다).</summary>
        public bool VdcWritten;

        public Machine(byte[] rom) { this.rom = rom; }

        public byte Read(int a)
        {
            int page = a >> 13, off = a & 0x1FFF;
            if (page == 0xFF) return ReadIo(off);
            if (page >= 0xF8 && page <= 0xFB) return Ram[off];
            int at = (page << 13) | off;
            if (at < rom.Length) return rom[at];
            return extraRam.TryGetValue(page, out var b) ? b[off] : (byte)0xFF;
        }

        public void Write(int a, byte v)
        {
            int page = a >> 13, off = a & 0x1FFF;
            if (page == 0xFF) { WriteIo(off, v); return; }
            if (page >= 0xF8 && page <= 0xFB) { Ram[off] = v; return; }
            if ((page << 13) + off < rom.Length) return;   // ROM
            if (!extraRam.TryGetValue(page, out var b)) extraRam[page] = b = new byte[0x2000];
            b[off] = v;
        }

        private byte ReadIo(int o)
        {
            if (o < 0x400)
            {
                if ((o & 3) == 0) { byte st = (byte)(VdcPending ? 0x20 : 0); VdcPending = false; return st; }
                return 0;
            }
            if (o >= 0xC00 && o < 0x1000)
            {
                if (!TimerOn) return (byte)((TimerLoad - 1) & 0x7F);
                long left = Math.Max(0, NextTimer - Cpu.Cycles);
                return (byte)((left / 1024) & 0x7F);
            }
            if (o >= 0x1000 && o < 0x1400) return 0xFF;   // 조이패드
            if (o >= 0x1400 && o < 0x1800)
            {
                if ((o & 3) == 2) return (byte)Disables;
                if ((o & 3) == 3) return (byte)((TimerPending ? 4 : 0) | (VdcPending ? 2 : 0));
            }
            return 0xFF;
        }

        private void WriteIo(int o, byte v)
        {
            if (o < 0x400)
            {
                if ((o & 3) == 0) VdcLatch = v & 0x1F;
                else if ((o & 3) == 2 && VdcLatch == 5) { VdcControl = v; VdcWritten = true; }
                return;
            }
            if (o >= 0x800 && o < 0xC00)
            {
                int r = o & 0x0F;
                if (r > 9) return;
                if (r == 0) psgSelect = v & 7;
                if (r == 4 && (v & 0x80) != 0 && (v & 0x1F) != 0) Sounded = true;
                UsedPsg = true;
                Log.Add((Cpu.Cycles * VgmSong.Rate / Clock, new byte[] { 0xB9, (byte)r, v }));
                return;
            }
            if (o >= 0xC00 && o < 0x1000)
            {
                if ((o & 1) == 0) TimerLoad = (v & 0x7F) + 1;
                else
                {
                    bool on = (v & 1) != 0;
                    if (on && !TimerOn) NextTimer = Cpu.Cycles + TimerLoad * 1024L;
                    if (!on) NextTimer = long.MaxValue;
                    TimerOn = on;
                }
                return;
            }
            if (o >= 0x1400 && o < 0x1800)
            {
                if ((o & 3) == 2) Disables = v & 7;
                else if ((o & 3) == 3) TimerPending = false;
            }
        }
    }

    // ------------------------------------------------------------ 돌리기

    private static Machine Run(byte[] file, int song, double seconds)
    {
        var h = ReadHeader(file);
        var sys = SystemDefinitions.Get("pce");
        var m = new Machine(h.Rom);
        if (sys != null && sys.CpuClock > 0) m.Clock = sys.CpuClock;
        var cpu = new Cpu6502(m, Cpu6502.Variant.HuC6280);
        m.Cpu = cpu;
        for (int i = 0; i < 8; i++) cpu.Mpr[i] = h.Banks[i];
        double fps = sys != null && sys.FrameRate > 0 ? sys.FrameRate : 59.94;
        long frame = (long)(m.Clock / fps);

        // 처음: 타이머 · VDC 인터럽트는 막혀 있음, init이 RTS하면 $1FFF(쉼)로
        m.Disables = 6;
        m.Ram[0x1FF] = (byte)((Idle - 1) >> 8);
        m.Ram[0x1FE] = (byte)((Idle - 1) & 0xFF);
        cpu.S = 0xFD;
        cpu.PC = h.InitAddr;
        cpu.A = song & 0xFF;
        cpu.I = true;

        long end = (long)(seconds * m.Clock);
        long nextVbl = frame;
        long lastIdle = 0, busyLimit = (long)m.Clock;
        int guard = 0;
        while (cpu.Cycles < end)
        {
            // 일어난 일
            if (cpu.Cycles >= nextVbl) { m.VdcPending = true; nextVbl += frame; }
            if (m.TimerOn && cpu.Cycles >= m.NextTimer) { m.TimerPending = true; m.NextTimer += m.TimerLoad * 1024L; }
            // 인터럽트
            if (!cpu.I)
            {
                if (m.TimerPending && (m.Disables & 4) == 0) { m.TimerPending = false; m.Irqs++; cpu.Interrupt(0xFFFA); continue; }
                if (m.VdcPending && ((m.VdcControl & 0x08) != 0 || !m.VdcWritten) && (m.Disables & 2) == 0) { m.Irqs++; cpu.Interrupt(0xFFF8); continue; }
            }
            if ((cpu.PC & 0xFFFF) == Idle)
            {
                // 다음 일까지 쉰다
                long next = Math.Min(nextVbl, m.TimerOn ? m.NextTimer : long.MaxValue);
                if (next <= cpu.Cycles) next = cpu.Cycles + 1;
                cpu.Cycles = Math.Min(next, end);
                lastIdle = cpu.Cycles;
                m.IdleSkips++;
                // 인터럽트가 막혀 있는데 쉬는 중이면 더 할 일이 없다 (그래도 시간은 흐른다)
                continue;
            }
            cpu.Step();
            m.Steps++;
            if (cpu.I && cpu.Cycles - lastIdle > busyLimit) { m.Crashed = true; break; }
            if (cpu.Jammed && ++guard > 4) break;
        }
        return m;
    }

    /// <summary>진단: secs초 돌린 뒤 기계 상태 (인터럽트 · 타이머 · PSG 쓰기 · 명령 수).</summary>
    public static string Describe(byte[] file, int song, double secs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var m = Run(file, song, secs);
        var c = m.Cpu;
        return "PC " + c.PC.ToString("X4") + " I " + c.I + " MPR " + string.Join(" ", c.Mpr.Select(b => b.ToString("X2"))) +
            " | disables " + m.Disables + " timer " + (m.TimerOn ? "on load " + m.TimerLoad : "off") + " vdcCtl " + m.VdcControl.ToString("X2") +
            " | psg writes " + m.Log.Count + " sounded " + m.Sounded + " | unknown " + c.UnknownOps + (c.Jammed ? " jammed" : "") +
            (m.Crashed ? " CRASHED" : "") + " | steps " + m.Steps + " idle " + m.IdleSkips + " irq " + m.Irqs + " | " + sw.ElapsedMilliseconds + " ms";
    }

    /// <summary>곡이 secs초 안에 소리를 내는지 (곡 목록 찾기).</summary>
    public static bool Probe(byte[] file, int song, double secs) { return Run(file, song, secs).Sounded; }

    /// <summary>secs초 동안의 PSG 쓰기(레지스터 · 값 · 10ms 단위 시간) 해시. 소리를 내지 않으면 null.</summary>
    public static ulong? Signature(byte[] file, int song, double secs)
    {
        var m = Run(file, song, secs);
        if (!m.Sounded || m.Crashed) return null;
        ulong h = 14695981039346656037UL;
        foreach (var (t, cmd) in m.Log)
        {
            h = (h ^ cmd[1]) * 1099511628211UL;
            h = (h ^ cmd[2]) * 1099511628211UL;
            h = (h ^ (ulong)(t / 441)) * 1099511628211UL;
        }
        return h;
    }

    /// <summary>곡 하나를 seconds초 돌려 VGM으로.</summary>
    public static VgmSong Render(byte[] file, int song, double seconds, double fade = DefaultFade)
    {
        var m = Run(file, song, seconds);
        var sys = SystemDefinitions.Get("pce");
        double psgClock = sys?.Chip("HuC6280")?.Clock ?? 0;
        if (psgClock <= 0) psgClock = 3579545;
        var w = new VgmWriter { System = "PC Engine (HES)", Notes = "HES" };
        w.SetClock(VgmWriter.ClockHuc6280, psgClock);
        double total = seconds * VgmSong.Rate;
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
        if (!m.UsedPsg) v.UnsupportedChips.Add("(칩 쓰기 없음)");
        if (m.Cpu.UnknownOps > 0 || m.Cpu.Jammed) v.UnsupportedChips.Add("(HuC6280 알 수 없는 명령 " + m.Cpu.UnknownOps + (m.Cpu.Jammed ? ", 멈춤" : "") + ")");
        return v;
    }
}
