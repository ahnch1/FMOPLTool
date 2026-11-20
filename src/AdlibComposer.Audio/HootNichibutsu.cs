using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 니치부츠 보드 (driver nichibutsu, type terracre: Terra Cresta · Galivan · Dangar · Ninja Emaki · Amazon · Kid no Hore Hore,
/// type terraf: Terra Force · Kozure Ookami · Armed Formation · Sky Robo): 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME terracre · galivan · armedf sound_map / sound_portmap — 셋이 같은 꼴): Z80 4MHz, ROM 0000h~ (48KB 이하면 ~BFFFh, RAM C000h~, 아니면 ~F7FFh, RAM F800h~),
/// I/O 00h · 01h = YM3526 / YM3812 (이름대로, 4MHz), 02h · 03h = 8비트 DAC 둘, 04h 읽기 = 명령 지움, 06h 읽기 = 명령 (드라이버가 폴링, NMI 없음) — 명령 = (코드 &lt;&lt; 1) | 1, bit0 = 새 명령.
/// 인터럽트 = 4MHz / 512 (7812.5Hz) 주기 IRQ (IM 1 또는 IM 2) — 음악 틱 · DAC 출력이 다 이것으로.</para>
/// <para>DAC 둘은 VGM에 그냥 DAC가 없어 YM2612 DAC (2Ah, 2Bh = 80h)에 둘의 평균으로 (값이 바뀔 때만).
/// 곡: rom code = Z80 ROM, 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM3526/YM3812 + YM2612 (DAC) VGM으로.</para>
/// </summary>
public static class HootNichibutsu
{
    public const double CpuClock = 4000000, OplClock = 4000000, IrqHz = 4000000.0 / 512;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        string t = (type ?? "").Trim().ToLowerInvariant();
        return string.Equals((driver ?? "").Trim(), "nichibutsu", StringComparison.OrdinalIgnoreCase) && (t == "terracre" || t == "terraf");
    }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        var code = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            code.Add((off, d));
        }
        if (code.Count == 0) missing.Add("(code)");
        long end = code.Count == 0 ? 0 : code.Max(c => c.off + c.d.Length);
        var mem = new byte[0x10000];
        foreach (var (off, d) in code) Array.Copy(d, 0, mem, off, (int)Math.Min(d.Length, 0x10000 - off));
        bool ym3812 = (e.Name ?? "").IndexOf("YM3812", StringComparison.OrdinalIgnoreCase) >= 0;
        var m = new Machine(mem, end <= 0xC000 ? 0xC000 : 0xF800, ym3812);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Mem;
        public readonly int RamStart;
        public readonly bool Ym3812;
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = OPL (reg, val), 1 = DAC (두 DAC 평균, 0~255).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, LatchReads, DacWrites;
        public readonly Dictionary<int, int> OtherPorts = new Dictionary<int, int>();

        private int latch, oplAddr;
        private readonly int[] dac = { 0x80, 0x80 };
        private int lastDac = -1;
        private long playStart = -1;
        private readonly double irqCycles = CpuClock / IrqHz;
        private double nextIrq;
        private bool irqPending;
        private readonly Dictionary<int, int> regs = new Dictionary<int, int>();

        public Machine(byte[] mem, int ramStart, bool ym3812)
        {
            Mem = mem; RamStart = ramStart; Ym3812 = ym3812;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextIrq = irqCycles;
        }

        public void Play(int code)
        {
            var init = Log.Where(x => x.chip == 0).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            lastDac = -1;
            playStart = Cpu.Cycles;
            latch = ((code & 0x7F) << 1) | 1;   // 68000이 (코드 << 1) | 1로 쓴다 — 드라이버가 bit0을 보고 오른쪽으로 민다 (MAME terracre · armedf soundlatch_w)
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq) { nextIrq += irqCycles; irqPending = true; }
                if (irqPending && Cpu.Iff1)
                {
                    irqPending = false;   // HOLD: 받으면 내림
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    if (Cpu.Im == 2) { int vec = (Cpu.I << 8) | 0xFF; Cpu.PC = Read(vec) | (Read((vec + 1) & 0xFFFF) << 8); Cpu.Cycles += 19; }
                    else { Cpu.PC = 0x38; Cpu.Cycles += 13; }
                }
                if (Cpu.Halted) { Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, (long)Math.Ceiling(nextIrq))); continue; }
                Cpu.Step();
            }
        }

        public byte Read(int a) { return Mem[a & 0xFFFF]; }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= RamStart) Mem[a] = v;
        }

        public byte In(int port)
        {
            port &= 0xFF;
            switch (port)
            {
                case 0x00: case 0x01: return 0;                    // OPL 상태 (타이머 안 씀)
                case 0x04: latch = 0; return 0xFF;                 // 명령 지움
                case 0x06: LatchReads++; return (byte)latch;
            }
            OtherPorts.TryGetValue(0x100 | port, out int c); OtherPorts[0x100 | port] = c + 1;
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            switch (port)
            {
                case 0x00: oplAddr = v; return;
                case 0x01: OplWrite(oplAddr, v); return;
                case 0x02: case 0x03:
                    {
                        dac[port - 2] = v;
                        DacWrites++;
                        int mix = (dac[0] + dac[1]) >> 1;
                        if (mix != lastDac) { lastDac = mix; Emit(1, 0x2A, mix); }
                        return;
                    }
            }
            OtherPorts.TryGetValue(port, out int c); OtherPorts[port] = c + 1;
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        private void OplWrite(int reg, int v)
        {
            if (reg >= 2 && reg <= 4) return;   // 타이머: 이 보드는 주기 IRQ를 쓴다
            if (regs.TryGetValue(reg, out int prev) && prev == v) return;   // OPL은 같은 값 다시 쓰기가 아무 일 없음
            regs[reg] = v;
            Emit(0, reg, v);
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Nichibutsu (hoot)", Notes = "hoot nichibutsu" };
            if (Ym3812) w.SetClock(VgmWriter.ClockYm3812, OplClock); else w.SetClock(VgmWriter.ClockYm3526, OplClock);
            bool dacUsed = Log.Any(x => x.chip == 1 && x.val != 0x80);
            if (dacUsed) { w.SetClock(VgmWriter.ClockYm2612, 7670453); w.Write(0x52, 0x2B, 0x80); w.Write(0x52, 0x2A, 0x80); }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !dacUsed) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(Ym3812 ? (byte)0x5A : (byte)0x5B, x.reg, x.val);
                else w.Write(0x52, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
