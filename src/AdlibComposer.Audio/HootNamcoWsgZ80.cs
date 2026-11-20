using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 남코 Z80 + WSG 보드 (driver namco, type wsgz80: Bosconian · Galaga · Dig Dug · Xevious): 갤러그 계열 셋째 Z80 (사운드 CPU)을 흉내 낸다.
/// <para>기계 (MAME galaga · bosco · digdug · xevious): Z80 3.072MHz (18.432MHz / 6), ROM 0000h~3FFFh, WSG 6800h~681Fh (니블 하나씩, 팩맨과 같은 배치),
/// 래치 6820h~6827h (6822h = NMI 끔 (1) / 켬 (0) — 사운드 CPU는 부팅 때 끄고 메인 CPU가 켜므로 흉내에선 늘 켬), 나머지 4000h~FFFFh는 메인 CPU와 같이 쓰는 RAM으로 본다. NMI = 화면당 둘 (줄 64 · 192, 약 121Hz).</para>
/// <para>요청: hoot 옵션 seq_addr + 소리 번호에 1 (드라이버가 NMI마다 훑음). 파형 = rom pcm (PROM 256바이트, 첫 것).</para>
/// <para>곡: 부팅 1초 뒤 요청, 180초를 VGM으로 — WSG 목소리 3개를 15XX 목소리 0~2로 옮겨 이 프로그램 안 명령 D8h로 (칩 속도 96kHz = 3.072MHz / 32, 주파수 공식이 같음).</para>
/// </summary>
public static class HootNamcoWsgZ80
{
    public const double CpuClock = 18432000 / 6.0, WsgClock = 18432000 / 6.0 / 32, NmiHz = 60.606 * 2;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "namco", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "wsgz80", StringComparison.OrdinalIgnoreCase);
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
        byte[] prom = null;
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off0, text) in e.Roms)
        {
            int off = (int)off0;
            if (type != "code" && type != "pcm") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "pcm") { if (prom == null && d.Length >= 0x100) { prom = new byte[0x100]; Array.Copy(d, prom, 0x100); } continue; }   // 둘째 PROM (bosco 5c)은 타이밍용
            if (off < 0) off = 0;   // 오프셋 없음 (digdug) = 0
            if (off >= 0x4000) { unsupported.Add(type + " offset " + off); continue; }
            Array.Copy(d, 0, mem, off, Math.Min(d.Length, 0x4000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        if (prom == null) { missing.Add("(pcm)"); prom = new byte[0x100]; }
        var m = new Machine(mem, prom, Option(e, "seq_addr", 0x9AA0));
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Mem, Prom;
        public readonly int SeqAddr;
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>WSG 쓰기를 15XX 레지스터로 옮긴 것 (reg = 목소리 × 8 + 3~6, 바뀐 것만).</summary>
        public readonly List<(double t, int reg, int val)> Log = new List<(double, int, int)>();
        public int Nmis, NmiOffWrites;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>();

        private readonly byte[] wsg = new byte[0x20];
        private readonly int[] last = Enumerable.Repeat(-1, 0x40).ToArray();
        private long playStart = -1;
        private double nextNmi;

        public Machine(byte[] mem, byte[] prom, int seqAddr)
        {
            Mem = mem; Prom = prom; SeqAddr = seqAddr;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextNmi = CpuClock / NmiHz;
        }

        /// <summary>요청: seq_addr + 소리 번호에 1.</summary>
        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Mem[(SeqAddr + (code & 0xFF)) & 0xFFFF] = 1;
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextNmi)
                {
                    nextNmi += CpuClock / NmiHz;
                    {
                        Nmis++;
                        Cpu.Halted = false;
                        Cpu.Iff2 = Cpu.Iff1; Cpu.Iff1 = false;
                        Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
                        Mem[Cpu.SP] = (byte)Cpu.PC; Mem[(Cpu.SP + 1) & 0xFFFF] = (byte)(Cpu.PC >> 8);
                        Cpu.PC = 0x66;
                        Cpu.Cycles += 11;
                    }
                }
                if (Cpu.Halted) { Cpu.Cycles = Math.Max(Cpu.Cycles + 4, Math.Min(end, (long)Math.Ceiling(nextNmi))); continue; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a >= 0x6800 && a < 0x6840) return 0xFF;
            if (a >= 0x7000 && a < 0x7200) return 0xFF;   // 06XX (메인 CPU 몫)
            return Mem[a];
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return;
            if (a >= 0x6800 && a < 0x6820) { WsgWrite(a & 0x1F, v & 15); return; }
            if (a >= 0x6820 && a < 0x6828) { if (a == 0x6822) NmiOffWrites += v & 1; return; }   // 6822h: 사운드 CPU가 부팅 때 끄고 메인 CPU가 켜 줌 — 흉내에선 늘 켬
            if (a >= 0x6828 && a < 0x6840) return;   // 감시 타이머 등
            if (a >= 0x7000 && a < 0x7200) { OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1; return; }
            Mem[a] = v;
        }

        public byte In(int p) { return 0xFF; }
        public void Out(int p, byte v) { }

        /// <summary>WSG 레지스터 (0x05 · 0x0A · 0x0F 파형, 0x10~0x14 · 0x16~0x19 · 0x1B~0x1E 주파수, 0x15 · 0x1A · 0x1F 음량) → 15XX 목소리 0~2.</summary>
        private void WsgWrite(int r, int v)
        {
            wsg[r] = (byte)v;
            int c = r < 0x10 ? (r - 5) / 5 : r == 0x10 ? 0 : (r - 0x11) / 5;
            if (c < 0 || c > 2) return;
            int b = c * 5;
            int freq = (c == 0 ? wsg[0x10] : 0) | (wsg[b + 0x11] << 4) | (wsg[b + 0x12] << 8) | (wsg[b + 0x13] << 12) | (wsg[b + 0x14] << 16);
            int wave = wsg[b + 0x05] & 7, vol = wsg[b + 0x15];
            Emit(c * 8 + 3, vol);
            Emit(c * 8 + 4, freq & 0xFF);
            Emit(c * 8 + 5, (freq >> 8) & 0xFF);
            Emit(c * 8 + 6, (wave << 4) | ((freq >> 16) & 15));
        }

        private void Emit(int reg, int val)
        {
            if (last[reg] == val) return;
            last[reg] = val;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Namco Z80 + WSG (hoot)", Notes = "hoot namco wsgz80" };
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
