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
/// hoot 남코 System 86 (driver namco, type system86: Sky Kid Deluxe · The Return of Ishtar · Genpei Toumaden · Rolling Thunder · Wonder Momo): 사운드를 맡은 MCU를 흉내 낸다.
/// <para>기계 (MAME namcos86 MCU + Sky Kid DX 디스어셈블): HD63701 (E = 1.536MHz), 00h~1Fh 내부 레지스터, 80h~FFh 내부 RAM, 1000h~13FFh = CUS30 (파형 · 레지스터 · 메인과 나누는 RAM),
/// 1400h~1FFFh = RAM, YM2151 = hoot 옵션 opmaddr (Sky Kid DX · Rolling Thunder 2000h, Wonder Momo 3800h, Ishtar 6000h — 없으면 ROM의 STA 확장 쓰기로 찾음, Genpei 2800h),
/// 4000h~BFFFh = 외부 ROM (8000h에 A6h 머리 → 진입점 [8001h], hoot가 2000h~에도 올리면 그 자리도 롬), F000h~FFFFh = 내부 ROM (cus60, 남코 MCU 공용 OS). IRQ = 화면 60Hz, OCF 타이머 = 작업 전환.</para>
/// <para>요청: 1380h에 곡 번호 (드라이버가 받으면 FFh로). 63701X 음성 (rom pcm)은 아직 없음.</para>
/// <para>곡: 부팅 1초 뒤 요청, 180초를 YM2151 + CUS30 VGM으로 (CUS30 = 이 프로그램 안 명령 D8h).</para>
/// </summary>
public static class HootNamcoSys86
{
    public const double CpuClock = 49152000 / 32.0, OpmClock = 3579545, IrqHz = 60.606, Cus30Clock = 49152000 / 2048.0;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "namco", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "system86", StringComparison.OrdinalIgnoreCase);
    }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    /// <summary>YM2151 자리 찾기: 외부 ROM (4000h~BFFFh)의 STAA · STAB 확장 쓰기 중 2000h~7FFFh에서 짝 (a, a+1)이 가장 많은 것.</summary>
    public static int FindOpm(byte[] mem)
    {
        var n = new Dictionary<int, int>();
        for (int i = 0x4000; i < 0xBFFD; i++)
        {
            if (mem[i] != 0xB7 && mem[i] != 0xF7) continue;
            int a = (mem[i + 1] << 8) | mem[i + 2];
            if (a < 0x2000 || a >= 0x8000) continue;
            n.TryGetValue(a & ~1, out int c); n[a & ~1] = c + 1;
        }
        return n.Count == 0 ? 0x2000 : n.OrderByDescending(x => x.Value).First().Key;
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        var mem = new byte[0x10000];
        var loaded = new bool[0x100];   // hoot가 롬을 올린 256바이트 쪽
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type == "pcm") { unsupported.Add("63701X pcm"); continue; }
            if (type != "code") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (off < 0 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            int len = (int)Math.Min(d.Length, 0x10000 - off);
            Array.Copy(d, 0, mem, off, len);
            for (long p = off; p < off + len; p += 0x100) loaded[p >> 8] = true;
            any = true;
        }
        if (!any) missing.Add("(code)");
        int opm = -1;
        if (e.Options.TryGetValue("opmaddr", out var ov))
        {
            ov = ov.Trim();
            try { opm = ov.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? int.Parse(ov.Substring(2), NumberStyles.HexNumber) : int.Parse(ov); } catch (FormatException) { }
        }
        if (opm < 0) opm = FindOpm(mem);
        var m = new Machine(mem, opm);
        for (int p = 0x20; p < 0x100; p++) m.RomPage[p] = loaded[p];
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6801Bus
    {
        public readonly byte[] Mem;
        public readonly int Opm;
        public readonly Cpu6801 Cpu;
        /// <summary>롬으로 읽는 쪽 (256바이트 단위, 2000h 위): 4000h~BFFFh · F000h~ 말고도 hoot가 올린 자리 (Return of Ishtar = 0000h에도 → 2000h~3FFFh가 6000h~의 거울).</summary>
        public readonly bool[] RomPage = new bool[0x100];
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2151, 1 = CUS30 (reg = 0~13Fh, 바뀐 것만).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, TimerIrqs;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int opmAddr;
        private long playStart = -1;
        private readonly double irqCycles = CpuClock / IrqHz;
        private double nextIrq;
        private bool irqLine;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();

        public Machine(byte[] mem, int opm)
        {
            Mem = mem; Opm = opm;
            Cpu = new Cpu6801(this);
            Cpu.Reset();
            nextIrq = irqCycles;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Mem[0x1380] = (byte)code;
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq) { nextIrq += irqCycles; irqLine = true; }
                if (irqLine && Cpu.Irq()) { Irqs++; irqLine = false; }
                else if (Cpu.TimerIrq()) TimerIrqs++;
                if (Cpu.Waiting)
                {
                    long next = Math.Min((long)Math.Ceiling(nextIrq), Cpu.Cycles + Cpu.CyclesToTimerEvent());
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, next));
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a == Opm || a == Opm + 1) return 0;   // YM2151 상태: 바쁘지 않음
            if ((a >= 0x4000 && a < 0xC000) || RomPage[a >> 8]) return Mem[a];
            if (a >= 0xF000) return Mem[a];
            if (a >= 0x80 && a < 0x100) return Mem[a];
            if (a >= 0x1000 && a < 0x2000) return Mem[a];
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a == Opm) { opmAddr = v; return; }
            if (a == Opm + 1) { OpmWrite(opmAddr, v); return; }
            if (a >= 0x1000 && a < 0x1140) { if (Mem[a] != v) Emit(1, a - 0x1000, v); Mem[a] = v; return; }
            if ((a >= 0x80 && a < 0x100) || (a >= 0x1000 && a < 0x2000)) { Mem[a] = v; return; }
            if (a >= 0x4000 && a < 0xC000 || a >= 0xF000 || RomPage[a >> 8]) return;
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte PortRead(int port) { return 0xFF; }
        public void PortWrite(int port, byte value) { }

        private void OpmWrite(int reg, int v)
        {
            if (reg >= 0x10 && reg <= 0x12) return;   // 타이머는 아무 데도 안 이어짐 (MCU는 OCF로 박자)
            if (reg == 0x14) { Emit(0, reg, v & 0x80); return; }
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
            var w = new VgmWriter { System = "Namco System 86 (hoot)", Notes = "hoot namco system86" };
            w.SetClock(VgmWriter.ClockYm2151, OpmClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x54, x.reg, x.val);
                else w.Write(new byte[] { 0xD8, (byte)(x.reg >> 8), (byte)x.reg, (byte)x.val });
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Any(x => x.chip == 1)) v.Cus30Clock = Cus30Clock;
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
