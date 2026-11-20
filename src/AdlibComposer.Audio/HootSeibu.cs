using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 세이부 사운드 시스템 (driver seibu, type raiden: Raiden · Toki no Mahjong Paradise · Heated Barrel · Seibu Cup Soccer): 사운드 CPU를 흉내 낸다.
/// <para>기계 (MAME seibusound.cpp seibu_sound_map + 디스어셈블): Z80 3.58MHz, 0000h~7FFFh ROM (hoot ROM은 SEI80BU 암호를 푼 것), 8000h~FFFFh = ROM 뒤 32KB, 2000h~27FFh RAM,
/// 4001h 쓰기 = IRQ 둘 다 지움, 4002h = RST 10h 받음, 4003h = RST 18h 받음 (지움), 4007h = 뱅크 (64KB ROM은 하나), 4008h · 4009h = YM3812 (3.58MHz),
/// 4010h · 4011h 읽기 = 명령 두 바이트 (메인 → 사운드), 4012h = 메인이 답을 아직 안 읽음 (bit0 — 늘 0), 4013h = 코인, 4018h · 4019h · 4000h = 답 (버림), 6000h = OKIM6295 (1MHz, 핀7 높음, ROM = pcm).</para>
/// <para>인터럽트 (IM 0, 벡터 = 둘의 AND): YM3812 IRQ = RST 10h (D7h, 드라이버는 4h에 42h를 써서 T1 깃발을 지움 — MAME fmopl), 명령 = RST 18h (DFh). 명령은 낱말: 4011h = 동작 (80h 곡 시작 · 81h 멈춤 · 90h 리셋), 4010h = 번호.
/// 곡: 부팅 1초 뒤 4010h = 곡 코드 · 4011h = 80h로 RST 18h, 180초를 YM3812 + OKIM6295 VGM으로.</para>
/// </summary>
public static class HootSeibu
{
    public const double CpuClock = 3579545, OplClock = 3579545, OkiClock = 1000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "seibu", StringComparison.OrdinalIgnoreCase)) return false;
        return (type ?? "").Trim().ToLowerInvariant() == "raiden";
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
        var pcm = new List<(long off, byte[] d)>();
        var missing = new List<string>();
        var unsupported = new HashSet<string>();
        bool any = false;
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code" && type != "pcm") { unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            if (type == "pcm") { pcm.Add((off, d)); continue; }
            if (off < 0 || off >= 0x10000) { unsupported.Add(type + " offset " + off); continue; }
            Array.Copy(d, 0, rom, off, (int)Math.Min(d.Length, 0x10000 - off));
            any = true;
        }
        if (!any) missing.Add("(code)");
        var p = new byte[pcm.Count == 0 ? 0 : (int)pcm.Max(x => x.off + x.d.Length)];
        foreach (var (off, d) in pcm) Array.Copy(d, 0, p, off, d.Length);
        var m = new Machine(rom, p);
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, Pcm;
        public readonly byte[] Ram = new byte[0x800];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기: chip 0 = YM3812, 1 = OKIM6295 (명령 바이트).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int Irqs10, Irqs18, LatchReads;
        public readonly Dictionary<int, int> OtherWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>();

        private int latch0, latch1, ymAddr;
        private bool rst18;
        private long playStart = -1;
        private int t1, t2, ctl, flags;
        private long next1 = long.MaxValue, next2 = long.MaxValue;
        private readonly int[] last = Enumerable.Repeat(-1, 0x100).ToArray();

        public Machine(byte[] rom, byte[] pcm)
        {
            Rom = rom; Pcm = pcm;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 0;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            latch0 = code & 0xFF; latch1 = code > 0xFF ? (code >> 8) & 0xFF : 0x80;   // 4011h = 동작 (80h = 곡 시작, 81h = 멈춤 …), 4010h = 번호
            rst18 = true;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

        private long Period1 { get { return Math.Max(1, (long)(288.0 * (256 - t1) / OplClock * CpuClock)); } }
        private long Period2 { get { return Math.Max(1, (long)(1152.0 * (256 - t2) / OplClock * CpuClock)); } }
        private bool Rst10 { get { return (flags & 0x80) != 0; } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                // MAME fmopl: 넘치면 깃발은 늘 서고, 가리지 않은 깃발이 있으면 IRQ (bit7)
                if (now >= next1) { next1 += Period1; flags |= 0x40; if ((flags & ~ctl & 0x60) != 0) flags |= 0x80; }
                if (now >= next2) { next2 += Period2; flags |= 0x20; if ((flags & ~ctl & 0x60) != 0) flags |= 0x80; }
                if ((Rst10 || rst18) && Cpu.Iff1)
                {
                    int vec = (Rst10 ? 0xD7 : 0xFF) & (rst18 ? 0xDF : 0xFF);   // IM 0: RST 10h / 18h (둘이면 10h)
                    if (vec == 0xD7) Irqs10++; else Irqs18++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = vec & 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted && !Rst10 && !rst18)
                {
                    long next = Math.Min(next1, next2);
                    if (next >= end) { Cpu.Cycles = end; break; }
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 4, next);
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x2000 || a >= 0x8000) return Rom[a];
            if (a < 0x2800) return Ram[a - 0x2000];
            switch (a)
            {
                case 0x4008: return (byte)flags;
                case 0x4009: return 0;
                case 0x4010: LatchReads++; return (byte)latch0;
                case 0x4011: return (byte)latch1;
                case 0x4012: return 0;   // 답 기다림 없음
                case 0x4013: return 0;
                case 0x6000: return 0;   // OKIM6295 상태: 쉼
            }
            if (a < 0x4000) return Rom[a];
            OtherReads.TryGetValue(a, out int c); OtherReads[a] = c + 1;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0x2000 && a < 0x2800) { Ram[a - 0x2000] = v; return; }
            if (a < 0x2000 || a >= 0x8000) return;
            switch (a)
            {
                case 0x4001: rst18 = false; return;   // MAME VECTOR_INIT (YM 쪽은 줄을 따름)
                case 0x4002: return;
                case 0x4003: rst18 = false; return;
                case 0x4000: case 0x4007: case 0x4018: case 0x4019: case 0x401B: return;
                case 0x4008: ymAddr = v; return;
                case 0x4009: OplWrite(ymAddr, v); return;
                case 0x6000: Emit(1, 0, v); return;
            }
            OtherWrites.TryGetValue(a, out int c); OtherWrites[a] = c + 1;
        }

        public byte In(int p) { return 0xFF; }
        public void Out(int p, byte v) { }

        private void OplWrite(int reg, int v)
        {
            switch (reg)
            {
                case 0x02: t1 = v; return;
                case 0x03: t2 = v; return;
                case 0x04:
                    {
                        if ((v & 0x80) != 0) { flags = 0; return; }
                        // bit6 · bit5 = 가림 + 그 깃발 지움 (드라이버가 42h로 T1을 받음), 남은 깃발이 다 가려지면 IRQ 내림
                        flags &= ~(v & 0x60);
                        if ((flags & ~v & 0x60) == 0) flags &= 0x7F;
                        int old = ctl;
                        ctl = v;
                        if ((v & 1) != 0 && (old & 1) == 0) next1 = Cpu.Cycles + Period1; else if ((v & 1) == 0) next1 = long.MaxValue;
                        if ((v & 2) != 0 && (old & 2) == 0) next2 = Cpu.Cycles + Period2; else if ((v & 2) == 0) next2 = long.MaxValue;
                        return;
                    }
            }
            bool keep = (reg >= 0xB0 && reg <= 0xB8) || reg == 0xBD;
            if (!keep) { if (last[reg] == v) return; last[reg] = v; }
            Emit(0, reg, v);
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Seibu Sound System (hoot)", Notes = "hoot seibu raiden" };
            w.SetClock(VgmWriter.ClockYm3812, OplClock);
            bool oki = Pcm.Length > 0 && Log.Any(x => x.chip == 1);
            if (oki)
            {
                w.SetClock(VgmWriter.ClockOkim6295, OkiClock, 0x80000000u);   // 핀7 높음
                var cmd = new byte[7 + 8 + Pcm.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8B;
                int size = 8 + Pcm.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int len = Pcm.Length;
                cmd[7] = (byte)len; cmd[8] = (byte)(len >> 8); cmd[9] = (byte)(len >> 16); cmd[10] = (byte)(len >> 24);
                Array.Copy(Pcm, 0, cmd, 15, Pcm.Length);
                w.Write(cmd);
            }
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                if (x.chip == 1 && !oki) continue;
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 0) w.Write((byte)0x5A, x.reg, x.val);
                else w.Write((byte)0xB8, 0, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
