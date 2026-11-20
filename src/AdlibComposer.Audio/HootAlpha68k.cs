using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 알파 덴시 Alpha 68k II / V 사운드 보드 (driver alpha, type 68k2: Sky Adventure · Gang Wars · Super Champion Baseball · Time Soldiers · Sky Soldiers · Gold Medalist):
/// 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME alpha68k II sound_map / sound_iomap): Z80 4MHz, 0000h~7FFFh = ROM, 8000h~87FFh = RAM, C000h~FFFFh = 뱅크 창 (I/O 0Eh = n → 영역 10000h + n × 4000h),
/// I/O 00h 읽기 = 명령 (드라이버가 두 번 읽어 같을 때까지 폴링) · 쓰기 = 지움, 08h = DAC (8비트), 0Ah · 0Bh = YM2413 (3.58MHz), 0Ch · 0Dh = YM2203 (3MHz).
/// 인터럽트 = 7614Hz 주기 <b>NMI</b> (MAME nmi_line_pulse) — 0066h 처리기가 DAC 샘플을 뱅크에서 읽어 08h로 내고 틱을 세어 8004h · 8003h를 세우면,
/// 인터럽트를 끈 주 루프가 RST 38h · RST 30h로 음악 틱을 부른다 (IRQ는 쓰지 않음). Alpha 68k V 판 (Sky Adventure …)도 같다 — NMI 벡터 JP 0100h → 0100h가 JP 처리기 (다시 시작이 아님).</para>
/// <para>ROM: code 파일이 두 번 (offset 0 · 18000h) 적혀 있으면 MAME ROM_LOAD + ROM_CONTINUE처럼 앞 반 · 뒤 반. pcm = 뱅크 영역의 소리 ROM (offset 그대로).
/// DAC는 VGM YM2612 DAC (2Ah)로. 곡: 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM2203 + YM2413 + YM2612 DAC VGM으로.</para>
/// </summary>
public static class HootAlpha68k
{
    public const double CpuClock = 4000000, OpnClock = 3000000, OpllClock = 3579545, IrqHz = 7614;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "alpha", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "68k2", StringComparison.OrdinalIgnoreCase);
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
        var parts = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        var codeRoms = e.Roms.Where(r => r.Type == "code").ToList();
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code" && type != "pcm") { unsupported.Add(type); continue; }
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "code")
            {
                // 같은 파일이 여러 번이면 나눠서 (ROM_LOAD + ROM_CONTINUE): k번째 = 파일의 k번째 조각
                var same = codeRoms.Where(r => string.Equals(r.Text, text, StringComparison.OrdinalIgnoreCase)).ToList();
                if (same.Count > 1)
                {
                    int k = same.FindIndex(r => r.Offset == off), size = d.Length / same.Count;
                    var piece = new byte[size];
                    Array.Copy(d, k * size, piece, 0, size);
                    d = piece;
                }
            }
            parts.Add((off, d));
        }
        if (codeRoms.Count == 0) missing.Add("(code)");
        long end = Math.Max(0x20000, parts.Count == 0 ? 0 : parts.Max(p => p.off + p.d.Length));
        var region = new byte[end];
        foreach (var (off, d) in parts) Array.Copy(d, 0, region, off, d.Length);
        var m = new Machine(region);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Region;
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM2203, 1 = YM2413, 2 = DAC (값).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, LatchReads, DacWrites;
        public readonly Dictionary<int, int> OtherPorts = new Dictionary<int, int>();

        private int latch, bank, opnAddr, opllAddr, lastDac = -1;
        private long playStart = -1;
        private readonly double irqCycles = CpuClock / IrqHz;
        private double nextIrq;


        public Machine(byte[] region)
        {
            Region = region;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextIrq = irqCycles;
        }

        public void Play(int code)
        {
            var init = Log.Where(x => x.chip != 2).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            lastDac = -1;
            playStart = Cpu.Cycles;
            Send(code & 0xFF);
        }

        /// <summary>명령: 래치에 놓기만 (NMI 없음 — 주 루프가 폴링).</summary>
        public void Send(int code) { latch = code; }

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

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextIrq) { nextIrq += irqCycles; Nmi(); }   // 주기 NMI (막을 수 없음)
                if (Cpu.Halted) { Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, (long)Math.Ceiling(nextIrq))); continue; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return Region[a];
            if (a < 0x8800) return Ram[a - 0x8000];
            if (a >= 0xC000) { long r = 0x10000 + (long)bank * 0x4000 + (a - 0xC000); return r < Region.Length ? Region[r] : (byte)0xFF; }
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x8000 && a < 0x8800) Ram[a - 0x8000] = v;
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if (port == 0x00) { LatchReads++; return (byte)latch; }
            if (port == 0x0C || port == 0x0D) return 0;   // YM2203 상태 (바쁨 · 타이머 없음)
            OtherPorts.TryGetValue(0x100 | port, out int c); OtherPorts[0x100 | port] = c + 1;
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            switch (port)
            {
                case 0x00: latch = 0; return;   // 명령 지움
                case 0x08:
                    DacWrites++;
                    if (v != lastDac) { lastDac = v; Emit(2, 0x2A, v); }
                    return;
                case 0x0A: opllAddr = v; return;
                case 0x0B: Emit(1, opllAddr, v); return;
                case 0x0C: opnAddr = v; return;
                case 0x0D: Emit(0, opnAddr, v); return;
                case 0x0E: bank = v & 0x1F; return;
            }
            OtherPorts.TryGetValue(port, out int c); OtherPorts[port] = c + 1;
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Alpha 68k (hoot)", Notes = "hoot alpha 68k2" };
            w.SetClock(VgmWriter.ClockYm2203, OpnClock);
            bool opll = Log.Any(x => x.chip == 1);
            if (opll) w.SetClock(VgmWriter.ClockYm2413, OpllClock);
            bool dac = Log.Any(x => x.chip == 2 && x.val != 0x80);
            if (dac) { w.SetClock(VgmWriter.ClockYm2612, 7670453); w.Write(0x52, 0x2B, 0x80); w.Write(0x52, 0x2A, 0x80); }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 2 && !dac) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(x.chip == 0 ? (byte)0x55 : x.chip == 1 ? (byte)0x51 : (byte)0x52, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
