using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 아타리 System 1 (driver atari, type system1: Marble Madness · Peter Pack-Rat · Indiana Jones and the Temple of Doom · Road Runner · Road Blasters): 사운드 보드를 흉내 내고 6502 사운드 ROM을 돌린다.
/// <para>기계 (MAME atarisy1 sound_map, ROM 스캔으로 확인): 6502 1.79MHz (14.318MHz ÷ 8), 0000h~0FFFh = RAM, 1000h~100Fh = VIA 6522 (TMS5220 음성 — 없음),
/// 1800h · 1801h = YM2151 (3.58MHz, 타이머 = IRQ), 1810h 읽기 = 명령 · 쓰기 = 응답, 1820h = 스위치 (bit3 = 명령 있음, bit7 = 자가 시험 끔), 1824h~1827h = LED, 1870h~187Fh = POKEY (1.79MHz), 4000h~FFFFh = ROM.
/// 명령은 메인 CPU가 쓰면 NMI (atari_sound_comm).</para>
/// <para>곡: rom code = 6502 ROM (offset = 주소), 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM2151 + POKEY VGM으로 (TMS5220 음성은 없음).</para>
/// </summary>
public static class HootAtariSys1
{
    public const double CpuClock = 14318181 / 8.0, OpmClock = 14318181 / 4.0, PokeyClock = CpuClock;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "atari", StringComparison.OrdinalIgnoreCase)
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
        var mem = new byte[0x10000];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0x4000 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            Array.Copy(d, 0, mem, off, (int)Math.Min(d.Length, 0x10000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var m = new Machine(mem);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6502Bus
    {
        public readonly byte[] Mem;
        public readonly Cpu6502 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151, 1 = POKEY.</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads, PokeyWrites, ViaWrites;
        /// <summary>진단: 알 수 없는 자리 쓰기 · 읽기.</summary>
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch, opmAddr, rnd = 0x1FFFF;
        private bool pending;
        private long playStart = -1;
        // YM2151 타이머: A = 64 × (1024 − TA) / 클럭, B = 1024 × (256 − TB) / 클럭
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;

        public Machine(byte[] mem)
        {
            Mem = mem;
            Cpu = new Cpu6502(this, Cpu6502.Variant.Nes2A03) { DecimalMode = true };
            Cpu.PC = Mem[0xFFFC] | (Mem[0xFFFD] << 8);
            Cpu.S = 0xFF;
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
            pending = true;
            Nmis++;
            Cpu.Interrupt(0xFFFA);
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
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (timerFlags != 0 && !Cpu.I) { Irqs++; Cpu.Interrupt(0xFFFE); }   // IRQ 줄 = YM2151 (레벨)
                if (Cpu.Jammed) { Cpu.Cycles = end; break; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a >= 0x4000) return Mem[a];
            if (a < 0x1000) return Mem[a];
            switch (a)
            {
                case 0x1800: case 0x1801: return (byte)timerFlags;   // YM2151 상태: 바쁘지 않음
                case 0x1810: LatchReads++; pending = false; return (byte)latch;
                case 0x1820: return (byte)(0x87 | (pending ? 0x08 : 0));   // 동전 없음 (낮음 = 눌림), bit3 = 명령 있음, bit4 = 응답 안 읽힘 (늘 읽힘), bit7 = 자가 시험 끔 (0이면 부팅이 RAM 시험 · 대기로 1초 넘게 걸려 첫 명령을 놓침 — Indiana Jones)
            }
            if (a >= 0x1000 && a < 0x1010) return 0;   // VIA (TMS5220) — 없음
            if (a == 0x187A) { rnd = (rnd >> 1) ^ (-(rnd & 1) & 0x12000); return (byte)rnd; }   // POKEY RANDOM (poly17 위 8비트를 흉내)
            if (a >= 0x1870 && a < 0x1880) return 0xFF;
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0x1000) { Mem[a] = v; return; }
            if (a >= 0x4000) return;
            switch (a)
            {
                case 0x1800: opmAddr = v; return;
                case 0x1801: OpmWrite(opmAddr, v); return;
                case 0x1810: return;   // 응답 (메인 CPU로)
                case 0x1824: case 0x1825: case 0x1826: case 0x1827: return;   // LED
            }
            if (a >= 0x1000 && a < 0x1010) { ViaWrites++; return; }
            if (a >= 0x1870 && a < 0x1880) { PokeyWrites++; PokeyWrite(a & 15, v); return; }
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
                        Emit(0, reg, v & 0x80);   // CSM 비트만
                        return;
                    }
            }
            Emit(0, reg, v);
        }

        private readonly int[] last = Enumerable.Repeat(-1, 0x200).ToArray();

        /// <summary>POKEY: 쓰기 레지스터 0~8 (소리) · 9 (STIMER) · Fh (SKCTL)만 VGM에, 나머지 (시리얼 · 키보드 · 팟)는 버림.</summary>
        private void PokeyWrite(int reg, int v)
        {
            if (reg <= 9 || reg == 0x0F) Emit(1, reg, v);
        }

        private void Emit(int chip, int reg, int val)
        {
            // 같은 값 다시 쓰기는 버린다 (타이머 14h를 IRQ마다 써서 VGM이 부푼다) — 키온 08h · LFO 리셋 01h · POKEY STIMER 9는 다시 쓰기도 뜻이 있어 둔다
            bool keep = chip == 0 ? reg == 0x08 || reg == 0x01 : reg == 0x09;
            if (!keep && last[chip << 8 | reg] == val) return;
            last[chip << 8 | reg] = val;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Atari System 1 (hoot)", Notes = "hoot atari system1" };
            w.SetClock(VgmWriter.ClockYm2151, OpmClock);
            bool pokey = Log.Any(x => x.chip == 1);
            if (pokey) w.SetClock(VgmWriter.ClockPokey, PokeyClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(x.chip == 0 ? (byte)0x54 : (byte)0xBB, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
