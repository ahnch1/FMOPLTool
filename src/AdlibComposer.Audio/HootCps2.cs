using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 캡콤 QSound 보드 (driver capcom, type cps2 · cps1qs · zn): QSound 사운드 보드를 흉내 내고 Z80 사운드 ROM을 돌린다.
/// <para>기계 (MAME cps2 · cps1 qsound_sub_map, zn qsound_map, 사운드 ROM을 읽어 확인): Z80 8MHz, 0000h~7FFFh = ROM, 8000h~BFFFh = 뱅크 (D003h 아래 4비트 → ROM 8000h + n × 4000h),
/// C000h~CFFFh = 68000과 같이 쓰는 RAM (ZN은 그냥 RAM), D000h · D001h = QSound 데이터 위 · 아래 바이트, D002h = 레지스터 (쓰면 기록), D007h 읽기 = 준비 (80h), F000h~FFFFh = RAM.
/// IRQ = 250Hz (IM 1).</para>
/// <para>CPS-2 · CPS-1 QSound 명령: 부팅 핸드셰이크 = Z80이 CFFFh ← 77h, 68000이 FFh로 답함. 68000이 C000h~C00Eh에 16바이트 묶음을 쓰고 C00Fh ← 00h (드라이버가 큐로 옮기고 FFh로), CFFDh = 88h (68000 준비).</para>
/// <para>CPS-1 QSound (wof · dino · punisher · slammast): Z80 ROM 0000h~7FFFh가 Kabuki로 암호화 — 명령 바이트와 자료 바이트가 다르게 풀린다 (<see cref="Kabuki"/>).
/// 열쇠는 이름이 아니라 ROM으로 고른다: 풀린 명령이 F3 ED 56 31 (DI · IM 1 · LD SP)로 시작하는 열쇠.</para>
/// <para>ZN (ZN-1 · ZN-2): 명령 = 포트 00h 래치 + NMI, 바이트 하나에 NMI 하나. 곡 코드 16비트를 위 · 아래 바이트 두 번에 (드라이버가 둘씩 묶는다).</para>
/// <para>곡: rom code = Z80 ROM, pcm = QSound ROM (MAME ROM_LOAD16_WORD_SWAP처럼 두 바이트씩 바꿔 둔다). 180초를 QSound (DSP 없는 HLE) VGM으로.</para>
/// </summary>
public static class HootCps2
{
    public const double CpuClock = 8000000, QsClock = 4000000;
    public const double DefaultSeconds = 180, DefaultFade = 4;

    public enum Board { Cps2, Cps1QSound, Zn }

    public static bool Handles(string driver, string type)
    {
        return string.Equals((driver ?? "").Trim(), "capcom", StringComparison.OrdinalIgnoreCase) && BoardOf(type) != null;
    }

