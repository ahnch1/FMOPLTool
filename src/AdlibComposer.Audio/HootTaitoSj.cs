using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 타이토 SJ 사운드 보드 (driver taito, type taitosj: Elevator Action · Space Seeker · Alpine Ski · Front Line · Time Tunnel · Wild Western · Bio Attack · High Way Race · Tinstar · Water Ski · Kick Start · Sea Fighter Poseidon): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME taitosj sound + Elevator Action 디스어셈블): Z80 3MHz, ROM 0000h~3FFFh, RAM 4000h~43FFh, AY-3-8910 × 3 (1.5MHz) = 4800h · 4802h · 4804h (짝수 = 주소, 홀수 = 자료, 드라이버는 LD (nn),HL로 둘을 한 번에),
/// 5000h = 명령 (메인이 쓰면 NMI), 5001h 읽기 = 깃발 (bit3 = 명령 있음 — 5001h 쓰기로 지움), E000h = 진단 ROM (없음 — C3h가 아니면 건너뜀). IRQ = 주기 6MHz ÷ 163840 ≈ 36.6Hz. DAC = AY #1 (4800h) 포트 A, 볼륨 = 포트 B.</para>
/// <para>곡: 부팅 1초 뒤 명령, 180초를 AY × 3 (셋째 = 이 프로그램 안 명령 D9h) + DAC (VGM YM2612 DAC) VGM으로.</para>
/// </summary>
public static class HootTaitoSj
{
    public const double CpuClock = 3000000, AyClock = 1500000, IrqHz = 6000000.0 / (4 * 16 * 16 * 10 * 16);
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "taito", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "taitosj", StringComparison.OrdinalIgnoreCase);
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
        /// <summary>쓰기: chip 0~2 = AY #1~#3, 3 = DAC (reg 0 = 8비트 표본, 볼륨 곱한 값).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, Nmis, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch;
        private bool latchFlag;
        private readonly int[] ayAddr = new int[3];
        private readonly int[,] ayReg = new int[3, 16];
        private readonly int[,] last = new int[3, 16];
        private int dacData = 0x80, dacVol = 0, lastDac = -1;
        private long playStart = -1;
        private readonly double irqCycles = CpuClock / IrqHz;
        private double nextIrq;
        private bool irqLine;

        public Machine(byte[] rom)
        {
            Rom = rom;
            for (int c = 0; c < 3; c++) for (int r = 0; r < 16; r++) last[c, r] = -1;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextIrq = irqCycles;
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
            latchFlag = true;
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

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq) { nextIrq += irqCycles; irqLine = true; }
                if (irqLine && Cpu.Iff1)
                {
                    irqLine = false;   // hold: 받으면 내려감
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted) { Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, (long)Math.Ceiling(nextIrq))); continue; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return Rom[a];
            if (a < 0x4400) return Ram[a - 0x4000];
            if (a >= 0x4800 && a < 0x4806) return (a & 1) != 0 ? (byte)ayReg[(a - 0x4800) >> 1, ayAddr[(a - 0x4800) >> 1]] : (byte)0xFF;
            if (a == 0x5000) { LatchReads++; return (byte)latch; }
            if (a == 0x5001) return (byte)(latchFlag ? 0x08 : 0x00);   // soundlatch_flags_r: bit3 = 명령 있음, bit0 = 세마포어 (없음)
            if (a >= 0xE000 && a < 0xF000) return 0xFF;   // 진단 ROM 없음
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return;
            if (a < 0x4400) { Ram[a - 0x4000] = v; return; }
            if (a >= 0x4800 && a < 0x4806)
            {
                int chip = (a - 0x4800) >> 1;
                if ((a & 1) == 0) ayAddr[chip] = v & 15; else AyWrite(chip, ayAddr[chip], v);
                return;
            }
            if (a == 0x5001) { latchFlag = false; return; }   // 명령 깃발 지움
            if (a == 0x5000) return;   // 세마포어 지움
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { }

        private void AyWrite(int chip, int reg, int v)
        {
            ayReg[chip, reg] = v;
            if (chip == 0 && reg == 14) { dacData = v; Dac(); return; }   // DAC 표본
            if (chip == 0 && reg == 15) { dacVol = v ^ 0xFF; Dac(); return; }   // DAC 볼륨 (낮을수록 큼)
            if (reg >= 14) return;
            if (reg != 13 && last[chip, reg] == v) return;
            last[chip, reg] = v;
            Emit(chip, reg, v);
        }

        private void Dac()
        {
            int s = 0x80 + (dacData - 0x80) * dacVol / 255;
            if (s == lastDac) return;
            lastDac = s;
            Emit(3, 0, s);
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Taito SJ (hoot)", Notes = "hoot taito taitosj" };
            w.SetClock(VgmWriter.ClockAy8910, AyClock, 0x40000000u);   // bit30 = AY 둘 (셋째는 D9h)
            bool dac = Log.Any(x => x.chip == 3 && x.val != 0x80);
            if (dac) { w.SetClock(VgmWriter.ClockYm2612, 7670453); w.Write(0x52, 0x2B, 0x80); w.Write(0x52, 0x2A, 0x80); }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 3 && !dac) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip <= 1) w.Write(0xA0, x.reg | (x.chip << 7), x.val);
                else if (x.chip == 2) w.Write(new byte[] { 0xD9, (byte)x.reg, (byte)x.val, 0 });   // 셋째 AY (이 프로그램 안 명령)
                else w.Write(0x52, 0x2A, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Any(x => x.chip == 2)) v.AyClock3 = AyClock;
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
