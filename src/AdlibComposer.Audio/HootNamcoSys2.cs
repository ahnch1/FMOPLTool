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
/// hoot 남코 System 2 (driver namco, type system2: Final Lap · Assault · Metal Hawk · Ordyne · Mirai Ninja · Phelios · Dirt Fox · Four Trax · Valkyrie · Burning Force · Marvel Land · Dragon Saber ·
/// Rolling Thunder 2 · Steel Gunner · Cosmo Gang · Bubble Trouble · Lucky &amp; Wild · Suzuka 8 Hours · Super World Stadium '92 …): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME namcos2 sound + Metal Hawk 디스어셈블): 6809 2.048MHz, 0000h~3FFFh = 뱅크 (사운드 ROM 16KB 단위, C000h · C001h에 쓴 값 &gt;&gt; 4), 4000h = YM2151 (3.58MHz),
/// 5000h~6FFFh = C140 (200h 거울), 7000h~7FFFh = 메인과 나누는 RAM (2KB 거울), 8000h~9FFFh = RAM, C000h~FFFFh = ROM 첫 16KB.
/// IRQ = 화면 120Hz, FIRQ = C140 타이머 (1FEh bit0 켬, 주기 = 1F8h × 2 / 21.3kHz, 1FAh = 확인). 부팅 맞춤 = 703Ah = A6h · 703Bh = 0, 뱅크 8 표시 확인이 실패하면 (77FEh = FFh) 메인이 77FDh에 A6h · 6Ah로 답함.</para>
/// <para>요청: hoot 옵션 codeaddr (7100h · 7110h) + 2 × 묶음 = 곡 번호, 다음 바이트 = 명령 (40h) — hoot 코드 = 묶음 &lt;&lt; 8 | 곡 (System 1과 같음).</para>
/// <para>곡: 부팅 1초 뒤 요청, 180초를 YM2151 + C140 VGM으로 (C140 ROM = rom pcm, VGM 블록 8Dh).</para>
/// </summary>
public static class HootNamcoSys2
{
    public const double CpuClock = 49152000 / 24.0, OpmClock = 3579545, IrqHz = 120, C140Clock = 49152000 / 2304.0;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "namco", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "system2", StringComparison.OrdinalIgnoreCase);
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
        int codeAddr = 0x7100;
        if (e.Options.TryGetValue("codeaddr", out var cv))
        {
            cv = cv.Trim();
            try { codeAddr = cv.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(cv.Substring(2), NumberStyles.HexNumber) : int.Parse(cv); } catch (FormatException) { }
        }
        var m = new Machine(rom, p, codeAddr);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6809Bus
    {
        public readonly byte[] Rom, Pcm;
        public readonly int CodeAddr;
        public readonly byte[] Shared = new byte[0x800], Ram = new byte[0x2000], C140 = new byte[0x200];
        public readonly Cpu6809 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151, 1 = C140 (reg 0~1FFh).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, Firqs;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int bank, opmAddr;
        private bool romError;
        /// <summary>시작 명령 (bit6 = 시작, 아래 6비트는 드라이버가 받아 둠).</summary>
        public int PlayCommand = 0x40;
        private long playStart = -1;
        private readonly double irqCycles = CpuClock / IrqHz;
        private double nextIrq;
        private bool irqLine, firqLine;
        private long nextTimer = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();
        private int timerA, timerB, timerCtl, timerFlags;

        public Machine(byte[] rom, byte[] pcm, int codeAddr)
        {
            Rom = rom; Pcm = pcm; CodeAddr = codeAddr;
            Shared[0x3A] = 0xA6;   // 메인의 부팅 맞춤 (703Ah = A6h, 703Bh = 0)
            Cpu = new Cpu6809(this);
            Cpu.Reset();
            nextIrq = irqCycles;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            int a = (CodeAddr & 0x7FF) + (code >> 8) * 2;
            if (a + 1 < Shared.Length) { Shared[a] = (byte)code; Shared[a + 1] = (byte)PlayCommand; }
        }

        private long TimerPeriod { get { int div = C140[0x1F8] != 0 ? C140[0x1F8] : 256; return Math.Max(1, (long)(div * 2 / C140Clock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq) { nextIrq += irqCycles; irqLine = true; }
                if (Cpu.Cycles >= nextTimer) { nextTimer = long.MaxValue; firqLine = true; }   // 1FAh 확인 때 다시 걸림
                if (firqLine && Cpu.Firq()) Firqs++;
                else if (irqLine && Cpu.Irq()) { Irqs++; irqLine = false; }
                if (Cpu.Waiting)
                {
                    long next = Math.Min((long)Math.Ceiling(nextIrq), nextTimer);
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, next));
                    continue;
                }
                Cpu.Step();
            }
        }

        private int RomAt(int a) { return a < Rom.Length ? Rom[a] : 0xFF; }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return (byte)RomAt(((bank % Math.Max(1, Rom.Length >> 14)) << 14) | a);   // 뱅크 n = ROM n × 4000h (롬보다 크면 감쌈)
            if (a >= 0xC000) return Rom[a - 0xC000];
            if (a >= 0x8000 && a < 0xA000) return Ram[a - 0x8000];
            if (a >= 0x7000 && a < 0x8000)
            {
                byte r = Shared[a & 0x7FF];
                if ((a & 0x7FF) == 0x7FD && r == 0xA6 && romError) Shared[0x7FD] = 0x6A;   // 메인의 답: A6h 다음 6Ah
                return r;
            }
            if (a >= 0x5000 && a < 0x7000) return C140[a & 0x1FF];
            if (a == 0x4000 || a == 0x4001) return (byte)timerFlags;
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x8000 && a < 0xA000) { Ram[a - 0x8000] = v; return; }
            if (a >= 0x7000 && a < 0x8000)
            {
                Shared[a & 0x7FF] = v;
                if ((a & 0x7FF) == 0x7FE && v == 0xFF && playStart < 0) { romError = true; Shared[0x7FD] = 0xA6; }   // 뱅크 표시 ("KOBA") 확인 실패 → 메인이 77FDh로 답 (Burning Force 계열)
                return;
            }
            if (a >= 0x5000 && a < 0x7000) { C140Write(a & 0x1FF, v); return; }
            switch (a)
            {
                case 0x4000: opmAddr = v; return;
                case 0x4001: OpmWrite(opmAddr, v); return;
                case 0xC000: case 0xC001: bank = (v >> 4) & 0x0F; return;
            }
            if (a >= 0xA000) return;   // 앰프 켜기 · 와치독 · ROM
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        private void C140Write(int r, int v)
        {
            C140[r] = (byte)v;
            if (r == 0x1FA) { firqLine = false; if ((C140[0x1FE] & 1) != 0) nextTimer = Cpu.Cycles + TimerPeriod; return; }
            if (r == 0x1FE) { nextTimer = (v & 1) != 0 ? Cpu.Cycles + TimerPeriod : long.MaxValue; return; }
            if (r >= 0x180) return;   // 그 밖의 제어
            Emit(1, r, v);
        }

        private void OpmWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x10: timerA = (timerA & 3) | (v << 2); return;
                case 0x11: timerA = (timerA & 0x3FC) | (v & 3); return;
                case 0x12: timerB = v; return;
                case 0x14: if ((v & 0x10) != 0) timerFlags &= ~1; if ((v & 0x20) != 0) timerFlags &= ~2; timerCtl = v; Emit(0, reg, v & 0x80); return;
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
            var w = new VgmWriter { System = "Namco System 2 (hoot)", Notes = "hoot namco system2" };
            w.SetClock(VgmWriter.ClockYm2151, OpmClock);
            bool pcm = Pcm.Length > 0 && Log.Any(x => x.chip == 1);
            if (pcm)
            {
                w.SetClock(VgmWriter.ClockC140, C140Clock);
                w.SetHeaderByte(0x96, 0);   // System 2
                var cmd = new byte[7 + 8 + Pcm.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8D;
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
                else w.Write(new byte[] { 0xD4, (byte)(x.reg >> 8), (byte)x.reg, (byte)x.val });
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
