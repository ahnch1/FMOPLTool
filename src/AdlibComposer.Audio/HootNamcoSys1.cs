using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 남코 System 1 (driver namco, type system1: Shadowland · Dragon Spirit · Blazer · Pac-Mania · Galaga '88 · World Stadium …): 사운드 보드를 흉내 내고 6809 사운드 ROM을 돌린다.
/// <para>기계 (MAME namcos1 sound_map, 블레이저 디스어셈블로 확인): 6809 1.536MHz (49.152MHz ÷ 32), 0000h~3FFFh = 뱅크 (사운드 ROM 16KB 단위, C000h · C001h에 쓴 값의 bit4~6),
/// 4000h · 4001h = YM2151 (3.58MHz), 5000h~53FFh (+ 거울 5400h~) = CUS30 (파형 RAM 5000h~ · 목소리 레지스터 5100h~ · 나머지는 메인 CPU와 같이 쓰는 RAM), 7000h~77FFh = 공유 RAM,
/// 8000h~9FFFh = RAM, C000h~FFFFh = 사운드 ROM 첫 16KB, D001h = 와치독, E000h = IRQ 확인. IRQ = 화면 (60Hz), FIRQ = YM2151 타이머 (FM 드라이버).</para>
/// <para>메인 CPU 쪽 (드라이버가 보는 것): 부팅 때 5000h = A6h를 기다림 (5001h = A6h면 자가 시험, 옛 판은 7000h = A6h). FM 곡 요청 = 공유 RAM 7100h 곡 번호 + 7101h 명령 (bit5~6이 서면 시작, 드라이버가 80h로 — 묶음 1~8개, 7102h · 7103h …), 블레이저만 7100h 한 바이트 (1~84h, FIRQ 처리기가 받아 F0h로),
/// CUS30 효과음 = 5240h + 슬롯에 0 아닌 값 (슬롯 표 = 뱅크 1의 [0000h] · 종류 [0002h], 종류 0은 값이 남아 있는 동안 울림). hoot 코드 = 묶음 번호 &lt;&lt; 8 | 곡 (묶음 1~ = 효과음, 블레이저는 100h 이상 = CUS30 효과음 슬롯).</para>
/// <para>곡: rom code = 사운드 ROM (offset 0 = s0, 10000h = s1), 부팅 1초 뒤 요청, 180초를 YM2151 VGM으로 (CUS30 파형 소리는 아직 없음).</para>
/// </summary>
public static class HootNamcoSys1
{
    public const double CpuClock = 49152000 / 32.0, OpmClock = 3579545, IrqHz = 60.606;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "namco", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "system1", StringComparison.OrdinalIgnoreCase);
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
        var rom = new byte[0x20000];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0 || off >= rom.Length) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            Array.Copy(d, 0, rom, off, (int)Math.Min(d.Length, rom.Length - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var m = new Machine(rom);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6809Bus
    {
        public readonly byte[] Rom;
        public readonly byte[] Cus30 = new byte[0x400], Shared = new byte[0x800], Ram = new byte[0x2000];
        public readonly Cpu6809 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151.</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, Firqs, Cus30Writes;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int bank, opmAddr;
        private long playStart = -1;
        private readonly double irqCycles = CpuClock / IrqHz;
        private double nextIrq;
        private bool irqLine;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();

        /// <summary>요청 방식: true = 7100h~ 묶음 (곡 번호, 명령) × 1~8 (거의 모두), false = 7100h 한 바이트 (블레이저 — ROM에 LDB $7100 · … · CMPB #$85).</summary>
        public readonly bool QueueDriver;
        /// <summary>묶음 방식의 시작 명령 (bit5~6 중 하나가 서면 시작, 드라이버가 80h로 바꿈).</summary>
        public int PlayCommand = 0x40;

        public Machine(byte[] rom)
        {
            Rom = rom;
            QueueDriver = true;
            for (int i = 0; i + 6 < 0x4000 && QueueDriver; i++) if (rom[i] == 0xF6 && rom[i + 1] == 0x71 && rom[i + 2] == 0x00 && rom[i + 5] == 0xC1 && rom[i + 6] == 0x85) QueueDriver = false;
            Cus30[0] = 0xA6;   // 메인 CPU의 부팅 맞춤 (5000h = A6h → 보통 실행, 5001h = A6h면 자가 시험)
            Shared[0] = 0xA6;  // 옛 판 (Shadowland · Mappy Maze · Beraboh Man · Blast Off): 7000h = A6h, 그 뒤 7001h = 0을 기다림
            Cpu = new Cpu6809(this);
            Cpu.Reset();
            nextIrq = irqCycles;
        }

        /// <summary>곡 요청: 코드 &lt; 100h = FM 곡 → 공유 RAM 7100h (드라이버가 받으면 F0h로 바꿈), 100h 이상 = CUS30 효과음 슬롯 → 5240h + 아래 바이트에 1 (음악 칸은 남아 있는 동안 울림).</summary>
        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            if (QueueDriver && code < 0x800) { int g = code >> 8; Shared[0x100 + g * 2] = (byte)code; Shared[0x101 + g * 2] = (byte)PlayCommand; }   // 묶음 (코드 위 바이트): 곡 번호 + 명령
            else if (code < 0x100) Shared[0x100] = (byte)code;
            else if (0x240 + (code & 0xFF) < Cus30.Length) Cus30[0x240 + (code & 0xFF)] = 1;
        }

        private long TimerAPeriod { get { return Math.Max(1, (long)(64.0 * (1024 - timerA) / OpmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1024.0 * (256 - timerB) / OpmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextIrq) { nextIrq += irqCycles; irqLine = true; }
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (irqLine && Cpu.Irq()) Irqs++;   // 화면 IRQ는 E000h에 쓸 때까지 걸려 있음
                if (timerFlags != 0 && Cpu.Firq()) Firqs++;   // YM2151 타이머 = FIRQ (FM 드라이버, 처리기가 14h로 확인)
                if (Cpu.Waiting)
                {
                    long next = Math.Min(end, Math.Min((long)Math.Ceiling(nextIrq), Math.Min(nextTimerA, nextTimerB)));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return Rom[(bank << 14) | a];
            if (a >= 0xC000) return Rom[a - 0xC000];
            if (a >= 0x8000 && a < 0xA000) return Ram[a - 0x8000];
            if (a >= 0x5000 && a < 0x5800) return Cus30[a & 0x3FF];
            if (a >= 0x7000 && a < 0x7800) return Shared[a - 0x7000];
            if (a == 0x4000 || a == 0x4001) return (byte)timerFlags;   // YM2151 상태: 바쁘지 않음
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x8000 && a < 0xA000) { Ram[a - 0x8000] = v; return; }
            if (a >= 0x5000 && a < 0x5800)
            {
                int o = a & 0x3FF;
                if (o < 0x140) Cus30Writes++;
                Cus30[o] = v;
                return;
            }
            if (a >= 0x7000 && a < 0x7800) { Shared[a - 0x7000] = v; return; }
            switch (a)
            {
                case 0x4000: opmAddr = v; return;
                case 0x4001: OpmWrite(opmAddr, v); return;
                case 0xC000: case 0xC001: bank = (v >> 4) & 7; return;
                case 0xD001: return;   // 와치독
                case 0xE000: irqLine = false; return;   // IRQ 확인
            }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        private void OpmWrite(int reg, int v)
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
                        Emit(0, reg, v & 0x80);
                        return;
                    }
            }
            Emit(0, reg, v);
        }

        private void Emit(int chip, int reg, int val)
        {
            // 같은 값 다시 쓰기는 버린다 — 키온 08h · LFO 리셋 01h는 늘
            if (reg != 0x08 && reg != 0x01 && last[reg] == val) return;
            last[reg] = val;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Namco System 1 (hoot)", Notes = "hoot namco system1" };
            w.SetClock(VgmWriter.ClockYm2151, OpmClock);
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
            if (Cus30Writes > 0) v.UnsupportedChips.Add("Namco CUS30");
            return v;
        }
    }
}
