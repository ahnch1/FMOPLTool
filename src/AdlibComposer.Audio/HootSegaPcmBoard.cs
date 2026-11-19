using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 세가 PCM 보드 (driver sega · bootleg, type outrun · shangon · toutrun · aburner · spmonaco · gforce · pdrift · hangon · sharrier · eropnx2):
/// 사운드 보드를 그대로 흉내 내고 Z80 사운드 ROM을 돌린다 (배치는 사운드 ROM을 읽어 확인).
/// <para>Out Run 계열 (Out Run · Super Hang-On · X 보드 · Y 보드): Z80 4MHz, ROM 0000h~EFFFh, 세가 PCM F000h~F7FFh (256바이트 거울), RAM F800h~FFFFh,
/// YM2151 포트 00h~3Fh (짝수 주소 · 홀수 데이터), 명령 포트 40h (보내면 NMI), YM2151 타이머 = IRQ (Y 보드가 씀).
/// Hang-On 계열 (Hang-On · Space Harrier · Enduro Racer): ROM 0000h~7FFFh, RAM C000h~C7FFh, YM2203 D000h (짝수 주소 · 홀수 데이터), 세가 PCM E000h~E0FFh, 명령 포트 40h (NMI), YM2203 타이머 = IRQ.</para>
/// <para>세가 PCM: Out Run 계열 4MHz (Y 보드는 뱅크 BANK_12M · 마스크 F8h, 그 밖 BANK_512), Hang-On 계열 8MHz (클럭은 MAME 기억 — ROM으로 확인 못 함).
/// Out Run의 32KB PCM ROM은 MAME처럼 64KB 뱅크마다 하나씩 (거울). 180초를 YM2151 / YM2203 + 세가 PCM VGM으로.</para>
/// </summary>
public static class HootSegaPcmBoard
{
    public const double CpuClock = 4000000, FmClock = 4000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;
    private static readonly string[] OutRunTypes = { "outrun", "shangon", "toutrun", "aburner", "spmonaco", "gforce", "pdrift" };
    private static readonly string[] HangOnTypes = { "hangon", "sharrier", "eropnx2" };

    public static bool Handles(string driver, string type)
    {
        string d = (driver ?? "").Trim().ToLowerInvariant(), t = (type ?? "").Trim().ToLowerInvariant();
        return (d == "sega" || d == "bootleg") && (OutRunTypes.Contains(t) || HangOnTypes.Contains(t));
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
        string t = (e.Type ?? "").Trim().ToLowerInvariant();
        bool hangOn = HangOnTypes.Contains(t);
        var code = new List<(long off, byte[] d)>();
        var pcm = new List<(long off, string name, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code" && type != "pcm") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "code") code.Add((off, d)); else pcm.Add((off, text, d));
        }
        var rom = new byte[0x10000];
        if (code.Count > 0)
        {
            long min = code.Min(c => c.off);
            foreach (var (off, d) in code) { long at = off - min; if (at < rom.Length) Array.Copy(d, 0, rom, at, (int)Math.Min(d.Length, rom.Length - at)); }
        }
        else missing.Add("(code)");
        byte[] pcmRom;
        if (t == "outrun" && pcm.Count > 0 && pcm.All(p => p.d.Length < 0x10000))
        {
            // MAME: 32KB ROM 하나가 64KB 뱅크 하나 (ROM_RELOAD로 거울) — hoot 목록의 같은 이름 되풀이는 뺀다
            var distinct = new List<byte[]>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in pcm.OrderBy(p => p.off)) if (seen.Add(p.name)) distinct.Add(p.d);
            pcmRom = new byte[distinct.Count * 0x10000];
            for (int i = 0; i < distinct.Count; i++)
                for (int k = 0; k < 0x10000; k += distinct[i].Length) Array.Copy(distinct[i], 0, pcmRom, i * 0x10000 + k, Math.Min(distinct[i].Length, 0x10000 - k));
        }
        else
        {
            long size = pcm.Count == 0 ? 0 : pcm.Max(p => p.off + p.d.Length);
            pcmRom = new byte[size];
            foreach (var (off, _, d) in pcm) Array.Copy(d, 0, pcmRom, off, d.Length);
        }
        int intf = t == "gforce" || t == "pdrift" ? SegaPcm.Bank12M : SegaPcm.Bank512;
        var m = new Machine(rom, pcmRom, hangOn, intf, hangOn ? 8000000 : 4000000);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, Pcm;
        public readonly bool HangOn;
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly SegaPcm Chip;
        public readonly double PcmClock;
        public readonly int Interface;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151 · YM2203 (reg, val), 1 = 세가 PCM (주소, val).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads;
        public Dictionary<int, int> PcCount;

        private int latch, fmAddr;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private double pcmAcc;
        private long lastCycles;

