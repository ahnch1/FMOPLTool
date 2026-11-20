using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 타이토 Bubble Bobble 보드 (driver taito, type bubblebobble: Bubble Bobble · Tokio (hoot가 고친 ROM = 같은 배치)): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME bublbobl sound_map + 디스어셈블): Z80 3MHz, 0000h~7FFFh ROM, 8000h~8FFFh RAM, 9000h · 9001h = YM2203 (3MHz), A000h · A001h = YM3526 (3MHz),
/// B000h 읽기 = 명령 (오면 대기 → NMI 켜져 있으면 NMI), B001h 쓰기 = NMI 켬, B002h = NMI 끔, E000h~ ROM (없으면 FFh). IRQ = 두 YM의 타이머 (레벨).</para>
/// <para>곡: rom code = Z80 ROM. 부팅 1초 뒤 명령, 180초를 YM2203 + YM3526 VGM으로.</para>
/// </summary>
public static class HootTaitoBubble
{
    public const double CpuClock = 3000000, YmClock = 3000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "taito", StringComparison.OrdinalIgnoreCase)) return false;
        return (type ?? "").Trim().ToLowerInvariant() == "bubblebobble";
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
        var rom = Enumerable.Repeat((byte)0xFF, 0x10000).ToArray();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0 || off >= 0x8000) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            Array.Copy(d, 0, rom, off, (int)Math.Min(d.Length, 0x8000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var m = new Machine(rom);
        if (Environment.GetEnvironmentVariable("HOOT_DEBUG") != null) m.PcCount = new Dictionary<int, int>();
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        // 타이토 드라이버: 처음엔 소리가 꺼져 있고 명령 EFh (소리 켬)가 있어야 곡을 낸다 (8FAAh · 87A0h 깃발)
        m.Send(0xEF);
        m.RunSeconds(0.2);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom;
        public readonly byte[] Ram = new byte[0x1000];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2203, 1 = YM3526.</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, Nmis, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch, opnAddr, oplAddr;
        private bool pending, nmiEnabled, nmiLine;
        private long playStart = -1;
        // YM2203 타이머
        private int ta, tb, tctl, tflags;
        private long nextA = long.MaxValue, nextB = long.MaxValue;
        // YM3526 타이머
        private int t1, t2, octl, oflags;
        private long next1 = long.MaxValue, next2 = long.MaxValue;
        private readonly int[,] last = new int[2, 0x100];

        public Machine(byte[] rom)
        {
            Rom = rom;
            for (int c = 0; c < 2; c++) for (int r = 0; r < 0x100; r++) last[c, r] = -1;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        /// <summary>곡 전 명령 (기록 남김).</summary>
        public void Send(int code) { latch = code & 0xFF; pending = true; UpdateNmi(); }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            latch = code & 0xFF;
            pending = true;
            UpdateNmi();
        }

        private void UpdateNmi()
        {
            bool line = nmiEnabled && pending;
            if (line && !nmiLine)
            {
                Nmis++;
                Cpu.Halted = false;
                Cpu.Iff2 = Cpu.Iff1; Cpu.Iff1 = false;
                Push(Cpu.PC);
                Cpu.PC = 0x66;
                Cpu.Cycles += 11;
            }
            nmiLine = line;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        private long PeriodA { get { return Math.Max(1, (long)(72.0 * (1024 - ta) / YmClock * CpuClock)); } }
        private long PeriodB { get { return Math.Max(1, (long)(1152.0 * (256 - tb) / YmClock * CpuClock)); } }
        private long Period1 { get { return Math.Max(1, (long)(288.0 * (256 - t1) / YmClock * CpuClock)); } }
        private long Period2 { get { return Math.Max(1, (long)(1152.0 * (256 - t2) / YmClock * CpuClock)); } }

        private bool IrqLine { get { return (tflags & 3) != 0 || (oflags & 0x80) != 0; } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextA) { nextA += PeriodA; if ((tctl & 4) != 0) tflags |= 1; }
                if (now >= nextB) { nextB += PeriodB; if ((tctl & 8) != 0) tflags |= 2; }
                if (now >= next1) { next1 += Period1; if ((octl & 0x40) == 0) oflags |= 0xC0; }
                if (now >= next2) { next2 += Period2; if ((octl & 0x20) == 0) oflags |= 0xA0; }
                if (IrqLine && Cpu.Iff1)
                {
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted && !IrqLine)
                {
                    long next = Math.Min(Math.Min(nextA, nextB), Math.Min(next1, next2));
                    if (next >= end) { Cpu.Cycles = end; break; }
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 4, next);
                    continue;
                }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                if (PcCount != null) { ring[ringAt++ & 31] = Cpu.PC; if (Cpu.PC == 0 && Cpu.Cycles > 100 && ResetTrail == null) ResetTrail = string.Join(" ", Enumerable.Range(0, 32).Select(i => ring[(ringAt + i) & 31].ToString("X4"))); }
                Cpu.Step();
            }
        }

        /// <summary>진단: null이 아니면 PC마다 횟수.</summary>
        public Dictionary<int, int> PcCount;
        public string ResetTrail;
        private readonly int[] ring = new int[32];
        private int ringAt;

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000 || a >= 0xE000) return Rom[a];
            if (a < 0x9000) return Ram[a - 0x8000];
            if (a == 0x9000) return (byte)tflags;
            if (a == 0x9001) return opnAddr < 0x10 ? ssg[opnAddr] : (byte)0;   // SSG 레지스터 되읽기 (부팅 때 7h = 3Fh를 확인, 아니면 RST 0)
            if (a == 0xA000 || a == 0xA001) return (byte)oflags;
            if (a == 0xB000) { LatchReads++; pending = false; UpdateNmi(); return (byte)latch; }
            if (a == 0x9800) return 0;   // Tokio: 메인 쪽 응답 래치 상태 (bit1 = 아직 안 읽음) — 늘 비어 있음
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x8000 && a < 0x9000) { Ram[a - 0x8000] = v; return; }
            if (a < 0x8000 || a >= 0xE000) return;
            switch (a)
            {
                case 0x9000: opnAddr = v; return;
                case 0x9001: OpnWrite(opnAddr, v); return;
                case 0xA000: oplAddr = v; return;
                case 0xA001: OplWrite(oplAddr, v); return;
                case 0xB000: return;   // 메인에 답
                case 0xB001: nmiEnabled = true; UpdateNmi(); return;
                case 0xB002: nmiEnabled = false; UpdateNmi(); return;
            }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int p) { return 0xFF; }
        public void Out(int p, byte v) { }

        private readonly byte[] ssg = new byte[16];

        private void OpnWrite(int reg, int v)
        {
            if (reg < 0x10) ssg[reg] = (byte)v;
            switch (reg)
            {
                case 0x24: ta = (ta & 3) | (v << 2); return;
                case 0x25: ta = (ta & 0x3FC) | (v & 3); return;
                case 0x26: tb = v; return;
                case 0x27:
                    {
                        int old = tctl;
                        tctl = v;
                        if ((v & 0x10) != 0) tflags &= ~1;
                        if ((v & 0x20) != 0) tflags &= ~2;
                        if ((v & 1) != 0 && (old & 1) == 0) nextA = Cpu.Cycles + PeriodA; else if ((v & 1) == 0) nextA = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextB = Cpu.Cycles + PeriodB; else if ((v & 2) == 0) nextB = long.MaxValue;
                        Emit(0, reg, v & 0xC0);
                        return;
                    }
            }
            Emit(0, reg, v);
        }

        private void OplWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x02: t1 = v; return;
                case 0x03: t2 = v; return;
                case 0x04:
                    {
                        if ((v & 0x80) != 0) { oflags = 0; return; }
                        int old = octl;
                        octl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) next1 = Cpu.Cycles + Period1; else if ((v & 1) == 0) next1 = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) next2 = Cpu.Cycles + Period2; else if ((v & 2) == 0) next2 = long.MaxValue;
                        return;
                    }
            }
            if (reg < 0x20 && reg != 0x08) return;
            Emit(1, reg, v);
        }

        private void Emit(int chip, int reg, int val)
        {
            bool keep = chip == 0 ? reg == 0x28 || reg == 0x0D || reg == 0x27 || (reg >= 0xA0 && reg <= 0xAE) : (reg >= 0xB0 && reg <= 0xB8) || reg == 0xBD;
            if (!keep) { if (last[chip, reg] == val) return; last[chip, reg] = val; }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Taito Bubble Bobble (hoot)", Notes = "hoot taito bubblebobble" };
            w.SetClock(VgmWriter.ClockYm2203, YmClock);
            bool opl = Log.Any(x => x.chip == 1);
            if (opl) w.SetClock(VgmWriter.ClockYm3526, YmClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write((byte)(x.chip == 0 ? 0x55 : 0x5B), x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
