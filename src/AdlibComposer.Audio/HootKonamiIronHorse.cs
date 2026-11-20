using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 코나미 Z80 + YM2203 보드 (driver konami, type ironhors: Iron Horse · Scooter Shooter): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME ironhors · scotrsht 사운드 맵 + 디스어셈블): Z80 3.072MHz, 0000h~3FFFh ROM, 4000h~43FFh RAM, 8000h = 명령 (메인이 쓰면 IRQ, hold),
/// I/O 00h · 01h = YM2203 (3.072MHz). 박자 = 본 고리가 YM2203 상태 bit1 (타이머 B)을 폴링, IRQ 처리기는 명령만 받는다.</para>
/// <para>곡: rom code = Z80 ROM. 부팅 1초 뒤 명령, 180초를 YM2203 VGM으로.</para>
/// </summary>
public static class HootKonamiIronHorse
{
    public const double CpuClock = 3072000, OpnClock = 3072000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "konami", StringComparison.OrdinalIgnoreCase)) return false;
        return (type ?? "").Trim().ToLowerInvariant() == "ironhors";
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
        var rom = new byte[0x4000];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0 || off >= 0x4000) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            Array.Copy(d, 0, rom, off, (int)Math.Min(d.Length, 0x4000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var m = new Machine(rom);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom;
        public readonly byte[] Ram = new byte[0x400];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>YM2203 쓰기 (reg, val).</summary>
        public readonly List<(double t, int reg, int val)> Log = new List<(double, int, int)>();
        public int Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch, opnAddr;
        private bool irqLine;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();

        public Machine(byte[] rom)
        {
            Rom = rom;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            latch = code & 0xFF;
            irqLine = true;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        private long TimerAPeriod { get { return Math.Max(1, (long)(72.0 * (1024 - timerA) / OpnClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1152.0 * (256 - timerB) / OpnClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (irqLine && Cpu.Iff1)
                {
                    irqLine = false;
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted && !irqLine) { Cpu.Cycles = end; break; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return Rom[a];
            if (a < 0x8000) return Ram[a & 0x3FF];
            if (a == 0x8000) { LatchReads++; return (byte)latch; }
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x4000 && a < 0x8000) { Ram[a & 0x3FF] = v; return; }
            if (a < 0x4000) return;
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int p)
        {
            p &= 0xFF;
            if (p == 0) return (byte)timerFlags;   // 상태: bit7 바쁨 아님, bit0 · bit1 타이머
            if (p == 1) return 0xFF;
            return 0xFF;
        }

        public void Out(int p, byte v)
        {
            p &= 0xFF;
            if (p == 0) { opnAddr = v; return; }
            if (p == 1) { OpnWrite(opnAddr, v); return; }
        }

        private void OpnWrite(int reg, int v)
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
                        Emit(reg, v & 0xC0);
                        return;
                    }
            }
            Emit(reg, v);
        }

        private void Emit(int reg, int val)
        {
            bool keep = reg == 0x28 || reg == 0x0D || reg == 0x27 || (reg >= 0xA0 && reg <= 0xAE);
            if (!keep) { if (last[reg] == val) return; last[reg] = val; }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Konami Iron Horse (hoot)", Notes = "hoot konami ironhors" };
            w.SetClock(VgmWriter.ClockYm2203, OpnClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(0x55, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