        public Machine(byte[] rom, byte[] pcm, bool hangOn, int intf, double pcmClock)
        {
            Rom = rom; Pcm = pcm; HangOn = hangOn; Interface = intf; PcmClock = pcmClock;
            Chip = new SegaPcm(pcmClock, intf);
            if (pcm.Length > 0) Chip.LoadRom(pcm, 0);
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        public void Play(int code)
        {
            // 곡 시작 앞의 FM 설정은 남기고, 세가 PCM은 지금 레지스터를 통째로 0초에
            var init = Log.Where(x => x.chip == 0).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            for (int i = 0; i < 0x100; i++) Log.Add((0.0, 1, i, Chip.Ram[i]));
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        public void Send(int code)
        {
            latch = code;
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

        // YM2151: A = 64 × (1024 − TA), B = 1024 × (256 − TB) / 클럭. YM2203 (÷6): A = 72 × (1024 − TA), B = 1152 × (256 − TB)
        private long TimerAPeriod { get { return Math.Max(1, (long)((HangOn ? 72.0 : 64.0) * (1024 - timerA) / FmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)((HangOn ? 1152.0 : 1024.0) * (256 - timerB) / FmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            lastCycles = Cpu.Cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                pcmAcc += (now - lastCycles) * Chip.ChipRate / CpuClock;
                lastCycles = now;
                while (pcmAcc >= 1.0) { pcmAcc -= 1.0; Chip.ChipSample(out _, out _); }
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (timerFlags != 0 && Cpu.Iff1)
                {
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

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (HangOn)
            {
                if (a < 0x8000) return Rom[a];
                if (a >= 0xC000 && a < 0xC800) return Ram[a - 0xC000];
                if (a >= 0xD000 && a < 0xE000) return (byte)(timerFlags & 3);   // YM2203 상태
                if (a >= 0xE000 && a < 0xF000) return Chip.Ram[a & 0xFF];
                return 0xFF;
            }
            if (a < 0xF000) return Rom[a];
            if (a < 0xF800) return Chip.Ram[a & 0xFF];
            return Ram[a & 0x7FF];
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (HangOn)
            {
                if (a >= 0xC000 && a < 0xC800) { Ram[a - 0xC000] = v; return; }
                if (a >= 0xD000 && a < 0xE000) { if ((a & 1) == 0) fmAddr = v; else FmWrite(fmAddr, v); return; }
                if (a >= 0xE000 && a < 0xF000) { PcmWrite(a & 0xFF, v); return; }
                return;
            }
            if (a >= 0xF000 && a < 0xF800) { PcmWrite(a & 0xFF, v); return; }
            if (a >= 0xF800) Ram[a & 0x7FF] = v;
        }

        private void PcmWrite(int reg, byte v)
        {
            Chip.Write(0, reg, v);
            Emit(1, reg, v);
        }

        public byte In(int port)
        {
            int p = port & 0xFF;
            if (!HangOn && p < 0x40) return (byte)(timerFlags & 3);
            if (p >= 0x40 && p < 0x80) { LatchReads++; return (byte)latch; }
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            int p = port & 0xFF;
            if (!HangOn && p < 0x40) { if ((p & 1) == 0) fmAddr = v; else FmWrite(fmAddr, v); }
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        private void FmWrite(int reg, int v)
        {
            if (HangOn)
            {
                switch (reg)
                {
                    case 0x24: timerA = (timerA & 3) | (v << 2); return;
                    case 0x25: timerA = (timerA & 0x3FC) | (v & 3); return;
                    case 0x26: timerB = v; return;
                    case 0x27: Timers(v & 0x0F, (v & 0x10) != 0, (v & 0x20) != 0); Emit(0, reg, v & 0xC0); return;
                }
            }
            else
            {
                switch (reg)
                {
                    case 0x10: timerA = (timerA & 3) | (v << 2); return;
                    case 0x11: timerA = (timerA & 0x3FC) | (v & 3); return;
                    case 0x12: timerB = v; return;
                    case 0x14: Timers(v & 0x0F, (v & 0x10) != 0, (v & 0x20) != 0); return;
                }
            }
            Emit(0, reg, v);
        }

        private void Timers(int ctl, bool resetA, bool resetB)
        {
            int old = timerCtl;
            timerCtl = ctl;
            if (resetA) timerFlags &= ~1;
            if (resetB) timerFlags &= ~2;
            if ((ctl & 1) != 0 && (old & 1) == 0) nextTimerA = Cpu.Cycles + TimerAPeriod; else if ((ctl & 1) == 0) nextTimerA = long.MaxValue;
            if ((ctl & 2) != 0 && (old & 2) == 0) nextTimerB = Cpu.Cycles + TimerBPeriod; else if ((ctl & 2) == 0) nextTimerB = long.MaxValue;
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Sega PCM board (hoot)", Notes = "hoot sega pcm board" };
            if (HangOn) w.SetClock(VgmWriter.ClockYm2203, FmClock); else w.SetClock(VgmWriter.ClockYm2151, FmClock);
            w.SetClock(VgmWriter.ClockSegaPcm, PcmClock);
            w.SetClock(VgmWriter.SegaPcmInterface, Interface);
            if (Pcm.Length > 0)
            {
                var cmd = new byte[7 + 8 + Pcm.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x80;
                int size = 8 + Pcm.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int total = Pcm.Length;
                cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                Array.Copy(Pcm, 0, cmd, 15, Pcm.Length);
                w.Write(cmd);
            }
            double total2 = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(HangOn ? (byte)0x55 : (byte)0x54, x.reg, x.val);
                else w.Write(0xC0, (byte)x.reg, (byte)0, (byte)x.val);
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
