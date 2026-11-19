using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 아이렘 M72 (driver irem, type m72): 사운드 보드를 그대로 흉내 내고 Z80 사운드 프로그램을 돌린다.
/// <para>기계 (MAME m72 sound_portmap): Z80 3.58MHz, 메모리 0000h~FFFFh. 사운드 ROM이 있는 판 (transfer 0) = ROM 0000h~EFFFh + RAM F000h~,
/// 없는 판 (transfer 1) = 메인 CPU(V30)가 자기 ROM의 Z80 프로그램을 64KB RAM에 옮겨 줌 — rom code 0 · 1 = 짝수 · 홀수 바이트, 그 안 z80_offset부터.
/// 포트 00h · 01h = YM2151, 02h 읽기 = 메인 CPU의 명령, 06h 쓰기 = 명령 인터럽트 받음 (뒤 세대 M82 · M84 · M90 · M97은 80h · 83h, New Dyna Blaster · Pound for Pound는 YM2151 40h · 41h, 명령 42h), 82h = DAC 샘플 (버림), 84h 읽기 = 샘플 (0).
/// 인터럽트 (RST_NEG 버퍼): IM 0이면 YM2151 타이머 = RST 28h, 명령 = RST 18h, 둘 다 = RST 08h, IM 1이면 0038h, IM 2면 벡터 표.</para>
/// <para>곡: 부팅 1초 뒤 명령 00h (초기화 핸드셰이크), 곡 코드를 명령으로, 180초를 YM2151 VGM으로 (DAC 북 · 목소리는 빠짐).</para>
/// </summary>
public static class HootM72
{
    public const double CpuClock = 3579545, YmClock = 3579545;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "irem", StringComparison.OrdinalIgnoreCase) && string.Equals((type ?? "").Trim(), "m72", StringComparison.OrdinalIgnoreCase);
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
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            code.Add((off, d));
        }
        bool transfer = e.Option("transfer", 0) != 0;
        var mem = new byte[0x10000];
        if (code.Count == 0) missing.Add("(code)");
        else if (transfer)
        {
            // V30 16비트 ROM: offset 0 = 짝수 바이트, 1 = 홀수 바이트
            int half = code.Max(c => c.d.Length);
            var main = new byte[half * 2];
            foreach (var (off, d) in code)
                for (int i = 0; i < d.Length; i++) main[i * 2 + (int)(off & 1)] = d[i];
            long z = e.Option("z80_offset", 0);
            for (int i = 0; i < mem.Length && z + i < main.Length; i++) mem[i] = main[z + i];
        }
        else
        {
            long min = code.Min(c => c.off);
            foreach (var (off, d) in code)
            {
                long at = off - min;
                if (at < 0xF000) Array.Copy(d, 0, mem, at, (int)Math.Min(d.Length, 0xF000 - at));
            }
        }
        var m = new Machine(mem, transfer ? 0x10000 : 0xF000);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        // 아이렘 드라이버 (Air Duel · M82~M97 …): 메인 CPU가 초기화 때 보내는 명령 00h를 받기 전에는 다른 명령을 버린다 — 먼저 00h (보통 "곡 멈춤")
        m.Send(0x00);
        m.RunSeconds(0.1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Mem;
        private readonly int romEnd;
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        public readonly List<(double t, int reg, int val)> Log = new List<(double, int, int)>();
        public int LatchIrqs, TimerIrqs, LatchReads;
        public Dictionary<int, int> PcCount;

        private int latch, ymAddr;
        private bool latchIrq;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags, overB;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;

        /// <summary>mem = 64KB, romEnd = 쓸 수 없는 끝 (transfer 판은 전부 RAM).</summary>
        public Machine(byte[] mem, int romEnd)
        {
            Mem = mem; this.romEnd = romEnd;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 0;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        public void Send(int code) { latch = code; latchIrq = true; }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        private long TimerAPeriod { get { return Math.Max(1, (long)(64.0 * (1024 - timerA) / YmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1024.0 * (256 - timerB) / YmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; overB++; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                bool ym = timerFlags != 0;
                if ((ym || latchIrq) && Cpu.Iff1)
                {
                    int target = ym && latchIrq ? 0x08 : ym ? 0x28 : 0x18;   // IM 0: RST_NEG 벡터 CFh · EFh · DFh = RST 08h · 28h · 18h
                    if (Cpu.Im == 1) target = 0x38;
                    else if (Cpu.Im == 2) { int vec = (Cpu.I << 8) | (0xC7 | target); target = Read(vec) | (Read(vec + 1) << 8); }
                    if (ym) TimerIrqs++; else LatchIrqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = target;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted)
                {
                    long next = Math.Min(end, Math.Min(nextTimerA, nextTimerB));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                    continue;
                }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                Cpu.Step();
            }
        }

        public byte Read(int a) { return Mem[a & 0xFFFF]; }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= romEnd || romEnd == 0x10000) Mem[a] = v;
        }

        public byte In(int port)
        {
            switch (port & 0xFF)
            {
                case 0x00: case 0x01: case 0x40: case 0x41: return (byte)(timerFlags & 3);
                case 0x02: case 0x80: case 0x42: LatchReads++; return (byte)latch;   // 42h = New Dyna Blaster · Pound for Pound   // 80h = M82 · M84 · M90 · M97 (MAME rtype2_sound_portmap)
                case 0x84: return 0;
            }
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            switch (port & 0xFF)
            {
                case 0x00: case 0x40: ymAddr = v; break;
                case 0x01: case 0x41: YmWrite(ymAddr, v); break;
                case 0x06: case 0x83: case 0x42: case 0x43: latchIrq = false; break;
            }
        }

        private void YmWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x10: timerA = (timerA & 3) | (v << 2); return;
                case 0x11: timerA = (timerA & 0x3FC) | (v & 3); return;
                case 0x12: timerB = v; return;
                case 0x14:
                    {
                        int old = timerCtl;
                        timerCtl = v;
                        if ((v & 0x10) != 0) timerFlags &= ~1;
                        if ((v & 0x20) != 0) timerFlags &= ~2;
                        if ((v & 1) != 0 && (old & 1) == 0) nextTimerA = Cpu.Cycles + TimerAPeriod; else if ((v & 1) == 0) nextTimerA = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextTimerB = Cpu.Cycles + TimerBPeriod; else if ((v & 2) == 0) nextTimerB = long.MaxValue;
                        return;
                    }
            }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), reg, v));
        }

        /// <summary>진단: 타이머 레지스터 (TA · TB · 14h).</summary>
        public string TimerInfo { get { return "TA " + timerA + " TB " + timerB + " ctl " + timerCtl.ToString("X2") + " B-over " + overB; } }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Irem M72 (hoot)", Notes = "hoot irem m72" };
            w.SetClock(VgmWriter.ClockYm2151, YmClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(0x54, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
