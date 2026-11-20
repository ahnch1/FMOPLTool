using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 데이터 이스트 6809 사운드 보드 (driver dataeast, type brkthru: Break Thru · Darwin 4078, type exprraid: Express Raider): 사운드 보드를 흉내 내고 6809 사운드 ROM을 돌린다.
/// <para>기계 (MAME brkthru · exprraid sound_map, ROM 스캔으로 확인): 6809 1.5MHz, RAM 0000h~1FFFh, ROM 8000h~FFFFh, 명령 = 래치 (메인이 쓰면 NMI), YM3526 타이머 = IRQ.
/// brkthru: YM3526 2000h (3MHz), 래치 4000h, YM2203 6000h (1.5MHz). exprraid: YM2203 2000h, YM3526 4000h (3.58MHz), 래치 6000h.</para>
/// <para>곡: rom code = 6809 ROM (offset = 주소), 부팅 1초 뒤 곡 코드를 래치에 (NMI), 180초를 YM2203 + YM3526 VGM으로.</para>
/// </summary>
public static class HootDeco6809
{
    public const double CpuClock = 1500000, OpnClock = 1500000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public sealed class Layout
    {
        public string Name;
        public int Latch, Opn, Opl;
        public double OplClock;
    }

    public static readonly Layout Brkthru = new Layout { Name = "Data East Break Thru", Opl = 0x2000, Latch = 0x4000, Opn = 0x6000, OplClock = 3000000 };
    public static readonly Layout Exprraid = new Layout { Name = "Data East Express Raider", Opn = 0x2000, Opl = 0x4000, Latch = 0x6000, OplClock = 3579545 };

    public static Layout LayoutOf(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "dataeast", StringComparison.OrdinalIgnoreCase)) return null;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "brkthru" ? Brkthru : t == "exprraid" ? Exprraid : null;
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
        var layout = LayoutOf(e.Driver, e.Type) ?? throw new InvalidDataException("데이터 이스트 6809 보드가 아닙니다: " + e.Type);
        var mem = new byte[0x10000];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0x8000 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            Array.Copy(d, 0, mem, off, (int)Math.Min(d.Length, 0x10000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var m = new Machine(layout, mem);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6809Bus
    {
        public readonly Layout L;
        public readonly byte[] Mem;
        public readonly Cpu6809 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2203, 1 = YM3526.</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch, opnAddr, oplAddr;
        private long playStart = -1;
        private int timer1, timer2, timerCtl, timerFlags;
        private long nextT1 = long.MaxValue, nextT2 = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x200).ToArray();

        public Machine(Layout layout, byte[] mem)
        {
            L = layout; Mem = mem;
            Cpu = new Cpu6809(this);
            Cpu.Reset();
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        public void Send(int code)
        {
            latch = code;
            Nmis++;
            Cpu.Nmi();
        }

        // OPL 타이머: 1 = 72 × 4 클럭 × (256 − T1), 2 = 그 4배
        private long T1Period { get { return Math.Max(1, (long)(288.0 * (256 - timer1) / L.OplClock * CpuClock)); } }
        private long T2Period { get { return Math.Max(1, (long)(1152.0 * (256 - timer2) / L.OplClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextT1) { nextT1 += T1Period; if ((timerCtl & 0x40) == 0) timerFlags |= 0x40; }
                if (now >= nextT2) { nextT2 += T2Period; if ((timerCtl & 0x20) == 0) timerFlags |= 0x20; }
                if (timerFlags != 0 && Cpu.Irq()) Irqs++;   // IRQ 줄 = YM3526 (레벨)
                if (Cpu.Waiting)
                {
                    long next = Math.Min(end, Math.Min(nextT1, nextT2));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a >= 0x8000 || a < 0x2000) return Mem[a];
            if (a == L.Latch) { LatchReads++; return (byte)latch; }
            if (a == L.Opl || a == L.Opl + 1) return (byte)(timerFlags != 0 ? 0x80 | timerFlags : 0);
            if (a == L.Opn || a == L.Opn + 1) return 0;   // YM2203 상태: 바쁘지 않음
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0x2000) { Mem[a] = v; return; }
            if (a == L.Opn) { opnAddr = v; return; }
            if (a == L.Opn + 1) { Emit(0, opnAddr, v); return; }
            if (a == L.Opl) { oplAddr = v; return; }
            if (a == L.Opl + 1) { OplWrite(oplAddr, v); return; }
            if (a >= 0x8000) return;
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        private void OplWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x02: timer1 = v; return;
                case 0x03: timer2 = v; return;
                case 0x04:
                    if ((v & 0x80) != 0) { timerFlags = 0; return; }   // IRQ 리셋
                    {
                        int old = timerCtl;
                        timerCtl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) nextT1 = Cpu.Cycles + T1Period; else if ((v & 1) == 0) nextT1 = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextT2 = Cpu.Cycles + T2Period; else if ((v & 2) == 0) nextT2 = long.MaxValue;
                        timerFlags &= ~(v & 0x60);
                    }
                    return;
            }
            Emit(1, reg, v);
        }

        private void Emit(int chip, int reg, int val)
        {
            // 같은 값 다시 쓰기는 버린다 — YM2203 키온 28h · SSG 엔벨로프 0Dh · 타이머 27h · 주파수 A0h~AEh는 늘 (OPL은 모두 버려도 됨)
            bool keep = chip == 0 && (reg == 0x28 || reg == 0x0D || reg == 0x27 || (reg >= 0xA0 && reg <= 0xAE));
            if (chip == 0 && reg >= 0x24 && reg <= 0x27) { if (reg != 0x27) return; val &= 0xC0; }   // YM2203 타이머는 아무 데도 안 이어짐
            if (!keep && last[chip << 8 | reg] == val) return;
            last[chip << 8 | reg] = val;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = L.Name + " (hoot)", Notes = "hoot " + L.Name };
            w.SetClock(VgmWriter.ClockYm2203, OpnClock);
            w.SetClock(VgmWriter.ClockYm3526, L.OplClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(x.chip == 0 ? (byte)0x55 : (byte)0x5B, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
