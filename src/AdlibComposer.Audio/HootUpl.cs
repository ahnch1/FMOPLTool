using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot UPL 보드 (driver upl, type robokid: Psychic 5 · Ark Area · Mutant Night · Atomic Robo-kid · Omega Fighter): 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME ninjakd2 · psychic5 sound_map / sound_io_map): Z80 5MHz, 0000h~BFFFh = ROM, C000h~C7FFh = RAM (쓰기는 C000h~FFFFh 모두 받음), E000h 읽기 = 명령 (폴링),
/// I/O 00h · 01h = YM2203 #1, 80h · 81h = YM2203 #2 (각 1.5MHz). YM2203 #1 타이머 = IRQ (IM 1).</para>
/// <para>곡: rom code = Z80 ROM, 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM2203 둘 VGM으로 (머리말 클럭 bit30 + 명령 A5h).</para>
/// </summary>
public static class HootUpl
{
    public const double CpuClock = 5000000, OpnClock = 1500000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "upl", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "robokid", StringComparison.OrdinalIgnoreCase);
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
        var mem = new byte[0x10000];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            Array.Copy(d, 0, mem, off, (int)Math.Min(d.Length, Math.Min(0xC000, 0x10000 - off)));   // ROM은 BFFFh까지
            any = true;
        }
        if (!any) missing.Add("(code)");
        var m = new Machine(mem);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Mem;
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2203 #1, 1 = YM2203 #2.</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherPorts = new Dictionary<int, int>();

        private int latch;
        private readonly int[] addr = new int[2];
        private long playStart = -1;
        // YM2203 #1 타이머: A = 72 × (1024 − TA) / 클럭, B = 1152 × (256 − TB) / 클럭
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;

        public Machine(byte[] mem)
        {
            Mem = mem;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            latch = code & 0xFF;
        }

        private long TimerAPeriod { get { return Math.Max(1, (long)(72.0 * (1024 - timerA) / OpnClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1152.0 * (256 - timerB) / OpnClock * CpuClock)); } }

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
            if (a == 0xE000) { LatchReads++; return (byte)latch; }
            return Mem[a];
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xC000) Mem[a] = v;
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if (port == 0x00) return (byte)(timerFlags & 3);   // YM2203 #1 상태 (바쁨 없음)
            if (port == 0x80) return 0;
            if (port == 0x01 || port == 0x81) return 0;
            OtherPorts.TryGetValue(0x100 | port, out int c); OtherPorts[0x100 | port] = c + 1;
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            switch (port)
            {
                case 0x00: addr[0] = v; return;
                case 0x01: OpnWrite(0, addr[0], v); return;
                case 0x80: addr[1] = v; return;
                case 0x81: OpnWrite(1, addr[1], v); return;
            }
            OtherPorts.TryGetValue(port, out int c); OtherPorts[port] = c + 1;
        }

        private void OpnWrite(int chip, int reg, int v)
        {
            if (chip == 0)
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
                            Emit(0, reg, v & 0xC0);   // 3채널 모드 비트만
                            return;
                        }
                }
            }
            Emit(chip, reg, v);
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "UPL (hoot)", Notes = "hoot upl robokid" };
            w.SetClock(VgmWriter.ClockYm2203, OpnClock, 0x40000000u);   // bit30 = YM2203 둘
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(x.chip == 0 ? (byte)0x55 : (byte)0xA5, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
