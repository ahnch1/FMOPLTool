using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 코나미 Z80 + AY-3-8910 × 2 사운드 보드 (driver konami, type scramble: Scramble · Super Cobra · Amidar · The End · Strategy X · Turtles · Super Fighter X …,
/// type timeplt: Time Pilot · Tutankham · Jungler · Chiki Chiki Boys): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME galaxian / scramble sound · timeplt_audio): Z80 1.79MHz, AY × 2 1.79MHz. 명령 = AY #1 포트 A (레지스터 14), AY #1 포트 B = 코나미 타이머 (표 [(사이클 ÷ 512) mod 10]),
/// 메인이 쓰면 IRQ (IM 1). scramble = ROM 0000h~, RAM 8000h~83FFh (거울 ~9FFFh), I/O 포트 bit6 = AY #1 주소 · bit7 = 자료, bit4 = AY #2 주소 · bit5 = 자료 (MAME konami_ay8910_r/w).
/// frogger (Frogger · Hustler, MAME galaxian frogger_sound_map): ROM 0000h~1FFFh (앞 800h는 bit0 · bit1이 바뀌어 있음 → 되돌림), RAM 4000h~43FFh (거울 ~5FFFh), 6000h~ 필터, AY 하나 — I/O bit6 = 자료 (읽기도), bit7 = 주소.
/// timeplt = ROM 0000h~, RAM 3000h~33FFh (거울 ~3FFFh — locomotn 배치 Jungler는 RAM 2000h~ · 3000h = 필터, ROM이 가리키는 쪽으로 고름), 4000h AY #1 자료 · 5000h 주소 · 6000h AY #2 자료 · 7000h 주소 (각 거울 ~FFFh), 8000h~ = RC 필터 (버림).</para>
/// <para>곡: 부팅 1초 뒤 명령, 180초를 AY × 2 VGM으로.</para>
/// </summary>
public static class HootKonamiAy
{
    public const double CpuClock = 14318181 / 8.0, AyClock = 14318181 / 8.0;
    public const double DefaultSeconds = 180, DefaultFade = 4;
    private static readonly int[] Timer = { 0x00, 0x10, 0x20, 0x30, 0x40, 0x90, 0xA0, 0xB0, 0xA0, 0xD0 };

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "konami", StringComparison.OrdinalIgnoreCase)) return false;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "scramble" || t == "timeplt" || t == "pooyan" || t == "frogger";
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
        var rom = new byte[0x10000];
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        int romEnd = 0;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { unsupported.Add(type); continue; }
            if (off < 0 || off >= 0x3000) { unsupported.Add(type + " offset " + off); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            int n = (int)Math.Min(d.Length, 0x3000 - off);
            Array.Copy(d, 0, rom, off, n);
            romEnd = (int)Math.Max(romEnd, off + n);
            any = true;
        }
        if (!any) missing.Add("(code)");
        string ty = (e.Type ?? "").Trim().ToLowerInvariant();
        bool timeplt = ty == "timeplt" || ty == "pooyan";   // pooyan = timeplt 보드 (Pooyan · Loco-Motion · Tactician)
        if (ty == "frogger") for (int i = 0; i < 0x800; i++) rom[i] = (byte)((rom[i] & 0xFC) | ((rom[i] & 1) << 1) | ((rom[i] >> 1) & 1));   // MAME init_frogger
        var m = new Machine(rom, timeplt) { Frogger = ty == "frogger" };
        m.ClearBit7 = ty == "pooyan";   // Pooyan 계열: 드라이버가 bit7 = 멈춤으로 봄 — hoot 코드 (86h · 92h …)는 bit7을 빼고 보냄
        if (timeplt)
        {
            // locomotn 배치 (Jungler · Loco-Motion): ROM ~1FFFh, RAM 2000h~23FFh, 3000h~ = 필터 — ROM이 가리키는 RAM 쪽으로 고름
            int r2 = 0, r3 = 0;
            for (int i = 0; i + 2 < romEnd; i++)
            {
                if (rom[i] != 0x21 && rom[i] != 0x31 && rom[i] != 0x32 && rom[i] != 0x3A && rom[i] != 0x22 && rom[i] != 0x2A) continue;
                int hi = rom[i + 2];
                if (hi >= 0x20 && hi < 0x24) r2++; else if (hi >= 0x30 && hi < 0x34) r3++;
            }
            m.RamBase = r2 > r3 ? 0x2000 : 0x3000;
        }
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom;
        public readonly bool TimePilot;
        public bool ClearBit7;
        /// <summary>frogger 배치 (AY 하나, RAM 4000h).</summary>
        public bool Frogger;
        public readonly byte[] Ram = new byte[0x400];
        /// <summary>timeplt 배치의 RAM 자리: 3000h (Time Pilot · Tutankham) 또는 2000h (locomotn: Jungler).</summary>
        public int RamBase = 0x3000;
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>AY 쓰기: chip 0 = AY #1, 1 = AY #2.</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch;
        private bool irqLine;
        private readonly int[] ayAddr = new int[2];
        private readonly int[,] ayReg = new int[2, 16];
        private readonly int[,] last = new int[2, 16];
        private long playStart = -1;

        public Machine(byte[] rom, bool timeplt)
        {
            Rom = rom; TimePilot = timeplt;
            for (int c = 0; c < 2; c++) for (int r = 0; r < 16; r++) last[c, r] = -1;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            latch = ClearBit7 ? code & 0x7F : code & 0xFF;
            irqLine = true;
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
                if (irqLine && Cpu.Iff1)
                {
                    irqLine = false;   // 받으면 내려감 (hold)
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted && !irqLine) { Cpu.Cycles = end; break; }
                Cpu.Step();
            }
        }

        private int AyRead(int chip)
        {
            int r = ayAddr[chip];
            if (chip == 0 && r == 14) { LatchReads++; return latch; }
            if (chip == 0 && r == 15)
            {
                int t = Timer[(int)(Cpu.Cycles / 512 % 10)];
                return Frogger ? (t & 0xD7) | ((t & 8) << 2) | ((t & 0x20) >> 2) : t;   // frogger: bit3 · bit5 바꿈 (MAME frogger_sound_timer_r)
            }
            return ayReg[chip, r];
        }

        private void AyWrite(int chip, int v)
        {
            int r = ayAddr[chip];
            ayReg[chip, r] = v;
            if (r >= 14) return;
            if (r != 13 && last[chip, r] == v) return;   // 같은 값 다시 쓰기는 버림 (엔벨로프 모양 13은 다시 시작이라 늘)
            last[chip, r] = v;
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, r, v));
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (TimePilot && a >= RamBase && a < RamBase + 0x1000) return Ram[a & 0x3FF];
            if (Frogger)
            {
                if (a < 0x2000) return Rom[a];
                if (a >= 0x4000 && a < 0x6000) return Ram[a & 0x3FF];
                OtherReads.TryGetValue(a, out int c0); OtherReads[a] = c0 + 1;
                return 0xFF;
            }
            if (a < 0x3000) return Rom[a];
            if (TimePilot)
            {
                if (a < 0x4000) return 0xFF;
                if (a < 0x5000) return (byte)AyRead(0);
                if (a >= 0x6000 && a < 0x7000) return (byte)AyRead(1);
            }
            else if (a >= 0x8000 && a < 0xA000) return Ram[a & 0x3FF];
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (TimePilot && a >= RamBase && a < RamBase + 0x1000) { Ram[a & 0x3FF] = v; return; }
            if (Frogger)
            {
                if (a >= 0x4000 && a < 0x6000) Ram[a & 0x3FF] = v;
                return;   // ROM · 6000h~ 필터
            }
            if (a < 0x4000 && (TimePilot || a < 0x3000)) return;   // ROM · 필터 (locomotn 3000h~)
            if (TimePilot)
            {
                if (a < 0x8000)
                {
                    switch (a >> 12)
                    {
                        case 4: AyWrite(0, v); return;
                        case 5: ayAddr[0] = v & 15; return;
                        case 6: AyWrite(1, v); return;
                        case 7: ayAddr[1] = v & 15; return;
                    }
                }
                return;   // 8000h~ = RC 필터
            }
            if (a >= 0x8000 && a < 0x9000) { Ram[a & 0x3FF] = v; return; }
            if (a >= 0x9000 && a < 0xA000) return;   // RC 필터 (읽기는 RAM 거울)
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if (TimePilot) return 0xFF;
            if (Frogger) return (byte)((port & 0x40) != 0 ? AyRead(0) : 0xFF);
            int v = 0xFF;
            if ((port & 0x80) != 0) v &= AyRead(0);
            if ((port & 0x20) != 0) v &= AyRead(1);
            return (byte)v;
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            if (TimePilot) return;
            if (Frogger) { if ((port & 0x40) != 0) AyWrite(0, v); else if ((port & 0x80) != 0) ayAddr[0] = v & 15; return; }   // MAME frogger_ay8910_w
            // MAME konami_ay8910_w: bit6 · bit7 = AY #1 (명령 · 타이머가 있는 칩) 주소 · 자료, bit4 · bit5 = AY #2 — 주소가 자료보다 먼저
            if ((port & 0x40) != 0) ayAddr[0] = v & 15; else if ((port & 0x80) != 0) AyWrite(0, v);
            if ((port & 0x10) != 0) ayAddr[1] = v & 15; else if ((port & 0x20) != 0) AyWrite(1, v);
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = (TimePilot ? "Konami Time Pilot" : Frogger ? "Konami Frogger" : "Konami Scramble") + " (hoot)", Notes = "hoot konami" };
            w.SetClock(VgmWriter.ClockAy8910, AyClock, Frogger ? 0u : 0x40000000u);   // bit30 = AY 둘
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(0xA0, x.reg | (x.chip << 7), x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
