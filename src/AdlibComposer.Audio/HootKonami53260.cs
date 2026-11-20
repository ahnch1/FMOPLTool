using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 코나미 Z80 + YM2151 + K053260 보드 (driver konami, type 053260: Lightning Fighters · Parodius · Escape Kids · Thunder Cross II · Vendetta · Punk Shot · Sunset Riders · Bucky? · TMNT 2 · Quiz Gakumon · Simpsons): 사운드 CPU를 흉내 낸다.
/// <para>배치는 hoot 옵션: ram_addr (기본 F000h) · ram_size (800h), opm_addr (F800h), pcm_addr (K053260, FC00h), bank_addr (Simpsons — 8000h~BFFFh 뱅크). Z80 3.58MHz, YM2151 3.58MHz.
/// 명령 = K053260 포트 0 (메인이 씀, 사운드 CPU가 pcm_addr + 0 읽기) + IRQ (IM 1). 박자는 YM2151 타이머 상태 폴링 (상태 = opm_addr + 1),
/// 또는 NMI: K053260이 FC00h면 FA00h (FA00h면 FC00h)에 쓰면 무장 → K053260 SH1 끝 (클럭 ÷ 64)에서 NMI 한 번 (MAME z80_arm_nmi_w — 처리기가 다시 무장). opm_mirror = YM2151 거울 비트.</para>
/// <para>곡: 부팅 1초 뒤 명령, 180초를 YM2151 + K053260 VGM으로 (K053260 ROM = rom pcm, VGM 블록 8Eh).</para>
/// </summary>
public static class HootKonami53260
{
    public const double CpuClock = 3579545, OpmClock = 3579545, PcmClock = 3579545;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "konami", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "053260", StringComparison.OrdinalIgnoreCase);
    }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    private static int Option(HootArchive.Entry e, string name, int fallback)
    {
        if (!e.Options.TryGetValue(name, out var v)) return fallback;
        v = v.Trim();
        try { return v.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(v.Substring(2), NumberStyles.HexNumber) : int.Parse(v); }
        catch (FormatException) { return fallback; }
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        var code = new List<(long off, byte[] d)>();
        var pcm = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            var list = type == "code" ? code : type == "pcm" ? pcm : null;
            if (list == null) { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            list.Add((off, d));
        }
        if (code.Count == 0) missing.Add("(code)");
        var rom = new byte[Math.Max(0x10000, code.Count == 0 ? 0 : (int)code.Max(c => c.off + c.d.Length))];
        foreach (var (off, d) in code) Array.Copy(d, 0, rom, off, d.Length);
        var p = new byte[pcm.Count == 0 ? 0 : (int)pcm.Max(x => x.off + x.d.Length)];
        foreach (var (off, d) in pcm) Array.Copy(d, 0, p, off, d.Length);
        var m = new Machine(rom, p, Option(e, "ram_addr", 0xF000), Option(e, "ram_size", 0x800), Option(e, "opm_addr", 0xF800), Option(e, "pcm_addr", 0xFC00), Option(e, "bank_addr", -1));
        m.OpmMirror = Option(e, "opm_mirror", 0);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        // 부팅: IRQ가 켜질 때까지 (최대 8초) — Parodius는 K053260 ROM 읽기 포트로 PCM 롬 전체를 검사해 몇 초 걸림
        m.RunSeconds(1);
        for (int i = 0; i < 28 && !m.Cpu.Iff1; i++) m.RunSeconds(0.25);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, Pcm, Ram;
        public readonly int RamAddr, OpmAddr, PcmAddr, BankAddr;
        /// <summary>YM2151 거울 비트 (hoot 옵션 opm_mirror, Thunder Cross II = 0011h: F800h · F801h · F810h · F811h).</summary>
        public int OpmMirror;
        /// <summary>NMI 무장 자리: K053260이 FC00h면 FA00h, FA00h면 FC00h (MAME z80_arm_nmi_w), 그 밖은 없음.</summary>
        public int NmiArm { get { return PcmAddr == 0xFC00 ? 0xFA00 : PcmAddr == 0xFA00 ? 0xFC00 : -1; } }
        public int Nmis;
        private long nmiAt = long.MaxValue;
        public readonly Z80 Cpu;
        public readonly K053260 Chip = new K053260();
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151, 1 = K053260 (reg 08h~2Fh).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private readonly byte[] port = new byte[4];
        private readonly int[] pcmStart = new int[3];
        private long romRead;
        private int opmAddr, bank;
        private bool irqLine;
        private long playStart = -1;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();

        public Machine(byte[] rom, byte[] pcm, int ramAddr, int ramSize, int opmAddr, int pcmAddr, int bankAddr)
        {
            Rom = rom; Pcm = pcm; RamAddr = ramAddr; Ram = new byte[Math.Max(0x100, ramSize)]; OpmAddr = opmAddr; PcmAddr = pcmAddr; BankAddr = bankAddr;
            Chip.LoadRom(pcm, 0);
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            port[0] = (byte)code;
            port[1] = (byte)(code >> 8);
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
                if (now >= nmiAt)
                {
                    nmiAt = long.MaxValue;
                    Nmis++;
                    Cpu.Halted = false;
                    Cpu.Iff2 = Cpu.Iff1;
                    Cpu.Iff1 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x66;
                    Cpu.Cycles += 11;
                }
                if (irqLine && Cpu.Iff1)
                {
                    irqLine = false;   // hold
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted) { Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, Math.Min(nmiAt, Math.Min(nextTimerA, nextTimerB)))); continue; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a >= RamAddr && a < RamAddr + Ram.Length) return Ram[a - RamAddr];
            if ((a & ~OpmMirror & 0xFFFE) == (OpmAddr & 0xFFFE)) return (byte)timerFlags;
            if (a >= PcmAddr && a < PcmAddr + 0x30)
            {
                int r = a - PcmAddr;
                if (r < 2) { LatchReads++; return port[r]; }
                if (r < 4) return port[r];
                if (r == 0x29) return (byte)Chip.Status;
                if (r == 0x2E) return (byte)(Pcm.Length == 0 ? 0 : Pcm[(int)(romRead++ % Pcm.Length)]);   // ROM 읽기 (목소리 0 시작부터 하나씩)
                return 0;
            }
            if (BankAddr >= 0 && a >= 0x8000 && a < 0xC000) return Rom[(0x8000 + bank * 0x4000 + a - 0x8000) % Rom.Length];
            if (a < RamAddr && a < Rom.Length) return Rom[a];
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= RamAddr && a < RamAddr + Ram.Length) { Ram[a - RamAddr] = v; return; }
            if ((a & ~OpmMirror & 0xFFFE) == (OpmAddr & 0xFFFE)) { if ((a & 1) == 0) opmAddr = v; else OpmWrite(opmAddr, v); return; }
            if (a == NmiArm) { nmiAt = Cpu.Cycles + 64; return; }   // 무장: K053260 SH1 (클럭 ÷ 64) 다음 끝에서 NMI 한 번 — 처리기가 다시 무장
            if (a >= PcmAddr && a < PcmAddr + 0x30)
            {
                int r = a - PcmAddr;
                if (r < 4) { port[r] = v; return; }
                Chip.Write(0, r, v);
                if (r >= 0x0C && r <= 0x0E) { pcmStart[r - 0x0C] = v; romRead = pcmStart[0] | (pcmStart[1] << 8) | ((pcmStart[2] & 0x1F) << 16); }
                Emit(1, r, v);
                return;
            }
            if (a == BankAddr) { bank = v & 7; return; }
            if (a < RamAddr) return;
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
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
            if (chip == 0)
            {
                if (reg != 0x08 && reg != 0x01 && last[reg] == val) return;
                last[reg] = val;
            }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Konami K053260 (hoot)", Notes = "hoot konami 053260" };
            w.SetClock(VgmWriter.ClockYm2151, OpmClock);
            bool pcm = Pcm.Length > 0 && Log.Any(x => x.chip == 1);
            if (pcm)
            {
                w.SetClock(VgmWriter.ClockK053260, PcmClock);
                var cmd = new byte[7 + 8 + Pcm.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8E;
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
                if (x.chip == 1 && !pcm) continue;
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, x.reg, x.val);
                else w.Write(0xBA, x.reg, x.val);
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
