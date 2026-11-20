using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 코나미 Z80 + YM2151 (+ K007232) 보드 (driver konami, type scontra: Super Contra · Gang Busters (garuka) · Crazy Cop (Thunder Cross 계열),
/// type thundercross: Thunder Cross · Block Hole): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME thunderx scontra_sound_map / thunderx_sound_map + ROM 스캔): Z80 3.58MHz, 0000h~7FFFh ROM, 8000h~87FFh RAM, A000h = 명령 (메인이 쓰면 IRQ, hold),
/// B000h~B00Dh = K007232 (12 = 바깥 포트: 위 니블 = 채널 0 왼쪽 음량, 아래 = 채널 1 오른쪽 음량), C000h · C001h = YM2151 (3.58MHz), F000h = K007232 뱅크 (bit0~1 = 채널 0, bit2~3 = 채널 1, 128KB 단위).</para>
/// <para>곡: rom code = Z80 ROM, pcm = K007232 ROM. 부팅 1초 뒤 명령, 180초를 YM2151 + K007232 (이 프로그램 안 명령 DBh) VGM으로.</para>
/// </summary>
public static class HootKonamiK007232
{
    public const double CpuClock = 3579545, OpmClock = 3579545, PcmClock = 3579545;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "konami", StringComparison.OrdinalIgnoreCase)) return false;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "scontra" || t == "thundercross";
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
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code" && type != "pcm") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "pcm") { pcm.Add((off, d)); continue; }
            if (off < 0 || off >= 0x8000) { unsupported.Add(type + " offset " + off); continue; }
            Array.Copy(d, 0, rom, off, (int)Math.Min(d.Length, 0x8000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var p = new byte[pcm.Count == 0 ? 0 : (int)pcm.Max(x => x.off + x.d.Length)];
        foreach (var (off, d) in pcm) Array.Copy(d, 0, p, off, d.Length);
        var m = new Machine(rom, p);
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
        /// <summary>칩 쓰기: chip 0 = YM2151, 1 = K007232 (reg 0~0Dh, 가짜 10h~15h).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch, opmAddr;
        private bool irqLine;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();
        private readonly byte[] kreg = new byte[0x20];

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
            irqLine = true;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
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
                if (Cpu.Halted && !irqLine) { Cpu.Cycles = end; break; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return Rom[a];
            if (a < 0x8800) return Ram[a - 0x8000];
            if (a == 0xA000) { LatchReads++; return (byte)latch; }
            if (a == 0xC000 || a == 0xC001) return (byte)timerFlags;
            if (a >= 0xB000 && a < 0xB00E) { if (a == 0xB005 || a == 0xB00B) KWrite(a - 0xB000, kreg[a - 0xB000]); return 0; }   // 읽기 5 · 11 = 다시 시작
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x8000 && a < 0x8800) { Ram[a - 0x8000] = v; return; }
            if (a < 0x8000) return;
            if (a == 0xC000) { opmAddr = v; return; }
            if (a == 0xC001) { OpmWrite(opmAddr, v); return; }
            if (a >= 0xB000 && a < 0xB00E) { KWrite(a - 0xB000, v); return; }
            if (a == 0xF000) { Emit(1, 0x14, v & 3); Emit(1, 0x15, (v >> 2) & 3); return; }   // 뱅크
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        private void KWrite(int r, int v)
        {
            kreg[r] = (byte)v;
            if (r == 12) { Emit(1, 0x10, (v >> 4) * 0x11); Emit(1, 0x11, 0); Emit(1, 0x12, 0); Emit(1, 0x13, (v & 15) * 0x11); return; }   // 바깥 포트 = 음량
            Emit(1, r, v);
        }

        public byte In(int p) { return 0xFF; }
        public void Out(int p, byte v) { }

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
            if (chip == 0 && reg != 0x08 && reg != 0x01) { if (last[reg] == val) return; last[reg] = val; }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Konami K007232 (hoot)", Notes = "hoot konami k007232" };
            w.SetClock(VgmWriter.ClockYm2151, OpmClock);
            bool pcm = Pcm.Length > 0 && Log.Any(x => x.chip == 1 && (x.reg == 5 || x.reg == 0x0B));
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !pcm) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, x.reg, x.val);
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
