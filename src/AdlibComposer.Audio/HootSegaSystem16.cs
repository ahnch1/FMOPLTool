using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 세가 System 16A · 16B (driver sega, type system16a · system16b): 사운드 보드를 그대로 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (사운드 ROM을 읽어 맞춘 것, MAME segas16b sound_map · portmap과 같음): Z80 5MHz (16A 4MHz), 0000h~7FFFh = ROM, 8000h~DFFFh = ADPCM ROM 뱅크 창,
/// E800h 읽기 = 명령, F800h~FFFFh = RAM. 포트 00h~3Fh = YM2151 (짝수 주소 · 홀수 데이터), 40h~7Fh = 제어 (bit7 START, bit6 /RESET, 나머지 = 뱅크),
/// 80h~BFh 쓰기 = uPD7759 데이터 · 읽기 = bit7 바쁨 아님, C0h~FFh 읽기 = 명령.
/// 인터럽트: 명령 = IRQ (IM 1, HOLD — 16A는 NMI), uPD7759 DRQ = NMI (처리기가 HL'의 바이트를 OUT (C)로 칩에). YM2151 IRQ는 연결 안 됨 (드라이버가 상태를 폴링).</para>
/// <para>뱅크 (옵션 boardtype): 171-5358 = D2~D5 중 0인 비트가 ROM 고르기 (A8 · A9 · A10 · A11), D0 · D1 = 4000h 단위 — hoot는 ROM을 adpcm offset 간격으로 둔다.
/// 171-5521 · 5704 · 5797 = D3 × 20000h + (D0~D2) × 4000h. 곡: 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM2151 + uPD7759 (슬레이브 모드 바이트) VGM으로.</para>
/// </summary>
public static class HootSegaSystem16
{
    public const double YmClock = 4000000, UpdClock = 640000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        string d = (driver ?? "").Trim().ToLowerInvariant(), t = (type ?? "").Trim().ToLowerInvariant();
        return d == "sega" && (t == "system16a" || t == "system16b");
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
        var adpcm = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            var list = type == "code" ? code : type == "adpcm" ? adpcm : null;
            if (list == null) { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            list.Add((off, d));
        }
        var rom = new byte[0x8000];
        if (code.Count > 0) { var c = code.OrderBy(x => x.off).First().d; Array.Copy(c, rom, Math.Min(c.Length, rom.Length)); }
        else missing.Add("(code)");
        long size = adpcm.Count == 0 ? 0 : adpcm.Max(p => p.off + p.d.Length);
        var pcm = new byte[size];
        foreach (var (off, d) in adpcm) Array.Copy(d, 0, pcm, off, d.Length);
        // ROM 간격 (5358: ROM 하나의 자리)
        var offs = adpcm.Select(a => a.off).Distinct().OrderBy(x => x).ToList();
        long slot = offs.Count >= 2 ? offs[1] - offs[0] : adpcm.Count > 0 ? adpcm[0].d.Length : 0x8000;
        int board = e.Option("boardtype", 0);
        if (board == 0) board = slot >= 0x20000 ? 0x5521 : 0x5358;
        bool b16 = string.Equals(e.Type, "system16b", StringComparison.OrdinalIgnoreCase);
        var m = new Machine(rom, pcm, board, (int)Math.Max(0x4000, slot), b16 ? 5000000 : 4000000) { LatchNmi = !b16 };
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
        public readonly double CpuClock;
        public readonly int Board, Slot;
        public readonly Upd7759Core Upd = new Upd7759Core();
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151 (reg, val), 1 = uPD7759 (0 /RESET · 1 START · 2 데이터, val).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads, UpdBytes, UpdStarts;
        public Dictionary<int, int> PcCount;
        /// <summary>System 16A: 명령 = NMI (8255 PPI). 16B는 IRQ.</summary>
        public bool LatchNmi;

        private int latch, ymAddr, bankOffset, control = -1;
        private bool latchIrq;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private double updAcc;
        private long lastCycles;

