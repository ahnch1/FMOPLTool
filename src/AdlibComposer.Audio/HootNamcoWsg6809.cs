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
/// hoot 남코 6809 + 15XX 보드 (driver namco, type wsg6809: Mappy · Super Pac-Man · Pac & Pal · Phozon · Grobda · Libble Rabble · Tower of Druaga · Gaplus · Dig Dug II · Motos · Toy Pop): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME mappy · gaplus · toypop 사운드 CPU, 매피 디스어셈블로 확인): 6809 1.536MHz, 0000h~03FFh = 15XX와 같이 쓰는 RAM (00h~3Fh = 목소리 레지스터, 나머지는 메인 CPU와 나눔),
/// 래치 2000h~200Fh (hoot 옵션 irq_enable_addr · irq_disable_addr — 없으면 켬 = 2001h 또는 4000h (Libble Rabble · Toy Pop), 끔 = 2000h, Gaplus는 4000h · 6000h), ROM E000h~FFFFh. IRQ = 화면 60Hz (켜져 있을 때, 끄기 쓰기가 확인).</para>
/// <para>부팅 맞춤 (메인 CPU 몫, 흉내): seq_type MOTO = 0040h "CK", GAPL = 0040h 11h, 사운드 CPU가 0041h에 0 아닌 값을 쓰면 0040h · 0041h를 지움 (Super Pac-Man · Phozon), 0042h = "OK"면 0040h = "GO" (Grobda · Motos). IRQ는 처음부터 켜짐 · 받으면 내려감. SUPE (Super Pac-Man)는 IRQ 없이 메인이 00FBh를 화면마다 올림.</para>
/// <para>요청: 0040h + 소리 번호에 0 아닌 값 (드라이버가 IRQ마다 훑음, 남아 있는 동안 울림). 파형 = rom pcm (PROM 256바이트).</para>
/// <para>곡: 부팅 1초 뒤 요청, 180초를 15XX VGM으로 (이 프로그램 안 명령 D8h — VGM에 없는 칩).</para>
/// </summary>
public static class HootNamcoWsg6809
{
    public const double CpuClock = 49152000 / 32.0, WsgClock = 18432000 / 768.0, IrqHz = 60.606;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "namco", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "wsg6809", StringComparison.OrdinalIgnoreCase);
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
        var mem = new byte[0x10000];
        var prom = new byte[0x100];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code" && type != "pcm") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "pcm") { Array.Copy(d, 0, prom, 0, Math.Min(d.Length, prom.Length)); continue; }
            if (off < 0x2000 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            Array.Copy(d, 0, mem, off, (int)Math.Min(d.Length, 0x10000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        int seq = Option(e, "seq_type", 0);
        var m = new Machine(mem, prom, Option(e, "irq_enable_addr", -1), Option(e, "irq_disable_addr", 0x2000));
        // 메인 CPU의 부팅 맞춤 (hoot seq_type): MOTO (Grobda · Motos) = 0040h "CK", GAPL (Gaplus) = 0040h 11h
        if (seq == 0x4D4F544F) { mem[0x40] = (byte)'C'; mem[0x41] = (byte)'K'; }
        else if (seq == 0x4741504C) mem[0x40] = 0x11;
        m.FrameCounter = seq == 0x53555045;   // SUPE (Super Pac-Man): IRQ 없음 (벡터 = 리셋), 메인 CPU가 00FBh를 화면마다 올림
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : ICpu6809Bus
    {
        public readonly byte[] Mem, Prom;
        public readonly int IrqEnable, IrqDisable;
        public readonly Cpu6809 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>15XX 쓰기 (reg = 00h~3Fh, 바뀐 것만).</summary>
        public readonly List<(double t, int reg, int val)> Log = new List<(double, int, int)>();
        public int Irqs, Handshakes;
        /// <summary>IRQ 대신 메인 CPU가 00FBh를 화면마다 올림 (Super Pac-Man).</summary>
        public bool FrameCounter;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private long playStart = -1;
        private readonly double irqCycles = CpuClock / IrqHz;
        private double nextIrq;
        private bool irqOn = true, irqLine;   // 메인 CPU가 처음부터 켜 둔 셈 (Super Pac-Man · Phozon은 사운드 CPU가 켜지 않음)

        public Machine(byte[] mem, byte[] prom, int irqEnable, int irqDisable)
        {
            Mem = mem; Prom = prom; IrqEnable = irqEnable; IrqDisable = irqDisable;
            Cpu = new Cpu6809(this);
            Cpu.Reset();
            nextIrq = irqCycles;
        }

        /// <summary>요청: 0040h + 소리 번호에 1.</summary>
        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            int a = 0x40 + (code & 0xFF);
            if (a < 0x400) Mem[a] = 1;
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq)
                {
                    nextIrq += irqCycles;
                    if (FrameCounter) { Mem[0xFB]++; continue; }
                    if (IrqEnable >= 0) irqOn = true;   // hoot 옵션 irq_enable_addr = 메인 CPU가 켜는 자리 (Super Pac-Man: 사운드 CPU는 부팅 때 끄기만 함)
                    if (irqOn) irqLine = true;
                }
                if (irqLine && Cpu.Irq()) { Irqs++; irqLine = false; }   // 받으면 내려감 (hold) — 매피처럼 처리기가 끄기를 써도 같음
                if (Cpu.Waiting) { Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, (long)Math.Ceiling(nextIrq))); continue; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x400 || a >= 0x8000) return Mem[a];
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0x400)
            {
                if (a < 0x40 && Mem[a] != v)
                {
                    if (playStart < 0) playStart = Cpu.Cycles;
                    Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), a, v));
                }
                Mem[a] = v;
                // 메인 CPU 맞춤 흉내: 부팅 중 사운드 CPU가 0041h에 0 아닌 값을 쓰면 (Super Pac-Man 4Fh · Phozon 02h) 메인이 0040h · 0041h를 지워 준다
                if (a == 0x41 && v != 0 && playStart < 0) { Mem[0x40] = 0; Mem[0x41] = 0; Handshakes++; }
                // Grobda · Motos: 자가 시험 뒤 0042h = "OK"를 쓰고 0040h = "GO"를 기다림
                if (a == 0x43 && v == 'K' && Mem[0x42] == 'O' && playStart < 0) { Mem[0x40] = (byte)'G'; Mem[0x41] = (byte)'O'; Handshakes++; }
                return;
            }
            if (a == IrqEnable || (IrqEnable < 0 && (a == 0x2001 || a == 0x4000))) { irqOn = true; return; }
            if (a == IrqDisable) { irqOn = false; irqLine = false; return; }
            if (a >= 0x8000) return;
            if ((a & 0xFFF0) == 0x2000) return;   // 그 밖의 래치 (소리 켬 · 메인 CPU 쪽)
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Namco 6809 + 15XX (hoot)", Notes = "hoot namco wsg6809" };
            for (int i = 0; i < Prom.Length; i++) w.Write(new byte[] { 0xD8, (byte)((0x200 + i) >> 8), (byte)(0x200 + i), Prom[i] });   // 파형 PROM
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(new byte[] { 0xD8, 0, (byte)x.reg, (byte)x.val });
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.Cus30Clock = WsgClock;
            v.Cus30Is15xx = true;
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
