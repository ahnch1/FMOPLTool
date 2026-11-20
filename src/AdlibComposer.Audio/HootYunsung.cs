using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 윤성 Z80 + YM3812 + OKIM6295 보드 (driver yunsung, type nmg5: Multi 5 / New Multi Game 5 · Search Eye · Wonder Stick,
/// type pclubys: Puzzle Club · Garogun Seroyang · 7 Ordi): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME nmg5.cpp nmg5_sound_map · sound_io_map): Z80 4MHz, 0000h~DFFFh ROM, E000h~E7FFh RAM (pclubys 배치는 RAM F800h~FFFFh); I/O 00h = OKI 뱅크 (bit0 × 40000h), 10h · 11h = YM3812 (4MHz),
/// 18h = 명령 (메인이 쓰면 NMI), 1Ch = OKIM6295 (1MHz, 핀7 높음, ROM = pcm). IRQ = YM3812 (MAME fmopl: 4h bit6 · 5 = 그 깃발 지움).</para>
/// <para>곡: 부팅 1초 뒤 명령, 180초를 YM3812 + OKIM6295 VGM으로.</para>
/// </summary>
public static class HootYunsung
{
    public const double CpuClock = 4000000, OplClock = 4000000, OkiClock = 1000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "yunsung", StringComparison.OrdinalIgnoreCase)) return false;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "nmg5" || t == "pclubys";
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
        var rom = new byte[0x10000];
        var pcm = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code" && type != "pcm") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "pcm") { pcm.Add((off, d)); continue; }
            if (off < 0 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            Array.Copy(d, 0, rom, off, (int)Math.Min(d.Length, 0x10000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var p = new byte[pcm.Count == 0 ? 0 : (int)pcm.Max(x => x.off + x.d.Length)];
        foreach (var (off, d) in pcm) Array.Copy(d, 0, p, off, d.Length);
        var m = new Machine(rom, p) { RamBase = (e.Type ?? "").Trim().ToLowerInvariant() == "pclubys" ? 0xF800 : 0xE000 };
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, Pcm;
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM3812, 1 = OKIM6295 (reg 0 = 명령, 0Fh = 뱅크).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, Nmis, LatchReads;
        /// <summary>RAM 자리: nmg5 = E000h, pclubys = F800h (스택 FFFFh 아래).</summary>
        public int RamBase = 0xE000;
        public readonly Dictionary<int, int> OtherOut = new Dictionary<int, int>(), OtherIn = new Dictionary<int, int>();

        private int latch, ymAddr;
        private long playStart = -1;
        private int t1, t2, ctl, flags;
        private long next1 = long.MaxValue, next2 = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();

        public Machine(byte[] rom, byte[] pcm)
        {
            Rom = rom; Pcm = pcm;
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
            Nmis++;
            Cpu.Halted = false;
            Cpu.Iff2 = Cpu.Iff1; Cpu.Iff1 = false;
            Push(Cpu.PC);
            Cpu.PC = 0x66;
            Cpu.Cycles += 11;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        private long Period1 { get { return Math.Max(1, (long)(288.0 * (256 - t1) / OplClock * CpuClock)); } }
        private long Period2 { get { return Math.Max(1, (long)(1152.0 * (256 - t2) / OplClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= next1) { next1 += Period1; flags |= 0x40; if ((flags & ~ctl & 0x60) != 0) flags |= 0x80; }
                if (now >= next2) { next2 += Period2; flags |= 0x20; if ((flags & ~ctl & 0x60) != 0) flags |= 0x80; }
                if ((flags & 0x80) != 0 && Cpu.Iff1)
                {
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted && (flags & 0x80) == 0)
                {
                    long next = Math.Min(next1, next2);
                    if (next >= end) { Cpu.Cycles = end; break; }
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 4, next);
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a >= RamBase && a < RamBase + 0x800) return Ram[a - RamBase];
            if (a < RamBase) return Rom[a];
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= RamBase && a < RamBase + 0x800) Ram[a - RamBase] = v;
        }

        public byte In(int p)
        {
            switch (p & 0xFF)
            {
                case 0x10: return (byte)flags;
                case 0x11: return 0;
                case 0x18: LatchReads++; return (byte)latch;
                case 0x1C: return 0;   // OKIM6295 상태: 쉼
            }
            OtherIn.TryGetValue(p & 0xFF, out int c); OtherIn[p & 0xFF] = c + 1;
            return 0xFF;
        }

        public void Out(int p, byte v)
        {
            switch (p & 0xFF)
            {
                case 0x00: Emit(1, 0x0F, v & 1); return;   // OKI 뱅크
                case 0x10: ymAddr = v; return;
                case 0x11: OplWrite(ymAddr, v); return;
                case 0x1C: Emit(1, 0, v); return;
            }
            OtherOut.TryGetValue(p & 0xFF, out int c); OtherOut[p & 0xFF] = c + 1;
        }

        private void OplWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x02: t1 = v; return;
                case 0x03: t2 = v; return;
                case 0x04:
                    {
                        if ((v & 0x80) != 0) { flags = 0; return; }
                        flags &= ~(v & 0x60);
                        if ((flags & ~v & 0x60) == 0) flags &= 0x7F;
                        int old = ctl;
                        ctl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) next1 = Cpu.Cycles + Period1; else if ((v & 1) == 0) next1 = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) next2 = Cpu.Cycles + Period2; else if ((v & 2) == 0) next2 = long.MaxValue;
                        return;
                    }
            }
            bool keep = (reg >= 0xB0 && reg <= 0xB8) || reg == 0xBD;
            if (!keep) { if (last[reg] == v) return; last[reg] = v; }
            Emit(0, reg, v);
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Yunsung (hoot)", Notes = "hoot yunsung" };
            w.SetClock(VgmWriter.ClockYm3812, OplClock);
            bool oki = Pcm.Length > 0 && Log.Any(x => x.chip == 1 && x.reg == 0);
            if (oki)
            {
                w.SetClock(VgmWriter.ClockOkim6295, OkiClock, 0x80000000u);   // 핀7 높음
                var cmd = new byte[7 + 8 + Pcm.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8B;
                int size = 8 + Pcm.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int len = Pcm.Length;
                cmd[7] = (byte)len; cmd[8] = (byte)(len >> 8); cmd[9] = (byte)(len >> 16); cmd[10] = (byte)(len >> 24);
                Array.Copy(Pcm, 0, cmd, 15, Pcm.Length);
                w.Write(cmd);
            }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !oki) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write((byte)(x.chip == 0 ? 0x5A : 0xB8), x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
