using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 메가드라이브 Z80 드라이버 (driver megadrive, type generic_z80 · iyinc): 게임 카트리지 ROM 안의 Z80 사운드 드라이버를 메가드라이브 사운드 쪽 흉내에서 돌린다 (68000은 흉내 내지 않음).
/// <para>기계: Z80 3.58MHz, 0000h~1FFFh = Z80 RAM 8KB (2000h~3FFFh 거울), 4000h~4003h = YM2612 (읽기 = 상태: 타이머 A · B 플래그 — 메가드라이브는 YM2612 IRQ가 없어 드라이버가 상태를 본다),
/// 6000h = 뱅크 (쓸 때마다 bit0이 9비트 뱅크로 밀려 들어감), 7F11h = PSG (SN76489), 8000h~FFFFh = 카트리지 ROM 32KB 창 (뱅크 × 8000h), 수직 귀선 60Hz = INT (IM 1 → 0038h).</para>
/// <para>hoot 옵션 (목록에서 읽은 뜻): topaddr = 드라이버가 있는 ROM 위치, data1 · size1 (data2 · size2 …) = 그것을 Z80 RAM 어디에 몇 바이트, reqaddr = 곡 번호를 쓸 Z80 RAM 주소,
/// trigadr · trigvalue = 그 뒤 "시작" 표시를 쓸 곳 · 값, stop = 정지 명령. 재생 = 드라이버를 올려 0.5초 돌린 뒤 곡 번호 (+ 표시)를 쓴다. 180초 → VGM (YM2612 + SN76489).</para>
/// </summary>
public static class HootMegaDrive
{
    public const double CpuClock = 3579545, YmClock = 7670453, PsgClock = 3579545;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        byte[] rom = null;
        var missing = new List<string>();
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") continue;
            var d = file(text);
            if (d == null) { missing.Add(text); continue; }
            long at = Math.Max(0, off);
            if (rom == null || rom.Length < at + d.Length) { var r = new byte[at + d.Length]; if (rom != null) Array.Copy(rom, r, rom.Length); rom = r; }
            Array.Copy(d, 0, rom, at, d.Length);
        }
        var m = new Machine(rom ?? new byte[0x8000]);
        m.Missing.AddRange(missing);
        long top = e.Option("topaddr", 0);
        // 68000이 보통 Z80 뱅크를 사운드 데이터 쪽에 맞춰 둔다: 처음 뱅크 = 드라이버가 있는 32KB 칸 (verytex: 8000h 창의 곡 표를 읽음)
        m.Bank = (int)((top >> 15) & 0x1FF);
        for (int k = 1; k <= 4; k++)
        {
            string dk = "data" + k, sk = "size" + k;
            if (!e.Options.ContainsKey(dk) || !e.Options.ContainsKey(sk)) continue;
            int dst = e.Option(dk, 0) & 0x1FFF, size = e.Option(sk, 0);
            // data1 = topaddr부터, data2 · data3 = 앞 조각에 이어서
            for (int i = 0; i < size && dst + i < 0x2000; i++) m.Ram[dst + i] = top + i < m.Rom.Length ? m.Rom[top + i] : (byte)0;
            top += size;
        }
        // reqaddr가 없으면 (iyinc · Falcom 드라이버 등) 1FFFh: lordmon 본 고리가 LD A,(1FFFh) · OR A · CALL NZ로 명령을 기다린다
        m.ReqAddr = e.Option("reqaddr", 0x1FFF) & 0x1FFF;
        m.TrigAddr = e.Options.ContainsKey("trigadr") ? e.Option("trigadr", 0) & 0x1FFF : -1;
        m.TrigValue = e.Option("trigvalue", 1) & 0xFF;
        m.SubTrigAddr = e.Options.ContainsKey("trigsubadr") ? e.Option("trigsubadr", 0) & 0x1FFF : -1;
        m.SubTrigValue = e.Option("trigsubval", 1) & 0xFF;
        m.RunSeconds(0.5);
        return m;
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom;
        public readonly byte[] Ram = new byte[0x2000];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        /// <summary>칩 쓰기 (시간 = 44100Hz 샘플, 재생부터; chip 0 = YM2612 앞 · 1 = 뒤, 2 = PSG).</summary>
        public readonly List<(double t, int chip, int reg, int val)> Log = new List<(double, int, int, int)>();
        public int ReqAddr = -1, TrigAddr = -1, TrigValue = 1, SubTrigAddr = -1, SubTrigValue = 1;
        public int Frames;

        private int bank, addr0, addr1;
        public int Bank { get { return bank; } set { bank = value & 0x1FF; } }
        private long playStart = -1, nextFrame;
        private bool vblank;
        private int timerA, timerB, timerCtl, timerFlags;
        private long nextTimerA = long.MaxValue, nextTimerB = long.MaxValue;
        private readonly long frameCycles = (long)(CpuClock / 60);

        public Machine(byte[] rom)
        {
            Rom = rom;
            Cpu = new Z80(this);
            Cpu.PC = 0; Cpu.SP = 0x1FFF; Cpu.Im = 1;
            nextFrame = frameCycles;
        }

        public void Play(int code)
        {
            var init = Log.Select(x => (0.0, x.chip, x.reg, x.val)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
            if (ReqAddr >= 0) Ram[ReqAddr] = (byte)code;
            if (TrigAddr >= 0) Ram[TrigAddr] = (byte)TrigValue;
            if (SubTrigAddr >= 0) Ram[SubTrigAddr] = (byte)SubTrigValue;
        }

        private long TimerAPeriod { get { return Math.Max(1, (long)(144.0 * (1024 - timerA) / YmClock * CpuClock)); } }
        private long TimerBPeriod { get { return Math.Max(1, (long)(2304.0 * (256 - timerB) / YmClock * CpuClock)); } }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                long now = Cpu.Cycles;
                if (now >= nextFrame) { nextFrame += frameCycles; vblank = true; Frames++; }
                if (now >= nextTimerA) { nextTimerA += TimerAPeriod; if ((timerCtl & 4) != 0) timerFlags |= 1; }
                if (now >= nextTimerB) { nextTimerB += TimerBPeriod; if ((timerCtl & 8) != 0) timerFlags |= 2; }
                if (vblank && Cpu.Iff1)
                {
                    vblank = false;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
                    Write(Cpu.SP, (byte)Cpu.PC); Write((Cpu.SP + 1) & 0xFFFF, (byte)(Cpu.PC >> 8));
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted)
                {
                    long next = Math.Min(end, Math.Min(nextFrame, Math.Min(nextTimerA, nextTimerB)));
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, next);
                    continue;
                }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x4000) return Ram[a & 0x1FFF];
            if (a < 0x6000) return (byte)(timerFlags & 3);   // YM2612 상태 (바쁘지 않음)
            if (a < 0x8000) return 0xFF;
            long at = ((long)bank << 15) + (a - 0x8000);
            return at < Rom.Length ? Rom[at] : (byte)0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a < 0x4000) { Ram[a & 0x1FFF] = v; return; }
            if (a < 0x6000)
            {
                switch (a & 3)
                {
                    case 0: addr0 = v; break;
                    case 1: YmWrite(0, addr0, v); break;
                    case 2: addr1 = v; break;
                    case 3: YmWrite(1, addr1, v); break;
                }
                return;
            }
            if (a < 0x6100) { bank = ((bank >> 1) | ((v & 1) << 8)) & 0x1FF; return; }
            if (a == 0x7F11 || a == 0x7F13 || a == 0x7F15 || a == 0x7F17) { Emit(2, 0, v); return; }
        }

        private void Emit(int chip, int reg, int val)
        {
            if (playStart < 0) playStart = Cpu.Cycles;
            Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), chip, reg, val));
        }

        private void YmWrite(int port, int reg, int v)
        {
            if (port == 0)
            {
                switch (reg)
                {
                    case 0x24: timerA = (timerA & 3) | (v << 2); return;
                    case 0x25: timerA = (timerA & 0x3FC) | (v & 3); return;
                    case 0x26: timerB = v; return;
                    case 0x27:
                        {
                            int old = timerCtl;
                            timerCtl = v;
                            if ((v & 0x10) != 0) timerFlags &= ~1;
                            if ((v & 0x20) != 0) timerFlags &= ~2;
                            if ((v & 1) != 0 && (old & 1) == 0) nextTimerA = Cpu.Cycles + TimerAPeriod; else if ((v & 1) == 0) nextTimerA = long.MaxValue;
                            if ((v & 2) != 0 && (old & 2) == 0) nextTimerB = Cpu.Cycles + TimerBPeriod; else if ((v & 2) == 0) nextTimerB = long.MaxValue;
                            Emit(0, reg, v & 0xC0);
                            return;
                        }
                }
            }
            Emit(port, reg, v);
        }

        public byte In(int port) { return 0xFF; }
        public void Out(int port, byte v) { }

        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "Mega Drive (hoot)", Notes = "hoot megadrive" };
            w.SetClock(VgmWriter.ClockYm2612, YmClock);
            if (Log.Any(x => x.chip == 2)) { w.SetClock(VgmWriter.ClockSn76489, PsgClock); w.SetHeaderByte(0x28, 0x09); w.SetHeaderByte(0x2A, 16); }   // 세가 PSG: 되먹임 0009h · 시프트 16
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                if (x.chip == 2) w.Write(0x50, x.val); else w.Write((byte)(0x52 + x.chip), x.reg, x.val);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
