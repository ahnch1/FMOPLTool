using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 캡콤 Exed Exes (driver capcom, type exedexes): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME exedexes sound_map): Z80 3MHz, 0000h~3FFFh ROM, 4000h~47FFh RAM, 6000h = 명령, 8000h · 8001h = AY-3-8910 (주소 · 자료, 1.5MHz),
/// 8002h · 8003h = SN76489 × 2 (3MHz). IRQ = 프레임당 4번 (240Hz).</para>
/// <para>곡: rom code = Z80 ROM. 부팅 1초 뒤 명령, 180초를 AY + SN76489 × 2 VGM으로.</para>
/// </summary>
public static class HootCapcomPsg
{
    public const double CpuClock = 3000000, AyClock = 1500000, SnClock = 3000000, IrqHz = 240;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "capcom", StringComparison.OrdinalIgnoreCase)) return false;
        return (type ?? "").Trim().ToLowerInvariant() == "exedexes";
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
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = AY (reg, val), 1 = SN #1 (0, 바이트), 2 = SN #2.</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch, ayAddr;
        private long playStart = -1;
        private double nextIrq;
        private readonly int[] ayReg = new int[16], ayLast = Enumerable.Repeat(-1, 16).ToArray();

        public Machine(byte[] rom)
        {
            Rom = rom;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextIrq = CpuClock / IrqHz;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            latch = code & 0xFF;
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
            bool pending = false;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq) { nextIrq += CpuClock / IrqHz; pending = true; }
                if (pending && Cpu.Iff1)
                {
                    pending = false;
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted && !pending) { Cpu.Cycles = Math.Max(Cpu.Cycles + 4, Math.Min(end, (long)Math.Ceiling(nextIrq))); continue; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return Rom[a];
            if (a < 0x4800) return Ram[a - 0x4000];
            if (a == 0x6000) { LatchReads++; return (byte)latch; }
            if (a == 0x8001) return (byte)ayReg[ayAddr];
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x4000 && a < 0x4800) { Ram[a - 0x4000] = v; return; }
            if (a < 0x4000) return;
            switch (a)
            {
                case 0x8000: ayAddr = v & 15; return;
                case 0x8001:
                    ayReg[ayAddr] = v;
                    if (ayAddr >= 14) return;
                    if (ayAddr != 13 && ayLast[ayAddr] == v) return;
                    ayLast[ayAddr] = v;
                    Emit(0, ayAddr, v);
                    return;
                case 0x8002: Emit(1, 0, v); return;
                case 0x8003: Emit(2, 0, v); return;
            }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int p) { return 0xFF; }
        public void Out(int p, byte v) { }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Capcom Exed Exes (hoot)", Notes = "hoot capcom exedexes" };
            w.SetClock(VgmWriter.ClockAy8910, AyClock);
            w.SetClock(VgmWriter.ClockSn76489, SnClock, 0x40000000u);   // bit30 = SN 둘
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write((byte)0xA0, x.reg, x.val);
                else w.Write(new byte[] { (byte)(x.chip == 1 ? 0x50 : 0x30), (byte)x.val });
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