    public static Board? BoardOf(string type)
    {
        switch ((type ?? "").Trim().ToLowerInvariant())
        {
            case "cps2": return Board.Cps2;
            case "cps1qs": return Board.Cps1QSound;
            case "zn": return Board.Zn;
            default: return null;
        }
    }

    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file, int layout = 0)
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
        var q = Join(pcm, 0);
        for (int i = 0; i + 1 < q.Length; i += 2) { byte t = q[i]; q[i] = q[i + 1]; q[i + 1] = t; }
        var board = BoardOf(e.Type) ?? Board.Cps2;
        var rom = Join(code, 0x10000);
        byte[] ops = null;
        string key = "";
        if (board == Board.Cps1QSound && !Kabuki.LooksPlain(rom))
        {
            var k = Kabuki.Find(rom);
            if (k != null) { ops = Kabuki.Decode(rom, k.Value, 0x8000); key = k.Value.Name; }
            else unsupported.Add("(Kabuki 열쇠 모름)");
        }
        var m = new Machine(rom, q, board, ops) { Layout = layout, KabukiKey = key };
        m.Missing.AddRange(missing);
        foreach (var u in unsupported) m.Unsupported.Add(u);
        m.RunSeconds(1);
        return m;
    }

    /// <summary>
    /// 캡콤 Kabuki Z80 암호 (MAME kabuki.cpp): 바이트마다 주소에 따라 비트 쌍을 바꾸고 · 돌리고 · XOR. 명령 바이트와 자료 바이트는 고르는 값(select)이 다르다.
    /// </summary>
    public static class Kabuki
    {
        public readonly struct Key
        {
            public readonly string Name;
            public readonly int Swap1, Swap2, Addr, Xor;
            public Key(string name, int swap1, int swap2, int addr, int xor) { Name = name; Swap1 = swap1; Swap2 = swap2; Addr = addr; Xor = xor; }
        }

        /// <summary>CPS-1 QSound 게임의 열쇠 (MAME wof · dino · punisher · slammast_decode).</summary>
        public static readonly Key[] Keys =
        {
            new Key("wof", 0x01234567, 0x54163072, 0x5151, 0x51),
            new Key("dino", 0x76543210, 0x24601357, 0x4343, 0x43),
            new Key("punisher", 0x67452103, 0x75316024, 0x2222, 0x22),
            new Key("slammast", 0x54321076, 0x65432107, 0x3131, 0x19),
        };

        private static int Swap1(int src, int key, int sel)
        {
            if ((sel & (1 << (key & 7))) != 0) src = (src & 0xFC) | ((src & 0x01) << 1) | ((src & 0x02) >> 1);
            if ((sel & (1 << ((key >> 4) & 7))) != 0) src = (src & 0xF3) | ((src & 0x04) << 1) | ((src & 0x08) >> 1);
            if ((sel & (1 << ((key >> 8) & 7))) != 0) src = (src & 0xCF) | ((src & 0x10) << 1) | ((src & 0x20) >> 1);
            if ((sel & (1 << ((key >> 12) & 7))) != 0) src = (src & 0x3F) | ((src & 0x40) << 1) | ((src & 0x80) >> 1);
            return src;
        }

        private static int Swap2(int src, int key, int sel)
        {
            if ((sel & (1 << ((key >> 12) & 7))) != 0) src = (src & 0xFC) | ((src & 0x01) << 1) | ((src & 0x02) >> 1);
            if ((sel & (1 << ((key >> 8) & 7))) != 0) src = (src & 0xF3) | ((src & 0x04) << 1) | ((src & 0x08) >> 1);
            if ((sel & (1 << ((key >> 4) & 7))) != 0) src = (src & 0xCF) | ((src & 0x10) << 1) | ((src & 0x20) >> 1);
            if ((sel & (1 << (key & 7))) != 0) src = (src & 0x3F) | ((src & 0x40) << 1) | ((src & 0x80) >> 1);
            return src;
        }

        private static int Rot(int s) { return ((s & 0x7F) << 1) | ((s & 0x80) >> 7); }

        public static int Byte(int src, Key k, int select)
        {
            src = Swap1(src, k.Swap1 & 0xFFFF, select & 0xFF);
            src = Rot(src);
            src = Swap2(src, (k.Swap1 >> 16) & 0xFFFF, select & 0xFF);
            src ^= k.Xor;
            src = Rot(src);
            src = Swap2(src, k.Swap2 & 0xFFFF, (select >> 8) & 0xFF);
            src = Rot(src);
            src = Swap1(src, (k.Swap2 >> 16) & 0xFFFF, (select >> 8) & 0xFF);
            return src & 0xFF;
        }

        /// <summary>rom 앞 length 바이트를 푼다: rom은 자료 바이트로 바꾸고, 명령 바이트 표를 돌려준다 (length 바이트).</summary>
        public static byte[] Decode(byte[] rom, Key k, int length)
        {
            var ops = new byte[length];
            for (int a = 0; a < length && a < rom.Length; a++)
            {
                int src = rom[a];
                ops[a] = (byte)Byte(src, k, (a + k.Addr) & 0xFFFF);
                rom[a] = (byte)Byte(src, k, ((a ^ 0x1FC0) + k.Addr + 1) & 0xFFFF);
            }
            return ops;
        }

        /// <summary>암호화 안 된 ROM인가 (DI · IM 1로 시작).</summary>
        public static bool LooksPlain(byte[] rom) { return rom.Length > 3 && rom[0] == 0xF3 && rom[1] == 0xED && rom[2] == 0x56; }

        /// <summary>풀린 명령이 F3 ED 56 31 (DI · IM 1 · LD SP,nn)로 시작하는 열쇠.</summary>
        public static Key? Find(byte[] rom)
        {
            if (rom.Length < 4) return null;
            foreach (var k in Keys)
            {
                bool ok = true;
                int[] want = { 0xF3, 0xED, 0x56, 0x31 };
                for (int a = 0; a < 4 && ok; a++) ok = Byte(rom[a], k, (a + k.Addr) & 0xFFFF) == want[a];
                if (ok) return k;
            }
            return null;
        }
    }

    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Rom, Pcm;
        /// <summary>Kabuki로 푼 명령 바이트 (0000h~7FFFh). 암호화가 없으면 null.</summary>
        public readonly byte[] Ops;
        public readonly Board Kind;
        public readonly byte[] Shared = new byte[0x1000], Ram = new byte[0x1000];
        public readonly Z80 Cpu;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>QSound 쓰기 (시각, 레지스터, 16비트 값).</summary>
        public readonly List<(double t, int reg, int val)> Log = new List<(double, int, int)>();
        public int Irqs, Taken, Nmis, LatchReads;
        /// <summary>
        /// 명령 모양. CPS-2 · CPS-1 QSound: 0 = C000h 00h · C001h 코드, 1 = C000h 코드.
        /// ZN: 0 = 위 바이트 다음 아래 바이트, 1 = 아래 다음 위.
        /// </summary>
        public int Layout;
        public string KabukiKey = "";
        /// <summary>공유 RAM을 그냥 RAM으로 (68000 핸드셰이크 · 명령 받음 셈 없음): QSF처럼 롬이 스스로 곡을 고르는 덤프.</summary>
        public bool PlainShared;
        public Dictionary<int, int> PcCount;

        private int bank, dataHi, dataLo, latch;
        private long playStart = -1;
        private long nextIrq, nextLatch;
        private bool irqPending;
        private readonly long irqPeriod = (long)(CpuClock / 250);
        /// <summary>ZN: 아직 보내지 않은 래치 바이트 (NMI 하나에 하나, 1ms 사이).</summary>
        private readonly Queue<int> latchQueue = new Queue<int>();
        private readonly long latchGap = (long)(CpuClock / 1000);

        public Machine(byte[] rom, byte[] pcm, Board kind = Board.Cps2, byte[] ops = null)
        {
            Rom = rom; Pcm = pcm; Kind = kind; Ops = ops;
            if (kind != Board.Zn)
            {
                Shared[0xFFD] = 0x88;   // 68000 준비
                Shared[0x00F] = 0xFF;   // 명령 없음
            }
            Cpu = new Z80(this);
            if (ops != null) Cpu.OpcodeRead = a => a < ops.Length ? ops[a] : Read(a);
            Cpu.PC = 0; Cpu.SP = 0xFFFF; Cpu.Im = 1;
            nextIrq = irqPeriod;
        }

        public void Play(int code)
        {
            Log.Clear();
            playStart = Cpu.Cycles;
            Send(code);
        }

        /// <summary>68000(ZN은 주 CPU)처럼 명령을 보낸다.</summary>
        public void Send(int code)
        {
            if (Kind == Board.Zn)
            {
                int hi = (code >> 8) & 0xFF, lo = code & 0xFF;
                if (Layout == 0) { latchQueue.Enqueue(hi); latchQueue.Enqueue(lo); }
                else { latchQueue.Enqueue(lo); latchQueue.Enqueue(hi); }
                nextLatch = Cpu.Cycles;
                return;
            }
            for (int i = 0; i < 0x0F; i++) Shared[i] = 0;
            if (Layout == 0) { Shared[0] = (byte)(code >> 8); Shared[1] = (byte)code; }
            else Shared[0] = (byte)code;
            Shared[0x0F] = 0x00;
        }

        private void Push(int v)
        {
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Write(Cpu.SP, (byte)v); Write((Cpu.SP + 1) & 0xFFFF, (byte)(v >> 8));
        }

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

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                if (latchQueue.Count > 0 && Cpu.Cycles >= nextLatch)
                {
                    latch = latchQueue.Dequeue();
                    nextLatch = Cpu.Cycles + latchGap;
                    Nmi();
                }
                if (Cpu.Cycles >= nextIrq) { nextIrq += irqPeriod; irqPending = true; }
                if (irqPending && Cpu.Iff1)
                {
                    irqPending = false;
                    Irqs++;
                    Cpu.Halted = false;
                    Cpu.Iff1 = Cpu.Iff2 = false;
                    Push(Cpu.PC);
                    Cpu.PC = 0x38;
                    Cpu.Cycles += 13;
                }
                if (Cpu.Halted)
                {
                    long wake = Math.Min(end, nextIrq);
                    if (latchQueue.Count > 0) wake = Math.Min(wake, nextLatch);
                    Cpu.Cycles = Math.Max(Cpu.Cycles + 1, wake);
                    continue;
                }
                if (PcCount != null) { PcCount.TryGetValue(Cpu.PC, out int k); PcCount[Cpu.PC] = k + 1; }
                Cpu.Step();
            }
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (a < 0x8000) return Rom[a % Rom.Length];
            if (a < 0xC000) return Rom[(0x8000 + (long)bank * 0x4000 + (a - 0x8000)) % Rom.Length];
            if (a < 0xD000) return Shared[a - 0xC000];
            if (a >= 0xF000) return Ram[a - 0xF000];
            if (a == 0xD007) return 0x80;
            return 0xFF;
        }

        public void Write(int a, byte v)
        {
            a &= 0xFFFF;
            if (a >= 0xC000 && a < 0xD000)
            {
                if (Kind != Board.Zn && !PlainShared)
                {
                    if (a == 0xC00F && v == 0xFF && Shared[0x0F] != 0xFF) Taken++;
                    if (a == 0xCFFF && v == 0x77) { Shared[0xFFF] = 0xFF; return; }   // 68000 핸드셰이크: Z80이 77h를 쓰면 FFh로 답한다
                }
                Shared[a - 0xC000] = v;
                return;
            }
            if (a >= 0xF000) { Ram[a - 0xF000] = v; return; }
            switch (a)
            {
                case 0xD000: dataHi = v; return;
                case 0xD001: dataLo = v; return;
                case 0xD002:
                    if (playStart < 0) playStart = Cpu.Cycles;
                    Log.Add((Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock), v, (dataHi << 8) | dataLo));
                    return;
                case 0xD003: bank = v & 0x0F; return;
            }
        }

        public byte In(int port)
        {
            if (Kind == Board.Zn && (port & 0xFF) == 0) { LatchReads++; return (byte)latch; }
            return 0xFF;
        }

        public void Out(int port, byte v) { }

        public VgmSong ToVgm(double seconds, double fade)
        {
            string sys = Kind == Board.Zn ? "Capcom ZN" : Kind == Board.Cps1QSound ? "Capcom CPS-1 QSound" : "Capcom CPS-2";
            var w = new VgmWriter { System = sys + " (hoot)", Notes = "hoot capcom " + (Kind == Board.Zn ? "zn" : Kind == Board.Cps1QSound ? "cps1qs" : "cps2") + " (QSound HLE)" };
            w.SetClock(VgmWriter.ClockQsound, QsClock);
            if (Pcm.Length > 0)
            {
                var cmd = new byte[7 + 8 + Pcm.Length];
                cmd[0] = 0x67; cmd[1] = 0x66; cmd[2] = 0x8F;
                int size = 8 + Pcm.Length;
                cmd[3] = (byte)size; cmd[4] = (byte)(size >> 8); cmd[5] = (byte)(size >> 16); cmd[6] = (byte)(size >> 24);
                int total = Pcm.Length;
                cmd[7] = (byte)total; cmd[8] = (byte)(total >> 8); cmd[9] = (byte)(total >> 16); cmd[10] = (byte)(total >> 24);
                Array.Copy(Pcm, 0, cmd, 15, Pcm.Length);
                w.Write(cmd);
            }
            double total2 = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var x in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total2, Math.Max(0, x.t));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(0xC4, (byte)(x.val >> 8), (byte)x.val, (byte)x.reg);
            }
            if ((long)total2 > at) w.Wait((long)total2 - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (Log.Count == 0) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
