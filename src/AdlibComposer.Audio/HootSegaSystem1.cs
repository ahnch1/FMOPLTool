using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 세가 System 1 (driver sega, type system1): 사운드 보드를 그대로 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME system1 sound_map): Z80 4MHz, 0000h~7FFFh = ROM, 8000h~9FFFh = RAM 2KB (거울), A000h~BFFFh = SN76489 #1 (2MHz) 쓰기,
/// C000h~DFFFh = SN76489 #2 (4MHz) 쓰기, E000h~FFFFh 읽기 = 메인 CPU가 보낸 명령 (보낼 때 NMI).
/// IRQ = 화면 줄 32부터 64줄마다 (프레임당 4번, 약 240Hz, IM 1).</para>
/// <para>곡: rom code = 사운드 ROM (가장 작은 offset을 0으로). 부팅 1초 뒤 곡 코드를 명령으로, 180초를 SN76489 둘 VGM으로
/// (첫째 = 2MHz, 둘째 = 4MHz — VGM 파일 머리에는 같은 클럭 둘로 적히고, 재생기는 곡의 Sn76489Clock · Sn76489Clock2를 쓴다).</para>
/// </summary>
public static class HootSegaSystem1
{
    public const double CpuClock = 4000000, Sn1Clock = 2000000, Sn2Clock = 4000000;
    public const double IrqRate = 60.0 * 4;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "sega", StringComparison.OrdinalIgnoreCase) && string.Equals((type ?? "").Trim(), "system1", StringComparison.OrdinalIgnoreCase);
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
        var rom = new byte[0x8000];
        if (code.Count > 0)
        {
            long min = code.Min(c => c.off);
            foreach (var (off, d) in code)
            {
                long at = off - min;
                if (at < rom.Length) Array.Copy(d, 0, rom, at, (int)Math.Min(d.Length, rom.Length - at));
            }
        }
        else missing.Add("(code)");
        var m = new Machine(rom);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom;
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = SN76489 #1 (2MHz), 1 = #2 (4MHz).</summary>
        public readonly List<(double t, int chip, int val)> Log = new List<(double, int, int)>();
        public int Nmis, Irqs, LatchReads;

        private int latch;
        private long playStart = -1;
        private long nextIrq;
        private readonly long irqPeriod = (long)(CpuClock / IrqRate);

        public Machine(byte[] rom)
        {
            Rom = rom;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextIrq = irqPeriod;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
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

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        private bool irqPending;

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq) { nextIrq += irqPeriod; irqPending = true; }
                if (irqPending && Cpu.Iff1)
                {
                    irqPending = false;   // HOLD_LINE: 받아들이면 내림
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted) { Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, nextIrq)); continue; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return Rom[a];
            if (a < 0xA000) return Ram[a & 0x7FF];
            if (a >= 0xE000) { LatchReads++; return (byte)latch; }
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x8000 && a < 0xA000) { Ram[a & 0x7FF] = v; return; }
            if (a >= 0xA000 && a < 0xC000) { Emit(0, v); return; }
            if (a >= 0xC000 && a < 0xE000) { Emit(1, v); return; }
        }

        private void Emit(int chip, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, val));
        }

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Sega System 1 (hoot)", Notes = "hoot sega system1" };
            w.SetClock(VgmWriter.ClockSn76489, Sn2Clock, 0x40000000u);   // 같은 칩 둘 (bit30)
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(x.chip == 0 ? (byte)0x50 : (byte)0x30, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.Sn76489Clock = Sn1Clock;
            v.Sn76489Clock2 = Sn2Clock;
            v.SnFeedback = 0x0006; v.SnWidth = 15;   // SN76489A
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
