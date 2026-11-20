using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot SNK68 사운드 보드 (driver snk, type 3812: P.O.W. · SAR · Street Smart · Ikari III): 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME snk68 sound_map · sound_io_map): Z80 4MHz, 0000h~EFFFh = ROM, F000h~F7FFh = RAM, F800h 읽기 = 명령 (68000이 쓰면 NMI).
/// I/O 00h = YM3812 주소 · 상태, 20h = YM3812 자료, 40h = uPD7759 곡 번호 (쓰면 START), 80h = uPD7759 /RESET (bit7). YM3812 4MHz, 타이머 = IRQ (IM 1).</para>
/// <para>uPD7759 (640kHz)는 이 보드에서 자기 ROM을 읽는다 (마스터 모드). VGM에는 슬레이브 모드만 있어서, 보드가 칩 대신 ROM 바이트를 읽어
/// 요청 (DRQ)마다 넣는다: FFh (마지막 곡 번호 자리 — 슬레이브 칩은 10h 이상이어야 계속) · 빈 바이트 · 주소 위 · 아래 (ROM 5 + 2n · 6 + 2n) ·
/// 그 뒤 (위 &lt;&lt; 9 | 아래 &lt;&lt; 1)부터 차례로 — 첫 바이트는 DUMMY2가 버린다 (MAME upd7759 마스터 모드가 offset++로 건너뛰는 것과 같다). 되풀이 블록 (C0h)은 되감지 않음.</para>
/// <para>곡: rom code = Z80 ROM, pcm = uPD7759 ROM. 부팅 1초 뒤 곡 코드를 명령으로, 180초를 YM3812 + uPD7759 VGM으로.</para>
/// </summary>
public static class HootSnk68
{
    public const double CpuClock = 4000000, OplClock = 4000000, UpdClock = Upd7759Core.StandardClock;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "snk", StringComparison.OrdinalIgnoreCase)
            && string.Equals((type ?? "").Trim(), "3812", StringComparison.OrdinalIgnoreCase);
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
            if (off < 0) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            list.Add((off, d));
        }
        if (code.Count == 0) missing.Add("(code)");
        byte[] Join(List<(long off, byte[] d)> parts, int min)
        {
            long size = Math.Max(min, parts.Count == 0 ? 0 : parts.Max(p => p.off + p.d.Length));
            var r = new byte[size];
            foreach (var (off, d) in parts) Array.Copy(d, 0, r, off, d.Length);
            return r;
        }
        var m = new Machine(Join(code, 0x10000), Join(pcm, 0));
        if (Environment.GetEnvironmentVariable("HOOT_PCQ") != null) m.PcCount = new Dictionary<int, int>();   // 진단: 부팅 때부터 PC 세기
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        // SNK68 드라이버는 처음에 음악 · 효과음이 꺼져 있다: 깃발 (P.O.W. F115h, SAR F116h) = 0Ch, bit2 = BGM 끔 (곡 처리기가 그냥 돌아감), bit3 = 효과음 · 목소리 끔.
        // 명령 형식 표 (ROM)에서 형식 1, 낮은 니블만 있는 명령 (00h~0Fh)은 같은 값이 두 번 연달아 와야 처리되고 (F12Fh에 앞 값),
        // 그 처리기가 명령 bit0이면 bit2 → BGM 켬 · bit3 → 효과음 켬 — 0Dh (bit0 · 2 · 3)를 두 번 = 둘 다 켬. 네 게임 모두 같다 (35h는 P.O.W. · Ikari III에서만 BGM만)
        string env = Environment.GetEnvironmentVariable("HOOT_SNKINIT");   // 진단: 부팅 명령 바꿔 보기 (16진, 쉼표)
        foreach (var c in env != null ? env.Split(',').Select(x => Convert.ToInt32(x, 16)) : new[] { 0x0D, 0x0D })
        {
            m.Send(c);
            m.RunSeconds(0.2);
        }
        m.SoundOn = true;
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, Pcm;
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly Upd7759Core Upd = new Upd7759Core();
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM3812 (reg, val), 1 = uPD7759 (0 /RESET · 1 START · 2 데이터).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Nmis, Irqs, LatchReads, UpdStarts, UpdBytes;
        /// <summary>부팅 뒤 음악 · 효과음 켬 (명령 0Dh 두 번)을 보냈다.</summary>
        public bool SoundOn;
        public readonly Dictionary<int, int> OtherOuts = new Dictionary<int, int>();
        /// <summary>진단: 명령마다 PC 횟수 (null = 끔), YM3812 타이머 레지스터 (2 · 3 · 4) 쓰기.</summary>
        public Dictionary<int, int> PcCount;
        public readonly List<(long cyc, int reg, int val)> TimerWrites = new List<(long, int, int)>();

        private int latch, oplAddr;
        private long playStart = -1, lastCycles;
        private int t1, t2, tCtl, tFlags;
        private long nextT1 = long.MaxValue, nextT2 = long.MaxValue;
        private readonly Dictionary<int, int> regs = new Dictionary<int, int>();
        private double updAcc;
        // uPD7759 마스터 모드 흉내: 다음에 넣을 바이트
        private int feedStep, feedAddr, sampleNo;
        private bool updResetHigh = true;

        public Machine(byte[] rom, byte[] pcm)
        {
            Rom = rom; Pcm = pcm ?? new byte[0];
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            Upd.SetReset(true);
        }

        public void Play(int code)
        {
            var init = Log.Where(x => x.chip == 0).Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
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

        private long T1Period { get { return Math.Max(1, (long)(288.0 * (256 - t1) / OplClock * CpuClock)); } }
        private long T2Period { get { return Math.Max(1, (long)(1152.0 * (256 - t2) / OplClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            lastCycles = Cpu.Cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                updAcc += (now - lastCycles) * UpdClock / CpuClock;
                lastCycles = now;
                int n = (int)updAcc;
                if (n > 0)
                {
                    updAcc -= n;
                    bool before = Upd.Drq;
                    Upd.Advance(n);
                    if (Upd.Drq && !before) Feed();
                }
                if (now >= nextT1) { nextT1 += T1Period; if ((tCtl & 0x40) == 0) tFlags |= 0x40; }
                if (now >= nextT2) { nextT2 += T2Period; if ((tCtl & 0x20) == 0) tFlags |= 0x20; }
                if (tFlags != 0 && Cpu.Iff1)
                {
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted)
                {
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, Math.Min(end, Math.Min(nextT1, Math.Min(nextT2, Cpu.Cycles + 64))));
                    continue;
                }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                Cpu.Step();
            }
        }

        /// <summary>uPD7759 DRQ: 마스터 모드에서 칩이 읽을 ROM 바이트를 대신 넣는다.</summary>
        private void Feed()
        {
            int b;
            switch (feedStep)
            {
                case 0: b = 0xFF; break;                                       // 마지막 곡 번호: 슬레이브 모드 칩은 10h 이상이어야 계속 (ROM 0의 값이 아니라)
                case 1: b = 0; break;                                          // 빈 바이트
                case 2: b = PcmAt(5 + sampleNo * 2); feedAddr = b << 9; break;   // 주소 위
                case 3: b = PcmAt(6 + sampleNo * 2); feedAddr |= b << 1; break;  // 주소 아래
                default: b = PcmAt(feedAddr++); break;                          // 빈 바이트 (DUMMY2) · 블록 머리 · 니블 …
            }
            feedStep++;
            Upd.Data(b);
            UpdBytes++;
            Emit(1, 2, b);
        }

        private int PcmAt(int a) { return Pcm.Length > 0 ? Pcm[a % Pcm.Length] : 0; }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0xF000) return a < Rom.Length ? Rom[a] : (byte)0xFF;
            if (a < 0xF800) return Ram[a - 0xF000];
            if (a == 0xF800) { LatchReads++; return (byte)latch; }
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xF000 && a < 0xF800) Ram[a - 0xF000] = v;
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if (port == 0x00) return (byte)(tFlags != 0 ? 0x80 | tFlags : 0);
            return 0xFF;
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            switch (port)
            {
                case 0x00: oplAddr = v; return;
                case 0x20: OplWrite(oplAddr, v); return;
                case 0x40:
                    // 곡 번호를 포트에 놓고 START 0 → 1 (MAME snk68 D7759_write_port_0_w)
                    sampleNo = v;   // 칩에는 넣지 않는다 (재생기 칩은 데이터를 쌓아 DRQ마다 꺼내므로 한 바이트 밀린다)
                    bool idle = !Upd.Busy;
                    Upd.SetStart(false); Emit(1, 1, 0);
                    feedStep = 0;
                    Upd.SetStart(true); Emit(1, 1, 1);
                    if (idle && Upd.Busy) UpdStarts++;
                    return;
                case 0x80:
                    {
                        bool high = (v & 0x80) != 0;
                        if (high != updResetHigh) { updResetHigh = high; Upd.SetReset(high); Emit(1, 0, high ? 1 : 0); }
                        return;
                    }
            }
            OtherOuts.TryGetValue(port, out int c); OtherOuts[port] = c + 1;
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        private void OplWrite(int reg, int v)
        {
            if (reg >= 2 && reg <= 4 && TimerWrites.Count < 64) TimerWrites.Add((Cpu.Cycles, reg, v));
            switch (reg)
            {
                case 0x02: t1 = v; return;
                case 0x03: t2 = v; return;
                case 0x04:
                    if ((v & 0x80) != 0) { tFlags = 0; return; }
                    {
                        int old = tCtl;
                        tCtl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) nextT1 = Cpu.Cycles + T1Period; else if ((v & 1) == 0) nextT1 = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) nextT2 = Cpu.Cycles + T2Period; else if ((v & 2) == 0) nextT2 = long.MaxValue;
                        tFlags &= ~(v & 0x60);
                    }
                    return;
            }
            if (regs.TryGetValue(reg, out int prev) && prev == v) return;   // OPL은 같은 값 다시 쓰기가 아무 일 없음
            regs[reg] = v;
            Emit(0, reg, v);
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "SNK68 (hoot)", Notes = "hoot snk 3812" };
            w.SetClock(VgmWriter.ClockYm3812, OplClock);
            bool upd = Pcm.Length > 0 && Log.Any(x => x.chip == 1);
            if (upd) w.SetClock(VgmWriter.ClockUpd7759, UpdClock);   // bit31 0 = 슬레이브 모드 (보드가 ROM 바이트를 넣음)
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !upd) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write(0x5A, x.reg, x.val);
                else w.Write(0xB6, x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