        public Machine(byte[] rom, byte[] pcm, int board, int slot, double cpuClock)
        {
            Rom = rom; Pcm = pcm; Board = board; Slot = slot; CpuClock = cpuClock;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        /// <summary>마지막 START 뒤 uPD7759에 넣은 바이트 (곡 시작 때 칩이 이미 돌고 있으면 재생기 칩도 같은 자리에서 시작하게).</summary>
        private readonly List<int> sinceStart = new List<int>();

        public void Play(int code)
        {
            var init = Log.Where(x => x.chip == 0).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            if (Upd.Busy)
            {
                Log.Add((0.0, 1, 0, 1)); Log.Add((0.0, 1, 1, 0)); Log.Add((0.0, 1, 1, 1));
                foreach (var b in sinceStart) Log.Add((0.0, 1, 2, b));
            }
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        public void Send(int code)
        {
            latch = code;
            if (LatchNmi) Nmi(); else latchIrq = true;
        }

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
            lastCycles = Cpu.Cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                // uPD7759를 CPU 시간에 맞춰 돌리고, DRQ가 올라가면 NMI
                updAcc += (now - lastCycles) * UpdClock / CpuClock;
                lastCycles = now;
                int n = (int)updAcc;
                if (n > 0)
                {
                    updAcc -= n;
                    bool before = Upd.Drq;
                    Upd.Advance(n);
                    if (Upd.Drq && !before) Nmi();
                }
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (latchIrq && Cpu.Iff1)   // IRQ = 명령만 (YM2151 IRQ는 연결 안 됨 — 드라이버가 타이머 플래그를 폴링)
                {
                    latchIrq = false;   // HOLD_LINE
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted) { Cpu.Cycles += 4; continue; }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                Cpu.Step();
            }
        }

        private void Nmi()
        {
            Nmis++;
            Cpu.Halted = false;
            Cpu.Iff2 = Cpu.Iff1;
            Cpu.Iff1 = false;
            Push(Cpu.PC);
            Cpu.PC = 0x66;
            Cpu.Cycles += 11;
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return Rom[a];
            if (a < 0xE000) return Pcm.Length > 0 ? Pcm[(bankOffset + (a - 0x8000)) % Pcm.Length] : (byte)0xFF;
            if (a >= 0xF800) return Ram[a & 0x7FF];
            if (a >= 0xE800 && a < 0xF000) { LatchReads++; return (byte)latch; }
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xF800) Ram[a & 0x7FF] = v;
        }

        public byte In(int port)
        {
            int p = port & 0xFF;
            if (p < 0x40) return (byte)(timerFlags & 3);
            if (p >= 0x80 && p < 0xC0) return (byte)(Upd.Busy ? 0 : 0x80);   // bit7 = 바쁨 아님
            if (p >= 0xC0) { LatchReads++; return (byte)latch; }
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            int p = port & 0xFF;
            if (p < 0x40) { if ((p & 1) == 0) ymAddr = v; else YmWrite(ymAddr, v); return; }
            if (p < 0x80) { Control(v); return; }
            if (p < 0xC0) { Upd.Data(v); UpdBytes++; Emit(1, 2, v); if (sinceStart.Count < 1 << 20) sinceStart.Add(v); return; }
        }

        private void Control(int v)
        {
            int old = control;
            control = v;
            // START를 먼저, 그다음 /RESET (MAME: 같이 내려가면 시작하지 않게)
            if (old < 0 || ((old ^ v) & 0x80) != 0) { bool idle = !Upd.Busy; Upd.SetStart((v & 0x80) != 0); if (idle && Upd.Busy) { UpdStarts++; sinceStart.Clear(); } Emit(1, 1, (v >> 7) & 1); }
            if (old < 0 || ((old ^ v) & 0x40) != 0) { Upd.SetReset((v & 0x40) != 0); Emit(1, 0, (v >> 6) & 1); }
            if (Board == 0x5358)
            {
                int rom = (v & 0x04) == 0 ? 0 : (v & 0x08) == 0 ? 1 : (v & 0x10) == 0 ? 2 : (v & 0x20) == 0 ? 3 : 0;
                bankOffset = rom * Slot + (v & 3) * 0x4000;
            }
            else bankOffset = ((v & 0x08) >> 3) * 0x20000 + (v & 7) * 0x4000;
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
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
            Emit(0, reg, v);
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Sega System 16 (hoot)", Notes = "hoot sega system16" };
            w.SetClock(VgmWriter.ClockYm2151, YmClock);
            bool upd = Pcm.Length > 0;
            if (upd)
            {
                w.SetClock(VgmWriter.ClockUpd7759, UpdClock);   // bit31 0 = 슬레이브 모드
                w.Write(0xB6, 0, 1);   // /RESET 높음 (재생기 칩 처음 상태)
            }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !upd) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, x.reg, x.val);
                else w.Write(0xB6, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
