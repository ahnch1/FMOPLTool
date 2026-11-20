using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot Z80 + YM2610 작은 보드 (같은 사운드 드라이버 계열): 비디오시스템 (driver videosystem, type turbofrc: Turbo Force · Spinal Breakers · Power Spikes · Karate Blazers,
/// type aerofgt: Aero Fighters · Goal! '92), 사이쿄 (driver psikyo, type gunbird: Gunbird · Battle K-Road · Strikers 1945 (JN)).
/// <para>기계 (MAME aerofgt · psikyo sound_map / portmap + 디스어셈블): Z80 5MHz (사이쿄 4MHz), YM2610 8MHz, 명령 = 래치 읽기 + NMI, IRQ = YM2610 타이머.
/// turbofrc: ROM 0000h~77FFh, RAM 7800h~7FFFh, 뱅크 8000h~FFFFh (포트 00h 값 &amp; 3 × 32KB, 영역 10000h부터), YM2610 포트 18h~1Bh, 래치 포트 14h.
/// aerofgt: YM2610 포트 00h~03h, 뱅크 포트 04h, 래치 0Ch (나머지 같음).
/// gunbird: ROM 0000h~7FFFh, RAM 8000h~81FFh, 뱅크 8200h~FFFFh (포트 00h (값 &gt;&gt; 4) &amp; 3 × 32KB, 창은 뱅크의 200h부터), YM2610 포트 04h~07h, 래치 08h.</para>
/// <para>곡: rom code = Z80 ROM, adpcma · adpcmb = YM2610 ADPCM ROM. 부팅 1초 뒤 명령, 180초를 YM2610 VGM으로.</para>
/// </summary>
public static class HootYm2610Board
{
    public const double YmClock = 8000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public sealed class Layout
    {
        public string Name;
        public double CpuClock;
        public int Ym, BankPort, BankShift, Latch, RamStart, RamEnd, WindowStart, WindowOffset;
        /// <summary>뱅크 0이 시작하는 영역 자리 (turbofrc = 10000h — MAME configure_entries(…, &amp;RAM[0x10000], 0x8000)).</summary>
        public int BankBase;
    }

    public static readonly Layout Turbofrc = new Layout { Name = "Video System (Turbo Force)", CpuClock = 5000000, Ym = 0x18, BankPort = 0x00, BankShift = 0, Latch = 0x14, RamStart = 0x7800, RamEnd = 0x8000, WindowStart = 0x8000, WindowOffset = 0, BankBase = 0x10000 };
    public static readonly Layout Aerofgt = new Layout { Name = "Video System (Aero Fighters)", CpuClock = 5000000, Ym = 0x00, BankPort = 0x04, BankShift = 0, Latch = 0x0C, RamStart = 0x7800, RamEnd = 0x8000, WindowStart = 0x8000, WindowOffset = 0 };
    // MAME welltris (Welltris · Quiz 18-Kin): YM 08h, 뱅크 00h (ROM 처음부터), 명령 10h
    public static readonly Layout Welltris = new Layout { Name = "Video System (Welltris)", CpuClock = 5000000, Ym = 0x08, BankPort = 0x00, BankShift = 0, Latch = 0x10, RamStart = 0x7800, RamEnd = 0x8000, WindowStart = 0x8000, WindowOffset = 0 };
    // MAME fromanc2 (Taisen Idol-Mahjong Final Romance 2 · R): Z80 8MHz, ROM 0000h~DFFFh, RAM E000h~FFFFh, 명령 00h, YM 08h, ADPCM ROM 하나를 A · B가 같이
    public static readonly Layout Fromanc2 = new Layout { Name = "Video System (Final Romance 2)", CpuClock = 8000000, Ym = 0x08, BankPort = 0x100, BankShift = 0, Latch = 0x00, RamStart = 0xE000, RamEnd = 0x10000, WindowStart = 0x10000, WindowOffset = 0 };
    public static readonly Layout Gunbird = new Layout { Name = "Psikyo (Gunbird)", CpuClock = 4000000, Ym = 0x04, BankPort = 0x00, BankShift = 4, Latch = 0x08, RamStart = 0x8000, RamEnd = 0x8200, WindowStart = 0x8200, WindowOffset = 0x200 };

    public static Layout LayoutOf(string driver, string type)
    {
        string d = (driver ?? "").Trim().ToLowerInvariant(), t = (type ?? "").Trim().ToLowerInvariant();
        if (d == "videosystem") return t == "turbofrc" ? Turbofrc : t == "aerofgt" ? Aerofgt : t == "welltris" ? Welltris : t == "fromanc2" ? Fromanc2 : null;
        if (d == "psikyo") return t == "gunbird" ? Gunbird : null;
        return null;
    }

    public static bool Handles(string driver, string type) { return LayoutOf(driver, type) != null; }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        var layout = LayoutOf(e.Driver, e.Type) ?? throw new InvalidDataException("YM2610 보드가 아닙니다: " + e.Type);
        var code = new List<(long off, byte[] d)>();
        var a = new List<(long off, byte[] d)>();
        var b = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            var list = type == "code" ? code : type == "adpcma" || type == "adpcm" ? a : type == "adpcmb" ? b : null;
            if (type == "adpcm" && layout == Fromanc2) { var dd = file(text); if (dd == null) missing.Add(text); else if (off >= 0) { a.Add((off, dd)); b.Add((off, dd)); } continue; }   // A · B 같은 ROM
            if (list == null) { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            list.Add((off, d));
        }
        if (code.Count == 0) missing.Add("(code)");
        byte[] Join(List<(long off, byte[] d)> l)
        {
            if (l.Count == 0) return null;
            var r = new byte[(int)l.Max(x => x.off + x.d.Length)];
            foreach (var (off, d) in l) Array.Copy(d, 0, r, off, d.Length);
            return r;
        }
        var rom = Join(code) ?? new byte[0x10000];
        if (rom.Length < 0x10000) Array.Resize(ref rom, 0x10000);
        var m = new Machine(layout, rom, Join(a), Join(b));
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly Layout L;
        public readonly byte[] Rom, AdpcmA, AdpcmB;
        public readonly byte[] Ram = new byte[0x2000];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>YM2610 쓰기 (port 0 · 1).</summary>
        public readonly List<(double t, int port, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherPorts = new Dictionary<int, int>();

        private int latch, bank;
        private readonly int[] addr = new int[2];
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x200).ToArray();

        public Machine(Layout layout, byte[] rom, byte[] a, byte[] b)
        {
            L = layout; Rom = rom; AdpcmA = a; AdpcmB = b;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.port, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            latch = code & 0xFF;
            Nmis++;
            Cpu.Halted = false;
            Cpu.Iff2 = Cpu.Iff1;
            Cpu.Iff1 = false;
            Push(Cpu.PC);
            Cpu.PC = 0x66;
            Cpu.Cycles += 11;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        private long TimerAPeriod { get { return Math.Max(1, (long)(144.0 * (1024 - timerA) / YmClock * L.CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(2304.0 * (256 - timerB) / YmClock * L.CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(L.CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (timerFlags != 0 && Cpu.Iff1)
                {
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted) { Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, Math.Min(nextTimerA, nextTimerB))); continue; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a >= L.RamStart && a < L.RamEnd) return Ram[a - L.RamStart];
            if (a >= L.WindowStart) { int o = L.BankBase + bank * 0x8000 + L.WindowOffset + (a - L.WindowStart); return o < Rom.Length ? Rom[o] : (byte)0xFF; }
            return Rom[a];
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= L.RamStart && a < L.RamEnd) Ram[a - L.RamStart] = v;
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if (port == L.Ym || port == L.Ym + 2) return (byte)timerFlags;   // 상태: 바쁘지 않음
            if (port == L.Ym + 1 || port == L.Ym + 3) return 0;
            if (port == L.Latch) { LatchReads++; return (byte)latch; }
            OtherPorts.TryGetValue(port, out int c); OtherPorts[port] = c + 1;
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            if (port >= L.Ym && port < L.Ym + 4)
            {
                int p = (port - L.Ym) >> 1;
                if (((port - L.Ym) & 1) == 0) addr[p] = v; else YmWrite(p, addr[p], v);
                return;
            }
            if (port == L.BankPort) { bank = (v >> L.BankShift) & 3; return; }
            if (port == L.Latch || port == 0x08 || port == 0x0C || port == 0x14) return;   // 받음 확인 (pending clear)
            OtherPorts.TryGetValue(0x100 | port, out int c); OtherPorts[0x100 | port] = c + 1;
        }

        private void YmWrite(int port, int reg, int v)
        {
            if (port == 0)
            {
                switch (reg)
                {
                    case 0x24: timerA = (timerA & 3) | (v << 2); return;
                    case 0x25: timerA = (timerA & 0x3FC) | (v & 3); return;
                    case 0x26: timerB = v; return;
                    case 0x27:
                        {
                            int old = timerCtl;
                            timerCtl = v;
                            if ((v & 0x10) != 0) timerFlags &= ~1;
                            if ((v & 0x20) != 0) timerFlags &= ~2;
                            if ((v & 1) != 0 && (old & 1) == 0) nextTimerA = Cpu.Cycles + TimerAPeriod; else if ((v & 1) == 0) nextTimerA = long.MaxValue;
                            if ((v & 2) != 0 && (old & 2) == 0) nextTimerB = Cpu.Cycles + TimerBPeriod; else if ((v & 2) == 0) nextTimerB = long.MaxValue;
                            Emit(0, reg, v & 0xC0);
                            return;
                        }
                }
            }
            Emit(port, reg, v);
        }

        private void Emit(int port, int reg, int val)
        {
            // 같은 값 다시 쓰기는 버린다 — 키온 28h · SSG 엔벨로프 0Dh · 주파수 A0h~AEh · ADPCM 키 (port 0 10h · port 1 00h) 는 늘
            bool keep = (port == 0 && (reg == 0x28 || reg == 0x0D || reg == 0x10 || reg == 0x1C || (reg >= 0xA0 && reg <= 0xAE)))
                || (port == 1 && (reg == 0x00 || (reg >= 0xA0 && reg <= 0xAE)));
            int k = port << 8 | reg;
            if (!keep && last[k] == val) return;
            last[k] = val;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / L.CpuClock), port, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = L.Name + " (hoot)", Notes = "hoot " + L.Name };
            w.SetClock(VgmWriter.ClockYm2610, YmClock);
            void Block(int type, byte[] data)
            {
                if (data == null || data.Length == 0) return;
                var cmd = new byte[7 + 8 + data.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = (byte)type;
                int size = 8 + data.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int total = data.Length;
                cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                Array.Copy(data, 0, cmd, 15, data.Length);
                w.Write(cmd);
            }
            Block(0x82, AdpcmA);
            Block(0x83, AdpcmB ?? AdpcmA);
            double total2 = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write((byte)(0x58 + x.port), x.reg, x.val);
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
