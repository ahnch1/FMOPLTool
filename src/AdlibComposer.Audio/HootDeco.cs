using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 데이터 이스트 HuC6280 사운드 보드 (driver dataeast, type thndzone · thunderzone · supbtime · deco32 · wolffang · generic):
/// 사운드 보드를 그대로 흉내 내고 HuC6280 사운드 ROM을 돌린다.
/// <para>기계 (MAME cninja · darkseal · supbtime · deco32 sound_map, 21비트 실제 주소): HuC6280 4.03MHz (32.22MHz / 8), 000000h~00FFFFh = ROM,
/// 100000h = YM2203 (있는 판), 110000h = YM2151, 120000h = OKIM6295 #1 (1MHz), 130000h = #2 (2MHz), 140000h 읽기 = 메인 CPU의 명령 (보내면 IRQ1, 읽으면 내림),
/// 1F0000h~1F1FFFh = RAM, 1FEC00h = 내부 타이머, 1FF402h = 인터럽트 막기, 1FF403h = 대기 · 타이머 확인.
/// YM2151 타이머 = IRQ2 (FFF6h), 명령 = IRQ1 (FFF8h), 내부 타이머 = FFFAh. YM2151 CT 출력 = OKI 뱅크 (deco32: bit0 → #1 · bit1 → #2, 그 밖: bit0 → #2).</para>
/// <para>Midnight Resistance (type midres, MAME dec0 midres_s_map): YM3812 108000h (3MHz, 타이머 = IRQ2), YM2203 118000h (1.5MHz), OKIM6295 130000h (1MHz),
/// 명령 138000h (IRQ1 — ROM의 NMI 벡터도 같은 처리기), YM2151 없음. 칩 자리는 ROM이 TAM으로 MPR2에 넣는 쪽 번호로 확인 (84h · 8Ch · 98h · 9Ch).</para>
/// <para>곡: rom code = 사운드 ROM, pcm1 · pcm2 = OKI ROM. 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM2151 (+ YM2203) + OKIM6295 둘 VGM으로.</para>
/// </summary>
public static class HootDeco
{
    public const double CpuClock = 32220000.0 / 8, YmClock = 32220000.0 / 9, OpnClock = 32220000.0 / 8;
    public const double Oki1Clock = 32220000.0 / 32, Oki2Clock = 32220000.0 / 16;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static readonly string[] Types = { "thndzone", "thunderzone", "supbtime", "deco32", "wolffang", "generic", "midres" };
    /// <summary>Midnight Resistance 배치의 칩 클럭.</summary>
    public const double MidresOplClock = 3000000, MidresOpnClock = 1500000, MidresOkiClock = 1000000;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "dataeast", StringComparison.OrdinalIgnoreCase) && Types.Contains((type ?? "").Trim().ToLowerInvariant());
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
        var p1 = new List<(long off, byte[] d)>();
        var p2 = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            var list = type == "code" ? code : type == "pcm1" || type == "pcm" ? p1 : type == "pcm2" ? p2 : null;
            if (list == null) { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            list.Add((off, d));
        }
        if (code.Count == 0) missing.Add("(code)");
        byte[] Join(List<(long off, byte[] d)> parts, int min)
        {
            long size = Math.Max(min, parts.Count == 0 ? 0 : parts.Max(p => p.off + p.d.Length));
            var r = new byte[size];
            foreach (var (off, d) in parts) Array.Copy(d, 0, r, off, d.Length);
            return r;
        }
        bool midres = string.Equals(e.Type, "midres", StringComparison.OrdinalIgnoreCase);
        var m = new Machine(Join(code, 0x10000), Join(p1, 0), Join(p2, 0), string.Equals(e.Type, "deco32", StringComparison.OrdinalIgnoreCase), midres);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6502Bus
    {
        public readonly byte[] Rom, Pcm1, Pcm2;
        public readonly bool Deco32, Midres;
        public readonly byte[] Ram = new byte[0x2000];
        public readonly Cpu6502 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151, 1 = YM2203, 2 = OKI #1, 3 = OKI #2 (OKI는 VGM 레지스터, 0 = 명령), 4 = YM3812 (midres).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int LatchIrqs, YmIrqs, TimerIrqs, LatchReads;
        public readonly HootOkiVoices Oki1, Oki2;
        public Dictionary<int, int> PcCount;

        private int latch, ymAddr, opnAddr;
        private bool latchPending;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        // HuC6280 내부
        private int irqMask, hucTimerLoad = 1;
        private bool hucTimerOn, hucTimerPending;
        private long nextHucTimer = long.MaxValue;
        private bool usesOpn;
        // YM3812 (midres): 주소 · 타이머 (1 = 288 클럭 × (256 − T1), 2 = 그 4배), 바뀐 레지스터만 VGM에
        private int oplAddr, opl1, opl2, oplCtl, oplFlags;
        private long nextOpl1 = long.MaxValue, nextOpl2 = long.MaxValue;
        private readonly Dictionary<int, int> oplRegs = new Dictionary<int, int>();

        public Machine(byte[] rom, byte[] pcm1, byte[] pcm2, bool deco32, bool midres = false)
        {
            Rom = rom; Pcm1 = pcm1; Pcm2 = pcm2; Deco32 = deco32; Midres = midres;
            Oki1 = new HootOkiVoices(pcm1, midres ? MidresOkiClock : Oki1Clock, CpuClock);
            Oki2 = new HootOkiVoices(pcm2, Oki2Clock, CpuClock);
            Cpu = new Cpu6502(this, Cpu6502.Variant.HuC6280);
            Cpu.Mpr[7] = 0;   // 리셋: MPR7 = 0 → FFFEh = ROM 1FFEh
            Cpu.PC = Cpu.Read(0xFFFE) | (Cpu.Read(0xFFFF) << 8);
            Cpu.I = true;
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

        public void Send(int code) { latch = code; latchPending = true; }

        private long TimerAPeriod { get { return Math.Max(1, (long)(64.0 * (1024 - timerA) / YmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(1024.0 * (256 - timerB) / YmClock * CpuClock)); } }
        private long Opl1Period { get { return Math.Max(1, (long)(288.0 * (256 - opl1) / MidresOplClock * CpuClock)); } }
        private long Opl2Period { get { return Math.Max(1, (long)(1152.0 * (256 - opl2) / MidresOplClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            int guard = 0;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (now >= nextOpl1) { nextOpl1 += Opl1Period; if ((oplCtl & 0x40) == 0) oplFlags |= 0x40; }
                if (now >= nextOpl2) { nextOpl2 += Opl2Period; if ((oplCtl & 0x20) == 0) oplFlags |= 0x20; }
                if (hucTimerOn && now >= nextHucTimer) { hucTimerPending = true; nextHucTimer += hucTimerLoad * 1024L; }
                if (!Cpu.I)
                {
                    // 우선: 내부 타이머 · IRQ1 (명령) · IRQ2 (YM2151) — MAME h6280 CHECK_IRQ_LINES
                    if (hucTimerPending && (irqMask & 4) == 0) { TimerIrqs++; Cpu.Interrupt(0xFFFA); continue; }
                    if (latchPending && (irqMask & 2) == 0) { LatchIrqs++; Cpu.Interrupt(0xFFF8); continue; }
                    if ((timerFlags != 0 || oplFlags != 0) && (irqMask & 1) == 0) { YmIrqs++; Cpu.Interrupt(0xFFF6); continue; }
                }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                Cpu.Step();
                if (Cpu.Jammed && ++guard > 4) break;
            }
            if (Cpu.Cycles < end) Cpu.Cycles = end;
        }

        public byte Read(int a)
        {
            a &= 0x1FFFFF;
            if (a < 0x100000) return a < Rom.Length ? Rom[a] : (byte)0xFF;
            if (a >= 0x1F0000 && a < 0x1F2000) return Ram[a - 0x1F0000];
            if (Midres)
            {
                switch (a >> 15)
                {
                    case 0x21: return (byte)(oplFlags != 0 ? 0x80 | oplFlags : 0);   // 108000h YM3812 상태
                    case 0x23: return 0;                                             // 118000h YM2203 상태
                    case 0x26: return Oki1.Status(Cpu.Cycles);                       // 130000h OKI
                    case 0x27: LatchReads++; latchPending = false; return (byte)latch;   // 138000h 명령
                }
                if (a < 0x1F0000) return 0;
            }
            else switch (a >> 16)
            {
                case 0x10: return 0;                                     // YM2203 상태 (바쁨 · 타이머 없음)
                case 0x11: return (byte)(timerFlags & 3);                // YM2151 상태
                case 0x12: return Oki1.Status(Cpu.Cycles);
                case 0x13: return Oki2.Status(Cpu.Cycles);
                case 0x14: LatchReads++; latchPending = false; return (byte)latch;
            }
            if (a == 0x1FEC00 || a == 0x1FEC01) return (byte)(hucTimerOn ? Math.Max(0, (nextHucTimer - Cpu.Cycles) / 1024) & 0x7F : (hucTimerLoad - 1) & 0x7F);
            if (a == 0x1FF402) return (byte)irqMask;
            if (a == 0x1FF403) return (byte)((hucTimerPending ? 4 : 0) | (latchPending ? 2 : 0) | (timerFlags != 0 ? 1 : 0));
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0x1FFFFF;
            if (a >= 0x1F0000 && a < 0x1F2000) { Ram[a - 0x1F0000] = v; return; }
            if (Midres)
            {
                switch (a >> 15)
                {
                    case 0x21: if ((a & 1) == 0) oplAddr = v; else OplWrite(oplAddr, v); return;
                    case 0x23: if ((a & 1) == 0) opnAddr = v; else { usesOpn = true; Emit(1, opnAddr, v); } return;
                    case 0x26: Oki1.Write(v, Cpu.Cycles); Emit(2, 0, v); return;
                }
            }
            else switch (a >> 16)
            {
                case 0x10: if ((a & 1) == 0) opnAddr = v; else { usesOpn = true; Emit(1, opnAddr, v); } return;
                case 0x11: if ((a & 1) == 0) ymAddr = v; else YmWrite(ymAddr, v); return;
                case 0x12: Oki1.Write(v, Cpu.Cycles); Emit(2, 0, v); return;
                case 0x13: Oki2.Write(v, Cpu.Cycles); Emit(3, 0, v); return;
            }
            if (a == 0x1FEC00) { hucTimerLoad = (v & 0x7F) + 1; return; }
            if (a == 0x1FEC01)
            {
                bool on = (v & 1) != 0;
                if (on && !hucTimerOn) nextHucTimer = Cpu.Cycles + hucTimerLoad * 1024L;
                if (!on) nextHucTimer = long.MaxValue;
                hucTimerOn = on;
                return;
            }
            if (a == 0x1FF402) { irqMask = v & 7; return; }
            if (a == 0x1FF403) { hucTimerPending = false; return; }
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        private void OplWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x02: opl1 = v; return;
                case 0x03: opl2 = v; return;
                case 0x04:
                    if ((v & 0x80) != 0) { oplFlags = 0; return; }   // IRQ 리셋
                    {
                        int old = oplCtl;
                        oplCtl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) nextOpl1 = Cpu.Cycles + Opl1Period; else if ((v & 1) == 0) nextOpl1 = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextOpl2 = Cpu.Cycles + Opl2Period; else if ((v & 2) == 0) nextOpl2 = long.MaxValue;
                        oplFlags &= ~(v & 0x60);
                    }
                    return;
            }
            if (oplRegs.TryGetValue(reg, out int prev) && prev == v) return;   // OPL은 같은 값 다시 쓰기가 아무 일 없음
            oplRegs[reg] = v;
            Emit(4, reg, v);
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
                case 0x1B:
                    {
                        // CT1 · CT2 출력 = OKI 뱅크
                        int ct = v >> 6;
                        if (Deco32)
                        {
                            if (Oki1.Bank != (ct & 1)) { Oki1.Bank = ct & 1; Emit(2, 0x0F, Oki1.Bank); }
                            if (Oki2.Bank != ((ct >> 1) & 1)) { Oki2.Bank = (ct >> 1) & 1; Emit(3, 0x0F, Oki2.Bank); }
                        }
                        else if (Oki2.Bank != (ct & 1)) { Oki2.Bank = ct & 1; Emit(3, 0x0F, Oki2.Bank); }
                        break;
                    }
            }
            Emit(0, reg, v);
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Data East (hoot)", Notes = "hoot dataeast" };
            if (Midres) w.SetClock(VgmWriter.ClockYm3812, MidresOplClock); else w.SetClock(VgmWriter.ClockYm2151, YmClock);
            if (usesOpn) w.SetClock(VgmWriter.ClockYm2203, Midres ? MidresOpnClock : OpnClock);
            bool o1 = Pcm1.Length > 0, o2 = Pcm2.Length > 0;
            if (o1 || o2) w.SetClock(VgmWriter.ClockOkim6295, Midres ? MidresOkiClock : Oki2Clock, 0x80000000u | (o1 && o2 ? 0x40000000u : 0));
            void Block(byte[] data, bool second)
            {
                var cmd = new byte[7 + 8 + data.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8B;
                uint size = (uint)(8 + data.Length) | (second ? 0x80000000u : 0);
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int total = data.Length;
                cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                Array.Copy(data, 0, cmd, 15, data.Length);
                w.Write(cmd);
            }
            // OKI가 하나뿐이면 그 ROM을 첫째 칩에
            if (o1) Block(Pcm1, false);
            if (o2) Block(Pcm2, o1);
            double total2 = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                switch (x.chip)
                {
                    case 0: w.Write(0x54, x.reg, x.val); break;
                    case 1: w.Write(0x55, x.reg, x.val); break;
                    case 2: if (o1) w.Write(0xB8, x.reg, x.val); break;
                    case 3: if (o2) w.Write(0xB8, o1 ? x.reg | 0x80 : x.reg, x.val); break;
                    case 4: w.Write(0x5A, x.reg, x.val); break;
                }
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            if (o1 && o2) { v.Okim6295Clock = Oki1Clock; v.Okim6295Clock2 = Oki2Clock; }
            else if (o1) v.Okim6295Clock = Midres ? MidresOkiClock : Oki1Clock;
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
