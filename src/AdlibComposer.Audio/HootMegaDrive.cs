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
        if (string.Equals(e.Type, "koei", StringComparison.OrdinalIgnoreCase))
        {
            int maxCode = e.Titles.Count > 0 ? e.Titles.Max(t => t.Code) : 0;
            return Koei.Boot(m.Rom, maxCode, missing);
        }
        long top = e.Option("topaddr", 0);
        // 68000이 보통 Z80 뱅크를 사운드 데이터 쪽에 맞춰 둔다: 처음 뱅크 = 드라이버가 있는 32KB 칸 (verytex: 8000h 창의 곡 표를 읽음)
        m.Bank = (int)((top >> 15) & 0x1FF);
        bool anyData = false;
        for (int k = 1; k <= 4; k++) anyData |= e.Options.ContainsKey("data" + k) && e.Options.ContainsKey("size" + k);
        if (!anyData)
        {
            // data · size가 없는 항목 (castlillj): topaddr부터 드라이버 칸 전부 (1c00 = 1C00h부터는 변수 칸이라 그 앞까지)
            int size = string.Equals(e.Type, "1c00", StringComparison.OrdinalIgnoreCase) ? 0x1C00 : 0x2000;
            for (int i = 0; i < size; i++) m.Ram[i] = top + i < m.Rom.Length ? m.Rom[top + i] : (byte)0;
        }
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
        // 1c00 = 세가 SMPS Z80 (변수 칸 1C00h~): 소리 큐 1C0Ah~1C0Ch (1C09h = 지금 소리), 68000이 곡 번호 (81h~)를 1C0Ah에 쓴다
        // (ryukoken 68000 코드: move.b d7,$A01C0A). 곡 표 위치 1C02h~1C03h도 68000이 쓴다 (ryukoken = 8000h, 뱅크 창) → 비어 있으면 8000h.
        bool smps = string.Equals(e.Type, "1c00", StringComparison.OrdinalIgnoreCase);
        m.ReqAddr = e.Option("reqaddr", smps ? 0x1C0A : 0x1FFF) & 0x1FFF;
        if (smps)
        {
            int ptr = m.Ram[0x1C02] | (m.Ram[0x1C03] << 8);
            if (ptr == 0 || ptr == 0xFFFF) { m.Ram[0x1C02] = 0x00; m.Ram[0x1C03] = 0x80; }
        }
        m.TrigAddr = e.Options.ContainsKey("trigadr") ? e.Option("trigadr", 0) & 0x1FFF : -1;
        m.TrigValue = e.Option("trigvalue", 1) & 0xFF;
        m.SubTrigAddr = e.Options.ContainsKey("trigsubadr") ? e.Option("trigsubadr", 0) & 0x1FFF : -1;
        m.SubTrigValue = e.Option("trigsubval", 1) & 0xFF;
        m.RunSeconds(0.5);
        return m;
    }

    /// <summary>
    /// 코에이 메가드라이브 사운드 (driver megadrive, type koei — hoot 옵션 없음): 카트리지에서 직접 찾는다.
    /// <para>드라이버: 68000 적재 루틴 <c>lea end(pc),a0 · lea start(pc),a1 · suba.l a1,a0 · … move.b (a1)+,(a0)+ · dbf</c> (41FA · 43FA · 91C9)이 가리키는 ROM 조각 → Z80 RAM 0000h.
    /// 두 세대: F3 31 00 20 C3 E0 00 (옛, 11게임) · F3 31 00 20 CD 00 05 (새: r3k2 · uw · gemfi).</para>
    /// <para>우편함 (68000 코드의 가장 작은 <c>movea.l #$A000xx,a1</c> = 바탕: 옛 07h · 새 09h): 바탕 = 명령, +1 = 인자, +3~+5 = 24비트 ROM 주소 (위 바이트 먼저), +6 ← 02h (Z80이 받으면 지움).
    /// 명령 (MD 버스를 흉내 낸 68000 부팅 추적으로 확인, --hootkoeitrace): 0 = 곡 주소 정하기, 1 = (새 세대 부팅 때 C8h), 2 = 멈춤, 3 = 연주 (인자 1).</para>
    /// <para>곡 표: 68000 코드 안의 ROM 주소 (32비트) 줄 — 게임마다 모양이 달라 (곡 머리 · 채널 기록 · 곡 묶음 맨 앞) 길이가 hoot 곡 코드 최대값 ~ +2인 줄을 모두 후보로 두고,
    /// 후보마다 첫 곡을 3초 돌려 키온이 가장 많은 줄을 고른다. 곡 코드 n = 표의 n−1번째.</para>
    /// </summary>
    public static class Koei
    {
        public static Machine Boot(byte[] rom, int maxCode, List<string> missing = null)
        {
            var (start, length) = FindDriver(rom);
            int mailbox = FindMailbox(rom, start);
            var tables = FindTables(rom, maxCode);
            Machine best = null;
            int bestKeys = -1;
            foreach (var t in tables.Count > 0 ? tables : new List<long[]> { new long[0] })
            {
                var m = Create(rom, start, length, mailbox, t);
                if (tables.Count <= 1 || t.Length == 0) { best = m; break; }
                var probe = Create(rom, start, length, mailbox, t);
                probe.Play(1);
                probe.RunSeconds(3);
                int keys = probe.Log.Count(x => x.chip == 0 && x.reg == 0x28 && (x.val & 0xF0) != 0) + probe.Log.Count(x => x.chip == 2 && (x.val & 0x90) == 0x90 && (x.val & 0x0F) != 0x0F);
                if (keys > bestKeys) { bestKeys = keys; best = m; }
            }
            if (missing != null) best.Missing.AddRange(missing);
            if (start < 0) best.Missing.Add("(코에이 드라이버를 찾지 못함)");
            if (tables.Count == 0) best.Missing.Add("(코에이 곡 표를 찾지 못함)");
            return best;
        }

        private static Machine Create(byte[] rom, int start, int length, int mailbox, long[] table)
        {
            var m = new Machine(rom) { KoeiMailbox = mailbox, KoeiTable = table };
            if (start >= 0) Array.Copy(rom, start, m.Ram, 0, Math.Min(length, 0x2000));
            // 새 세대: 부팅 때 68000이 명령 1 · 인자 C8h를 보낸다
            if (start >= 0 && rom[start + 4] == 0xCD) m.KoeiSend(1, 0xC8, 0);
            m.RunSeconds(0.5);
            return m;
        }

        /// <summary>68000 적재 루틴이 Z80 RAM으로 옮기는 ROM 조각 (시작, 길이). 없으면 (−1, 0).</summary>
        public static (int start, int length) FindDriver(byte[] rom)
        {
            for (int p = 0; p + 12 < rom.Length && p < 0x10000; p += 2)
            {
                if (rom[p] != 0x41 || rom[p + 1] != 0xFA || rom[p + 4] != 0x4E || rom[p + 5] != 0x71 || rom[p + 6] != 0x43 || rom[p + 7] != 0xFA
                    || rom[p + 10] != 0x4E || rom[p + 11] != 0x71 || rom[p + 12] != 0x91 || rom[p + 13] != 0xC9) continue;
                int end = p + 2 + (short)(rom[p + 2] << 8 | rom[p + 3]);
                int start = p + 8 + (short)(rom[p + 8] << 8 | rom[p + 9]);
                if (start <= 0 || end <= start || end > rom.Length) continue;
                return (start, end - start + 1);   // dbf: 차이 + 1 바이트
            }
            return (-1, 0);
        }

        /// <summary>우편함 바탕: 드라이버 앞 68000 코드의 movea.l #$A000xx,a1 (227C 00A0 00xx) 중 가장 작은 xx. 없으면 세대로 (옛 07h · 새 09h).</summary>
        public static int FindMailbox(byte[] rom, int driverStart)
        {
            int best = int.MaxValue;
            int limit = driverStart > 0 ? driverStart : Math.Min(rom.Length, 0x10000);
            for (int p = 0; p + 6 <= limit; p += 2)
                if (rom[p] == 0x22 && rom[p + 1] == 0x7C && rom[p + 2] == 0x00 && rom[p + 3] == 0xA0 && rom[p + 4] == 0x00 && rom[p + 5] > 0 && rom[p + 5] < 0x20)
                    best = Math.Min(best, rom[p + 5]);
            if (best != int.MaxValue) return best;
            return driverStart >= 0 && rom[driverStart + 4] == 0xCD ? 9 : 7;
        }

        /// <summary>곡 표 후보: 서로 다른 ROM 주소 (32비트, 서로 20000h 안) 줄 가운데 길이가 maxCode ~ maxCode + 2인 것 (앞에서부터 maxCode개).</summary>
        public static List<long[]> FindTables(byte[] rom, int maxCode)
        {
            var list = new List<long[]>();
            if (maxCode < 1) return list;
            long Long(int p) { return (long)rom[p] << 24 | (long)rom[p + 1] << 16 | (long)rom[p + 2] << 8 | rom[p + 3]; }
            for (int i = 0; i + 16 <= rom.Length;)
            {
                var vals = new List<long>();
                int j = i;
                while (j + 4 <= rom.Length)
                {
                    long v = Long(j);
                    if (v < 0x200 || v >= rom.Length || (vals.Count > 0 && Math.Abs(v - vals[0]) > 0x20000) || vals.Contains(v)) break;
                    vals.Add(v); j += 4;
                }
                if (vals.Count >= 4)
                {
                    if (vals.Count >= maxCode && vals.Count <= maxCode + 2) list.Add(vals.Take(maxCode).ToArray());
                    i = j;
                }
                else i += 2;
            }
            return list;
        }
    }

    public sealed class Machine : IZ80Bus
    {
        /// <summary>코에이: 우편함 바탕 (−1 = 코에이 아님), 곡 표 (곡 코드 n = n−1번째 ROM 주소).</summary>
        public int KoeiMailbox = -1;
        public long[] KoeiTable = new long[0];
        private readonly Queue<(int cmd, int arg, long addr)> koeiQueue = new Queue<(int, int, long)>();

        /// <summary>코에이 명령 하나를 줄에 (Z80이 앞 명령을 받은 뒤 우편함에 쓴다).</summary>
        public void KoeiSend(int cmd, int arg, long addr) { koeiQueue.Enqueue((cmd, arg, addr)); KoeiPump(); }

        private void KoeiPump()
        {
            if (KoeiMailbox < 0 || koeiQueue.Count == 0) return;
            int b = KoeiMailbox;
            if (Ram[b + 6] == 2) return;   // 앞 명령을 아직 안 받음
            var (cmd, arg, addr) = koeiQueue.Dequeue();
            Ram[b + 1] = (byte)arg;
            Ram[b + 3] = (byte)(addr >> 16); Ram[b + 4] = (byte)(addr >> 8); Ram[b + 5] = (byte)addr;
            Ram[b] = (byte)cmd;
            Ram[b + 6] = 2;
        }

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
            if (KoeiMailbox >= 0)
            {
                // 68000 순서 (uw 추적): 멈춤 → 곡 주소 → 연주
                if (code < 1 || code > KoeiTable.Length) return;
                KoeiSend(2, 0, 0);
                KoeiSend(0, 0, KoeiTable[code - 1]);
                KoeiSend(3, 1, 0);
                return;
            }
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
                if (now >= nextFrame) { nextFrame += frameCycles; vblank = true; Frames++; KoeiPump(); }
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
