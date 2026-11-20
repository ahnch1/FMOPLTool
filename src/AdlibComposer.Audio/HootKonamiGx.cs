using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 코나미 System GX (driver konami, type systemgx: Fantastic Journey · Lethal Enforcers II · Golfing Greats 2 · Puzzle Dama · Racin' Force · Soccer Superstars · Dragoon Might · Taisen Puzzle-dama ·
/// Tokimeki Memorial · Daisu-Kiss · Run and Gun 2 · Rushing Heroes · Salamander 2 · Sexy Parodius · Tokkae · Versus Net Soccer · Winning Spike …): 68000 사운드 CPU를 흉내 낸다.
/// <para>K056800: 사운드 쪽 읽기 400011h~40001Fh (오프셋 8~15) = 메인 레지스터 0~7, 쓰기 400001h~ (0~7) = 답.</para>
/// <para>기계 (MAME konamigx gxsndmap): 68000 8MHz, 000000h~03FFFFh ROM (두 파일 바이트 엇갈림: 짝수 = 첫 파일), 100000h~10FFFFh RAM,
/// 200000h~2004FFh = K054539 둘 (레지스터 = (주소 − 200000h) ÷ 2, 짝수 바이트 = #1 · 홀수 = #2 — 디스어셈블로 확인: 20045Eh ← 21h = #1 22Fh), 300000h · 500000h = TMS57002 DSP (없음 — 잔향, 상태 500001h = 07h),
/// 400000h~40001Fh K056800 (홀수 바이트, 메인과 통신). IRQ 1 = 명령 (400011h~17h 네 바이트를 큐 103C80h~에), IRQ 2 = K054539 #1 타이머 (227h, 22Fh bit5) — 처리기가 틱을 셈.</para>
/// <para>곡: 부팅 1초 뒤 명령, 180초를 K054539 × 2 VGM으로.</para>
/// </summary>
public static class HootKonamiGx
{
    public const double CpuClock = 8000000, PcmClock = 18432000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "konami", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "systemgx", StringComparison.OrdinalIgnoreCase);
    }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file) { return Boot(e, file, 1, -1); }

    /// <summary>부팅 (seconds 동안), host0 ≥ 0이면 부팅 전 메인 레지스터 0 (진단).</summary>
    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file, double seconds, int host0)
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
        var rom = new byte[0x40000];
        var ordered = code.OrderBy(c => c.off).ToList();
        if (ordered.Count >= 2)
        {
            // 두 파일: 바이트 엇갈림 (첫 파일 = 짝수 주소)
            var a = ordered[0].d; var b = ordered[1].d;
            for (int i = 0; i < Math.Min(a.Length, rom.Length / 2); i++) rom[i * 2] = a[i];
            for (int i = 0; i < Math.Min(b.Length, rom.Length / 2); i++) rom[i * 2 + 1] = b[i];
        }
        else if (ordered.Count == 1) Array.Copy(ordered[0].d, 0, rom, 0, Math.Min(rom.Length, ordered[0].d.Length));
        var p = new byte[pcm.Count == 0 ? 0 : (int)pcm.Max(x => x.off + x.d.Length)];
        foreach (var (off, d) in pcm) Array.Copy(d, 0, p, off, d.Length);
        var m = new Machine(rom, p);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        if (host0 >= 0) m.Host[0] = (byte)host0;
        m.RunSeconds(seconds > 0 ? seconds : 0.3);
        return m;
    }

    public sealed class Machine : IM68kBus
    {
        public readonly byte[] Rom, Pcm;
        public readonly byte[] Ram = new byte[0x10000];
        public readonly K054539[] Chips = { new K054539(), new K054539() };
        public readonly M68000 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 1 = K054539 #1, 2 = #2 (reg 0~22Fh).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int CmdIrqs, TimerIrqs;
        public readonly Dictionary<uint, int> OtherWrites = new Dictionary<uint, int>(), OtherReads = new Dictionary<uint, int>(), CommReads = new Dictionary<uint, int>();
        /// <summary>K056800 메인 → 사운드 레지스터 (사운드 쪽 읽기 0~7).</summary>
        public readonly byte[] Host = new byte[8];
        public readonly byte[] Reply = new byte[0x10];

        private long playStart = -1;
        private bool cmdIrq;
        private readonly byte[][] pcmReg = { new byte[0x230], new byte[0x230] };
        private readonly int[][] lastPcm = { Enumerable.Repeat(-1, 0x230).ToArray(), Enumerable.Repeat(-1, 0x230).ToArray() };
        private readonly int[] romPos = new int[2];
        private long nextToggle = long.MaxValue;
        private bool timerState, timerIrq;
        private long TimerPeriod { get { return Math.Max(1, (long)(38.0 * (255 - pcmReg[0][0x227]) / PcmClock * CpuClock)); } }

        public Machine(byte[] rom, byte[] pcm)
        {
            Rom = rom; Pcm = pcm;
            foreach (var c in Chips) c.LoadRom(pcm, 0);
            Cpu = new M68000(this) { AddressErrors = false };
            Cpu.Reset();
        }

        /// <summary>명령: 레지스터 0 = 소리 번호 (코드 아래 바이트), 1 = 묶음 (위 바이트 — hoot 1xxh · 2xxh …), 그리고 IRQ 1 (드라이버 1A6Ah: D1 = 번호, D2 & 7 = 묶음).</summary>
        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            Host[0] = (byte)code;          // 소리 번호
            Host[1] = (byte)(code >> 8);   // 묶음 (아래 3비트: 1 = 음악, 2 = 효과음 …)
            cmdIrq = true;
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextToggle) { nextToggle += TimerPeriod; timerState = !timerState; if (timerState) timerIrq = true; }   // IRQ 2 = K054539 #1 타이머 (올라가는 끝)
                Cpu.IrqLevel = timerIrq ? 2 : cmdIrq ? 1 : 0;
                if (Cpu.Stopped && Cpu.IrqLevel == 0) { Cpu.Cycles = Math.Max(Cpu.Cycles + 4, Math.Min(end, nextToggle)); continue; }
                Cpu.Step();
            }
        }

        public int Acknowledge(int level)
        {
            if (level == 2) { timerIrq = false; TimerIrqs++; }
            else { cmdIrq = false; CmdIrqs++; }
            return -1;
        }

        public byte Read8(uint a)
        {
            a &= 0xFFFFFF;
            if (a < 0x40000) return Rom[a];
            if (a >= 0x100000 && a < 0x110000) return Ram[a - 0x100000];
            if (a >= 0x200000 && a < 0x200500)
            {
                int chip = (int)(a & 1);   // 짝수 바이트 = #1, 홀수 = #2
                int r = (int)((a - 0x200000) >> 1);
                if (r >= 0x230) return 0;
                if (r == 0x22C) return (byte)Chips[chip].Active;
                if (r == 0x22D) return (byte)(Pcm.Length == 0 ? 0 : Pcm[(pcmReg[chip][0x22E] * 0x10000 + romPos[chip]++) % Pcm.Length]);
                return pcmReg[chip][r];
            }
            if (a >= 0x400000 && a < 0x400020)
            {
                int r = (int)((a - 0x400000) >> 1) & 0x0F;
                if (playStart >= 0) { CommReads.TryGetValue(a, out int cc); CommReads[a] = cc + 1; }
                return r >= 8 ? Host[r - 8] : Reply[r];   // 사운드 쪽: 8~15 = 메인이 쓴 레지스터, 0~7 = 사운드가 쓴 답
            }
            if (a == 0x500000 || a == 0x500001) return (byte)((a & 1) != 0 ? 0x07 : 0);   // TMS57002 상태: dready · pc0 · empty (DSP는 없지만 늘 준비)
            if (a >= 0x200000 && a < 0x600000) { OtherReads.TryGetValue(a, out int dc); OtherReads[a] = dc + 1; return 0; }   // TMS57002 등
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public ushort Read16(uint a) { return (ushort)((Read8(a) << 8) | Read8(a + 1)); }

        public void Write8(uint a, byte v)
        {
            a &= 0xFFFFFF;
            if (a >= 0x100000 && a < 0x110000) { Ram[a - 0x100000] = v; return; }
            if (a >= 0x200000 && a < 0x200500)
            {
                int chip = (int)(a & 1);
                int r = (int)((a - 0x200000) >> 1);
                if (r < 0x230) PcmWrite(chip, r, v);
                return;
            }
            if (a >= 0x400000 && a < 0x400020) { Reply[(a - 0x400000) >> 1 & 0x0F] = v; return; }
            if (a < 0x40000 || (a >= 0x200000 && a < 0x600000)) return;
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public void Write16(uint a, ushort v) { Write8(a, (byte)(v >> 8)); Write8(a + 1, (byte)v); }

        private void PcmWrite(int chip, int r, int v)
        {
            if (r == 0x22D) return;
            if (r == 0x22E) romPos[chip] = 0;
            pcmReg[chip][r] = (byte)v;
            Chips[chip].Write(0, r, v);
            if (chip == 0 && r == 0x227 && (pcmReg[0][0x22F] & 0x20) != 0) nextToggle = Cpu.Cycles + TimerPeriod;
            if (chip == 0 && r == 0x22F) { if ((v & 0x20) != 0) { if (nextToggle == long.MaxValue) nextToggle = Cpu.Cycles + TimerPeriod; } else { nextToggle = long.MaxValue; timerState = false; } }
            if (r == 0x22C || r == 0x227) return;
            if (r != 0x214 && r != 0x215) { if (lastPcm[chip][r] == v) return; lastPcm[chip][r] = v; }
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), 1 + chip, r, v));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Konami System GX (hoot)", Notes = "hoot konami systemgx" };
            bool two = Log.Any(x => x.chip == 2);
            bool pcm = Pcm.Length > 0 && Log.Count > 0;
            if (pcm)
            {
                w.SetClock(VgmWriter.ClockK054539, PcmClock, two ? 0x40000000u : 0);
                w.SetHeaderByte(0x95, 0x02);
                for (int c = 0; c < (two ? 2 : 1); c++)
                {
                    var cmd = new byte[7 + 8 + Pcm.Length];
                    cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8C;
                    uint size = (uint)(8 + Pcm.Length) | (c == 1 ? 0x80000000u : 0);
                    cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                    int total = Pcm.Length;
                    cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                    Array.Copy(Pcm, 0, cmd, 15, Pcm.Length);
                    w.Write(cmd);
                }
            }
            double total2 = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (!pcm) break;
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(new byte[] { 0xD3, (byte)((x.reg >> 8) | (x.chip == 2 ? 0x80 : 0)), (byte)x.reg, (byte)x.val });
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
