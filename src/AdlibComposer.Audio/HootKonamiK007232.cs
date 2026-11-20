using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 코나미 Z80 + K007232 보드 (driver konami): 사운드 CPU를 흉내 낸다. 배치는 type마다 <see cref="Layouts"/> (MAME 사운드 맵 + ROM 스캔).
/// <list type="bullet">
/// <item>scontra (Super Contra · Gang Busters · Crazy Cop) · thundercross (Thunder Cross · Block Hole): A000h 명령, B000h K007232, C000h YM2151, F000h 뱅크 (MAME thunderx)</item>
/// <item>salamander (Salamander · Life Force): 같은 배치, 뱅크 없음, D000h~F000h VLM5030 (빼고) (MAME nemesis salamand)</item>
/// <item>hcastle (Haunted Castle · City Bomber · Kitten Kaboodle): 9800h SCC (K051649), A000h YM3812 (IRQ → NMI), B000h K007232, C000h 뱅크, D000h 명령 (MAME hcastle)</item>
/// <item>twin16 (Devil World · Vulcan Venture · Final Round · Cue Brick · M.I.A.): scontra 배치 + uPD7759 마스터 모드 (9000h bit1 = /RESET, D000h = 번호, E000h bit0 = START, F000h 읽기 = 쉬는 중; ROM = pcm2) (MAME twin16)</item>
/// <item>battlantis (Battlantis · Rack 'em Up): K007232 없음, A000h · C000h = YM3812 × 2 (VGM에는 둘째 YM3812가 없어 YMF262 하나로 — #1 = 뱅크 0, #2 = 뱅크 1), E000h 명령 (MAME battlnts)</item>
/// <item>crimfght (Crime Fighters · Aliens): A000h YM2151 (CT 핀 = 뱅크), C000h 명령, E000h K007232, 음량 아래 니블 = 채널 0 (MAME crimfght · aliens)</item>
/// </list>
/// <para>공통: Z80 3.58MHz, 0000h~7FFFh ROM, 8000h~ RAM, 명령 = 래치 + IRQ (hold). K007232 바깥 포트 (reg 12) = 두 채널 음량 니블, 뱅크 = 채널마다 128KB.</para>
/// <para>곡: rom code = Z80 ROM, pcm = K007232 ROM (pcm2 = VLM5030 · uPD7759는 아직). 부팅 1초 뒤 명령, 180초를 YM2151 / YM3812 + SCC + K007232 (이 프로그램 안 명령 DBh) VGM으로.</para>
/// </summary>
public static class HootKonamiK007232
{
    public const double CpuClock = 3579545, OpmClock = 3579545, PcmClock = 3579545, SccClock = 3579545 / 2.0, UpdClock = 640000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    /// <summary>보드 배치. -1 = 없음. VolHi0 = 바깥 포트 위 니블이 채널 0 음량, BankCt = YM2151 CT 핀 (1Bh bit7 → 채널 0, bit6 → 1)이 뱅크, OplNmi = YM3812 IRQ가 NMI.</summary>
    public sealed class Layout
    {
        public int RamEnd = 0x8800, Latch = 0xA000, K = 0xB000, Opm = 0xC000, Opl = -1, Scc = -1, Bank = -1, VlmBusy = -1, Upd = -1, Opl2 = -1;
        public bool VolHi0 = true, BankCt, OplNmi;
    }

    public static readonly Dictionary<string, Layout> Layouts = new Dictionary<string, Layout>
    {
        ["scontra"] = new Layout { Bank = 0xF000 },
        ["thundercross"] = new Layout { Bank = 0xF000 },
        ["salamander"] = new Layout { RamEnd = 0x9000, VlmBusy = 0xE000 },
        ["hcastle"] = new Layout { Opm = -1, Opl = 0xA000, Scc = 0x9800, Bank = 0xC000, Latch = 0xD000, OplNmi = true },
        ["twin16"] = new Layout { RamEnd = 0x9000, Upd = 0x9000 },
        ["battlantis"] = new Layout { K = -0x100, Opm = -1, Opl = 0xA000, Opl2 = 0xC000, Latch = 0xE000 },
        ["crimfght"] = new Layout { Opm = 0xA000, Latch = 0xC000, K = 0xE000, VolHi0 = false, BankCt = true },
    };

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "konami", StringComparison.OrdinalIgnoreCase)) return false;
        return Layouts.ContainsKey((type ?? "").Trim().ToLowerInvariant());
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
        var rom = new byte[0x8000];
        var pcm = new List<(long off, byte[] d)>();
        var pcm2 = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code" && type != "pcm" && type != "pcm2") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "pcm") { pcm.Add((off, d)); continue; }
            if (type == "pcm2") { pcm2.Add((off, d)); continue; }
            if (off < 0 || off >= 0x8000) { unsupported.Add(type + " offset " + off); continue; }
            Array.Copy(d, 0, rom, off, (int)Math.Min(d.Length, 0x8000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var p = new byte[pcm.Count == 0 ? 0 : (int)pcm.Max(x => x.off + x.d.Length)];
        foreach (var (off, d) in pcm) Array.Copy(d, 0, p, off, d.Length);
        Layouts.TryGetValue((e.Type ?? "").Trim().ToLowerInvariant(), out var layout);
        var p2 = new byte[pcm2.Count == 0 ? 0 : (int)pcm2.Max(x => x.off + x.d.Length)];
        foreach (var (off, d) in pcm2) Array.Copy(d, 0, p2, off, d.Length);
        var m = new Machine(rom, p, layout) { UpdRom = p2 };
        m.Upd.Rom = p2;
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, Pcm;
        public readonly byte[] Ram = new byte[0x1000];
        public readonly Layout L;
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151, 1 = K007232 (reg 0~0Dh, 가짜 10h~15h), 2 = YM3812, 3 = SCC (reg = VGM 포트 &lt;&lt; 8 | 레지스터).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, Nmis, LatchReads;
        public bool UsedOpl, UsedScc, UsedUpd;
        /// <summary>uPD7759 (마스터 모드) — 바쁨 읽기용, 쓰기는 chip 4 (0 /RESET · 1 START · 2 번호).</summary>
        public readonly Upd7759Core Upd = new Upd7759Core();
        public byte[] UpdRom = new byte[0];
        private double updAcc;
        private long updLast;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch, ymAddr, ym2Addr;
        private bool irqLine, nmiPending;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();
        private readonly byte[] kreg = new byte[0x20];

        public Machine(byte[] rom, byte[] pcm, Layout layout = null)
        {
            Rom = rom; Pcm = pcm; L = layout ?? new Layout();
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
            irqLine = true;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        private bool Opl { get { return L.Opl >= 0; } }
        private long TimerAPeriod { get { return Math.Max(1, Opl ? (long)(80e-6 * (256 - timerA) * CpuClock) : (long)(64.0 * (1024 - timerA) / OpmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, Opl ? (long)(320e-6 * (256 - timerB) * CpuClock) : (long)(1024.0 * (256 - timerB) / OpmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (Opl)
                {
                    // YM3812: 4h bit6 · bit5 = 가림, 깃발 bit6 (T1) · bit5 (T2) · bit7 (IRQ, 0 → 1이면 NMI)
                    int before = timerFlags;
                    if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 0x40) == 0) timerFlags |= 0xC0; }
                    if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 0x20) == 0) timerFlags |= 0xA0; }
                    if ((before & 0x80) == 0 && (timerFlags & 0x80) != 0 && L.OplNmi) nmiPending = true;
                }
                else
                {
                    if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                    if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                }
                if (L.Upd >= 0 && Upd.Busy)
                {
                    updAcc += (now - updLast) * UpdClock / CpuClock;
                    int n = (int)updAcc;
                    if (n > 0) { updAcc -= n; Upd.Advance(n); }
                }
                updLast = now;
                if (nmiPending)
                {
                    nmiPending = false;
                    Nmis++;
                    Cpu.Halted = false;
                    Cpu.Iff2 = Cpu.Iff1; Cpu.Iff1 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x66;
                    Cpu.Cycles += 11;
                }
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
                if (Cpu.Halted && !irqLine)
                {
                    long next = Math.Min(nextTimerA, nextTimerB);
                    if (L.OplNmi && next < end) { Cpu.Cycles = Math.Max(Cpu.Cycles + 4, next); continue; }
                    Cpu.Cycles = end; break;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return Rom[a];
            if (a < L.RamEnd) return Ram[a - 0x8000];
            if (a == L.Latch) { LatchReads++; return (byte)latch; }
            if ((L.Opm >= 0 && (a == L.Opm || a == L.Opm + 1)) || (Opl && (a == L.Opl || a == L.Opl + 1))) return (byte)timerFlags;
            if (L.Opl2 >= 0 && (a == L.Opl2 || a == L.Opl2 + 1)) return 0;
            if (a >= L.K && a < L.K + 0x0E) { int r = a - L.K; if (r == 5 || r == 11) KWrite(r, kreg[r]); return 0; }   // 읽기 5 · 11 = 다시 시작
            if (L.Scc >= 0 && a >= L.Scc && a < L.Scc + 0x100) return 0xFF;
            if (a == L.VlmBusy) return 0;   // VLM5030 바쁨 아님
            if (L.Upd >= 0 && a == L.Upd + 0x6000) return (byte)(Upd.Busy ? 0 : 1);   // F000h: uPD7759 쉬는 중 = 1
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x8000 && a < L.RamEnd) { Ram[a - 0x8000] = v; return; }
            if (a < 0x8000) return;
            if (L.Opm >= 0 && a == L.Opm) { ymAddr = v; return; }
            if (L.Opm >= 0 && a == L.Opm + 1) { OpmWrite(ymAddr, v); return; }
            if (Opl && a == L.Opl) { ymAddr = v; return; }
            if (Opl && a == L.Opl + 1) { OplWrite(ymAddr, v); return; }
            if (L.Opl2 >= 0 && a == L.Opl2) { ym2Addr = v; return; }
            if (L.Opl2 >= 0 && a == L.Opl2 + 1) { if (ym2Addr < 2 || ym2Addr > 4) { UsedOpl = true; Emit(5, ym2Addr, v); } return; }
            if (a >= L.K && a < L.K + 0x0E) { KWrite(a - L.K, v); return; }
            if (a == L.Bank) { Emit(1, 0x14, v & 3); Emit(1, 0x15, (v >> 2) & 3); return; }   // 뱅크
            if (L.Scc >= 0 && a >= L.Scc && a < L.Scc + 0x100) { SccWrite(a - L.Scc, v); return; }
            if (L.Upd >= 0)
            {
                if (a == L.Upd) { Upd.SetReset((v & 2) != 0); Emit(4, 0, (v >> 1) & 1); return; }
                if (a == L.Upd + 0x4000) { Upd.Data(v); Emit(4, 2, v); return; }
                if (a == L.Upd + 0x5000) { Upd.SetStart((v & 1) != 0); if ((v & 1) != 0) UsedUpd = true; Emit(4, 1, v & 1); return; }
            }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        private void KWrite(int r, int v)
        {
            kreg[r] = (byte)v;
            if (r == 12)   // 바깥 포트 = 음량
            {
                int v0 = L.VolHi0 ? v >> 4 : v & 15, v1 = L.VolHi0 ? v & 15 : v >> 4;
                Emit(1, 0x10, v0 * 0x11); Emit(1, 0x11, 0); Emit(1, 0x12, 0); Emit(1, 0x13, v1 * 0x11);
                return;
            }
            Emit(1, r, v);
        }

        public byte In(int p) { return 0xFF; }
        public void Out(int p, byte v) { }

        private void OplWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x02: timerA = v; return;
                case 0x03: timerB = v; return;
                case 0x04:
                    {
                        if ((v & 0x80) != 0) { timerFlags = 0; return; }
                        int old = timerCtl;
                        timerCtl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) nextTimerA = Cpu.Cycles + TimerAPeriod; else if ((v & 1) == 0) nextTimerA = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextTimerB = Cpu.Cycles + TimerBPeriod; else if ((v & 2) == 0) nextTimerB = long.MaxValue;
                        return;
                    }
            }
            UsedOpl = true;
            Emit(2, reg, v);
        }

        private void SccWrite(int off, int v)
        {
            // K051649: 00h~7Fh 파형 (채널 3 · 4 같이), 80h~89h 주파수, 8Ah~8Eh 음량, 8Fh 켜기 → VGM D2h 포트 0~3
            int port, r;
            if (off < 0x80) { port = 0; r = off; }
            else
            {
                off &= 0x8F;
                if (off < 0x8A) { port = 1; r = off - 0x80; }
                else if (off < 0x8F) { port = 2; r = off - 0x8A; }
                else { port = 3; r = 0; }
            }
            UsedScc = true;
            Emit(3, port << 8 | r, v);
        }

        private void OpmWrite(int reg, int v)
        {
            if (reg == 0x1B && L.BankCt) { Emit(1, 0x14, (v >> 7) & 1); Emit(1, 0x15, (v >> 6) & 1); }
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
            if (chip == 0 && reg != 0x08 && reg != 0x01) { if (last[reg] == val) return; last[reg] = val; }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Konami K007232 (hoot)", Notes = "hoot konami k007232" };
            if (L.Opm >= 0) w.SetClock(VgmWriter.ClockYm2151, OpmClock);
            bool opl3 = UsedOpl && L.Opl2 >= 0;
            if (UsedOpl && !opl3) w.SetClock(VgmWriter.ClockYm3812, OpmClock);
            if (opl3) { w.SetClock(VgmWriter.ClockYmf262, OpmClock * 4); w.Write(0x5F, 0x05, 0x01); }   // NEW = 1
            var wse = new bool[2];
            if (UsedScc) w.SetClock(VgmWriter.ClockK051649, SccClock);
            bool upd = UsedUpd && UpdRom.Length > 0;
            if (upd)
            {
                w.SetClock(VgmWriter.ClockUpd7759, UpdClock, 0x80000000u);   // bit31 = 마스터 모드
                var cmd = new byte[7 + 8 + UpdRom.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x92;
                int size = 8 + UpdRom.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int len = UpdRom.Length;
                cmd[7] = (byte)len; cmd[8] = (byte)(len >> 8); cmd[9] = (byte)(len >> 16); cmd[10] = (byte)(len >> 24);
                Array.Copy(UpdRom, 0, cmd, 15, UpdRom.Length);
                w.Write(cmd);
            }
            bool pcm = Pcm.Length > 0 && Log.Any(x => x.chip == 1 && (x.reg == 5 || x.reg == 0x0B));
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !pcm) continue;
                if (x.chip == 4 && !upd) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, x.reg, x.val);
                else if ((x.chip == 2 || x.chip == 5) && opl3)
                {
                    // YM3812 → YMF262 뱅크: 01h bit5 = 파형 고름 허락 (OPL3에는 없음 → E0h~F5h를 가림), C0h~C8h에 왼 · 오 출력 bit
                    int c = x.chip == 2 ? 0 : 1, r = x.reg, ov = x.val;
                    if (r == 0x01) { wse[c] = (ov & 0x20) != 0; continue; }
                    if (r >= 0xE0 && r <= 0xF5) ov = wse[c] ? ov & 3 : 0;
                    if (r >= 0xC0 && r <= 0xC8) ov = (ov & 0x0F) | 0x30;
                    if (c == 1 && (r == 0xBD || r == 0x08)) continue;
                    w.Write((byte)(c == 0 ? 0x5E : 0x5F), r, ov);
                }
                else if (x.chip == 2) w.Write(0x5A, x.reg, x.val);
                else if (x.chip == 3) w.Write(new byte[] { 0xD2, (byte)(x.reg >> 8), (byte)x.reg, (byte)x.val });
                else if (x.chip == 4) w.Write(0xB6, x.reg, x.val);
                else w.Write(new byte[] { 0xDB, (byte)x.reg, (byte)x.val, 0 });
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (pcm) { v.K007232Clock = PcmClock; v.K007232Rom = Pcm; }
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
