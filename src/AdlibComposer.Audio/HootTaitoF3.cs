using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot 타이토 F3 · Super Z System (driver taito, type f3system): 타이토 앙상블 사운드 (MAME taito_en)를 흉내 낸다.
/// <para>기계: 68000 15.24MHz, RAM 000000h~00FFFFh (03FFFFh까지 · FF0000h~ 되비침, 시작 벡터 = 프로그램 ROM 처음 8바이트),
/// 메인 CPU와 같이 쓰는 MB8421 140000h~140FFFh (짝 주소 = 위 바이트), ES5505 200000h~20001Fh, ES5510 ESP 260000h (흉내 안 함), MC68681 DUART 280000h~28001Fh (홀 주소),
/// ES5505 뱅크 300000h~30003Fh (목소리마다 2MB 쪽), 음량 340000h, 프로그램 ROM C00000h~C7FFFFh (sub0 = 위 바이트 · sub1 = 아래 바이트).
/// IRQ 6 = DUART 타이머 (벡터 = DUART IVR).</para>
/// <para>표본 ROM: pcm (8비트) → 16비트 낱말의 위 바이트, 오프셋 = 바이트 오프셋 / 2.</para>
/// <para>곡 요청 (사운드 프로그램으로 맞춤 — 메인 CPU가 쓰는 DPRAM 메시지 고리): [길이 3][명령 81h][곡 코드 & 7Fh]. hoot 곡 코드의 bit7은 사운드 프로그램에서
/// "울리는 중일 때만"이라 뺀다. 명령 80h · 82h는 같은 찾기 뒤 다른 일 (82h = 페이드 목록에서 빼기).</para>
/// <para>재생은 미리 렌더하지 않고 스스로 도는 칩 (<see cref="Chip"/>): 표본 ROM이 16MB까지라 VGM에 담지 않는다. 길이 180초 고정.</para>
/// </summary>
public static class HootTaitoF3
{
    public const double CpuClock = 30476180 / 2.0, ChipClock = 30476180 / 2.0, DuartClock = 16000000 / 4.0;
    public const double DefaultSeconds = 180, DefaultFade = 4, BootSeconds = 1;

    /// <summary>게임 이름 형식이지만 같은 F3 사운드 보드 (main0~3 · sub0~1 · pcm, 옵션 없음): Ring Rage · Riding Fight · Grid Seeker.</summary>
    public static readonly string[] GameTypes = { "ringrage", "ridingf", "gseeker" };   // galastrm: zip의 표본 ROM 이름 (c99-06~09)이 목록 (c99-07~10)과 달라 뺌

    public static bool Handles(string driver, string type)
    {
        if (!string.Equals((driver ?? "").Trim(), "taito", StringComparison.OrdinalIgnoreCase)) return false;
        string t = (type ?? "").Trim().ToLowerInvariant();
        return t == "f3system" || Array.IndexOf(GameTypes, t) >= 0;
    }

    /// <summary>"항목.hoot::n" → 곡 (길이만 든 VGM + 스스로 도는 F3 사운드 칩).</summary>
    public static VgmSong Load(string path)
    {
        string file = ChipSongs.FilePart(path);
        NezM3u.SplitTrack(path, out int n);
        var e = HootArchive.Load(file);
        if (e.Titles.Count == 0) throw new InvalidDataException("hoot 항목에 곡 목록(titlelist)이 없습니다.");
        n = Math.Max(0, Math.Min(n, e.Titles.Count - 1));
        var (code, title) = e.Titles[n];
        string dir = Path.GetDirectoryName(Path.GetFullPath(file));
        var w = new VgmWriter { Title = title, Game = e.Name, System = "Taito F3 (hoot)" };
        w.Wait((long)Math.Round((DefaultSeconds + DefaultFade) * VgmSong.Rate));
        var v = VgmFormat.Read(w.ToArray());
        v.FilePath = path;
        v.FadeSeconds = DefaultFade;
        v.SelfRunningName = "Taito F3 (ES5505)";
        Func<string, byte[]> read = name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; };
        var pre = System.Threading.Tasks.Task.Run(() => { var m = Boot(e, read); m.Request(code); return m; });
        int taken = 0;
        v.SelfRunning = () => new Chip(() => System.Threading.Interlocked.Exchange(ref taken, 1) == 0 ? pre.GetAwaiter().GetResult() : StartMachine(e, read, code));
        v.SourceFormat = "hoot taito f3system (68000 + ES5505, 곡 코드 0x" + code.ToString("X2") + ", 길이 " + DefaultSeconds + "초 고정)";
        return v;
    }

    private static Machine StartMachine(HootArchive.Entry e, Func<string, byte[]> read, int code) { var m = Boot(e, read); m.Request(code); return m; }

    /// <summary>스스로 도는 칩: 기계 (68000 · DUART · ES5505)를 출력 표본마다 돌린다. 채널 = ES5505 목소리 32개.</summary>
    public sealed class Chip : ISoundChip, ISelfRunningChip
    {
        private readonly Func<Machine> boot;
        private Machine m;
        private bool failed;
        private int used;
        public Chip(Func<Machine> boot) { this.boot = boot; }
        public Machine Machine { get { return m; } }
        private bool Ensure()
        {
            if (m != null) return true;
            if (failed) return false;
            try { m = boot(); return true; } catch (Exception) { failed = true; return false; }
        }
        public string Name { get { return "Taito F3 (68000 + ES5505)"; } }
        public int NativeRate { get { return 44100; } }
        public int ChannelCount { get { return 32; } }
        public string ChannelLabel(int ch) { return "V" + (ch + 1); }
        public void Reset() { }
        public void Write(int port, int reg, int value) { }
        public void Generate(out int left, out int right)
        {
            if (!Ensure()) { left = right = 0; return; }
            m.Generate(NativeRate, out left, out right);
        }
        public void Skip(double seconds) { if (!Ensure()) return; long n = (long)(seconds * NativeRate); for (long i = 0; i < n; i++) m.Generate(NativeRate, out _, out _); }
        public int VoicesUsed { get { if (m != null) for (int i = 0; i < 32; i++) if (m.Otis.VoiceActive(i)) used |= 1 << i; return used; } }
        public void SetMute(int ch, bool mute) { if (Ensure() && ch >= 0 && ch < 32) m.Otis.Muted[ch] = mute; }
        public void SetPitch(int ch, double ratio) { }
        public double ChannelFrequency(int ch) { return m != null ? m.Otis.VoiceFrequency(ch) : 0; }
        public float ChannelLevel(int ch) { return m != null && m.Otis.VoiceActive(ch) ? m.Otis.VoiceVolume(ch) / 255f : 0; }
        public bool ChannelActive(int ch) { return m != null && m.Otis.VoiceActive(ch); }
    }

    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        byte[] sub0 = null, sub1 = null;
        var main = new byte[4][];
        var pcm = new List<(long off, byte[] data)>();
        var m = new Machine();
        foreach (var (type, off, text) in e.Roms)
        {
            var d = file(text);
            if (d == null) { if (type.StartsWith("sub") || type == "pcm") m.Missing.Add(text); continue; }
            switch (type)
            {
                case "sub0": sub0 = d; break;
                case "sub1": sub1 = d; break;
                case "pcm": pcm.Add((off, d)); break;
                case "main0": main[0] = d; break;
                case "main1": main[1] = d; break;
                case "main2": main[2] = d; break;
                case "main3": main[3] = d; break;
                default: m.Unsupported.Add(type); break;
            }
        }
        if (sub0 != null && sub1 != null)
        {
            int n = Math.Min(sub0.Length, sub1.Length);
            for (int i = 0; i < n && 2 * i + 1 < m.Rom.Length; i++) { m.Rom[2 * i] = sub0[i]; m.Rom[2 * i + 1] = sub1[i]; }
        }
        // 메인 프로그램 ROM (32비트: main0~3 = 바이트 0~3): hoot 옵션 tbloffs = 소리 번호 → 사운드 메시지 표 (메인 CPU의 재생 루틴을 흉내)
        m.TableOffset = e.Option("tbloffs", 0);
        if (m.TableOffset > 0 && main.All(x => x != null))
        {
            int n = main.Min(x => x.Length);
            m.MainRom = new byte[n * 4];
            for (int i = 0; i < n; i++) for (int k = 0; k < 4; k++) m.MainRom[i * 4 + k] = main[k][i];
        }
        long words = pcm.Count == 0 ? 0 : pcm.Max(p => p.off / 2 + p.data.Length);
        int size = 1; while (size < words) size <<= 1;
        var rom = new short[Math.Max(size, 0x100000)];
        foreach (var (off, d) in pcm)
            for (int i = 0; i < d.Length && off / 2 + i < rom.Length; i++) rom[off / 2 + i] = (short)(d[i] << 8);
        m.Otis.Rom = rom;
        m.BankMask = Math.Max(1, rom.Length / 0x100000) - 1;
        m.Reset();
        m.RunSeconds(BootSeconds);   // 사운드 프로그램 초기화 (ESP 확인 · 표 복사)
        return m;
    }

    public sealed class Machine : IM68kBus
    {
        public readonly byte[] Ram = new byte[0x10000], Rom = new byte[0x80000], Dpram = new byte[0x800];
        public readonly M68000 Cpu;
        public readonly Es5505 Otis = new Es5505(ChipClock);
        public readonly int[] OtisBank = new int[32];
        /// <summary>진단: ES5505 레지스터마다 쓴 횟수.</summary>
        public readonly int[] OtisWrites = new int[16];
        public int BankMask;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>진단: 메인 쪽이 읽힌 DPRAM 자리 · 횟수, 그 밖 주소.</summary>
        public readonly Dictionary<int, int> DpramReads = new Dictionary<int, int>(), DpramWrites = new Dictionary<int, int>(), OtherReads = new Dictionary<int, int>(), OtherWrites = new Dictionary<int, int>();
        public int Irqs;
        /// <summary>진단: PC마다 실행 횟수 (null = 안 셈).</summary>
        public Dictionary<int, int> PcCount;
        public readonly Dictionary<int, int> DpramReadPcs = new Dictionary<int, int>();

        // DUART (MC68681): 타이머만
        private int duartAcr, duartImr, duartIsr, duartIvr = 0x0F, duartCtur, duartCtlr, duartOpcr;
        private long duartNext = long.MaxValue, duartPeriod;

        public Machine()
        {
            Cpu = new M68000(this);
            Otis.VoiceBank = v => (OtisBank[v & 31] & BankMask) << 20;
        }

        public void Reset()
        {
            Array.Copy(Rom, 0, Ram, 0, 8);   // 스택 · 시작 벡터
            Cpu.Reset();
        }

        // ------------------------------------------------------------ DUART

        private void DuartStart()
        {
            int preload = (duartCtur << 8) | duartCtlr;
            if (preload == 0) preload = 0x10000;
            int src = (duartAcr >> 4) & 7;
            double clk = src == 7 ? DuartClock / 16 : DuartClock;   // 타이머 X1 · X1/16 (그 밖은 X1으로)
            bool timer = (src & 4) != 0;
            duartPeriod = Math.Max(1, (long)(preload * (timer ? 2 : 1) / clk * CpuClock));
            duartNext = Cpu.Cycles + duartPeriod;
        }

        private void DuartTick()
        {
            if (Cpu.Cycles < duartNext) return;
            duartNext += duartPeriod;
            duartIsr |= 0x08;   // C/T 준비
        }

        private bool DuartIrq { get { return (duartIsr & duartImr) != 0; } }

        private int DuartRead(int reg)
        {
            switch (reg)
            {
                case 1: case 9: return 0x0C;   // SRA · SRB: 보낼 준비됨
                case 5: return duartIsr;
                case 6: return duartCtur;
                case 7: return duartCtlr;
                case 12: return duartIvr;
                case 13: return 0xFF;   // 입력 핀
                case 14: DuartStart(); return 0xFF;            // 카운터 시작
                case 15: duartIsr &= ~0x08; return 0xFF;      // 카운터 멈춤 (타이머 모드 = 준비 지움)
            }
            return 0xFF;
        }

        private void DuartWrite(int reg, int v)
        {
            switch (reg)
            {
                case 4: duartAcr = v; if (((v >> 4) & 4) != 0) DuartStart(); return;
                case 5: duartImr = v; return;
                case 6: duartCtur = v; return;
                case 7: duartCtlr = v; return;
                case 12: duartIvr = v; return;
                case 13: duartOpcr = v; return;
            }
        }

        // ------------------------------------------------------------ ES5510 ESP: 호스트 쪽 기억만 (반향 DSP는 돌리지 않음 — 드라이버가 써 넣고 되읽어 확인함)

        private readonly int[] espGpr = new int[256];
        private readonly long[] espInstr = new long[256];
        private int espGprLatch, espDolLatch, espDadrLatch, espHostControl;
        private long espInstrLatch;

        private int EspRead(int o)
        {
            switch (o)
            {
                case 0x00: return (espGprLatch >> 16) & 0xFF;
                case 0x01: return (espGprLatch >> 8) & 0xFF;
                case 0x02: return espGprLatch & 0xFF;
                case 0x03: case 0x04: case 0x05: case 0x06: case 0x07: case 0x08: return (int)((espInstrLatch >> ((8 - o) * 8)) & 0xFF);
                case 0x0C: return (espDolLatch >> 16) & 0xFF;
                case 0x0D: return (espDolLatch >> 8) & 0xFF;
                case 0x0E: return 0xFF;
                case 0x0F: return (espDadrLatch >> 16) & 0xFF;
                case 0x10: return (espDadrLatch >> 8) & 0xFF;
                case 0x11: return espDadrLatch & 0xFF;
                case 0x12: return 0;
                case 0x16: return 0x27;
            }
            return 0;
        }

        private void EspWrite(int o, int v)
        {
            switch (o)
            {
                case 0x00: espGprLatch = (espGprLatch & 0x00FFFF) | (v << 16); return;
                case 0x01: espGprLatch = (espGprLatch & 0xFF00FF) | (v << 8); return;
                case 0x02: espGprLatch = (espGprLatch & 0xFFFF00) | v; return;
                case 0x03: case 0x04: case 0x05: case 0x06: case 0x07: case 0x08:
                    {
                        int sh = (8 - o) * 8;
                        espInstrLatch = (espInstrLatch & ~(0xFFL << sh)) | ((long)v << sh);
                        return;
                    }
                case 0x0C: espDolLatch = (espDolLatch & 0x00FFFF) | (v << 16); return;
                case 0x0D: espDolLatch = (espDolLatch & 0xFF00FF) | (v << 8); return;
                case 0x0E: espDolLatch = (espDolLatch & 0xFFFF00) | v; return;
                case 0x0F: espDadrLatch = (espDadrLatch & 0x00FFFF) | (v << 16); return;
                case 0x10: espDadrLatch = (espDadrLatch & 0xFF00FF) | (v << 8); return;
                case 0x11: espDadrLatch = (espDadrLatch & 0xFFFF00) | v; return;
                case 0x12: espHostControl = (espHostControl & 4) | (v & 3); return;
                case 0x80: if (v < 0xA0) espInstrLatch = espInstr[v]; espGprLatch = espGpr[v] & 0xFFFFFF; return;   // 읽기 고름
                case 0xA0: espGpr[v] = espGprLatch; return;                                                       // GPR 쓰기
                case 0xC0: if (v < 0xA0) espInstr[v] = espInstrLatch & 0xFFFFFFFFFFFFL; return;                 // 명령 쓰기
                case 0xE0: espGpr[v] = espGprLatch; if (v < 0xA0) espInstr[v] = espInstrLatch & 0xFFFFFFFFFFFFL; return;
            }
        }

        // ------------------------------------------------------------ 메인 CPU 쪽 (hoot)

        /// <summary>
        /// 곡 요청 (메인 CPU 몫, 사운드 프로그램으로 맞춤): DPRAM 고리 (사운드 쪽 140000h + 2 × 자리) 쓰기 포인터 (480h · 481h, 사운드 쪽 바이트 단위)에
        /// 메시지 [길이 3][명령 82h][곡]을 넣고 포인터를 올린다 — 사운드 쪽은 482h · 483h 읽기 포인터로 따라 읽고, 길이 표 (C13260h)와 처리기 표로 나눈다.
        /// </summary>
        /// <summary>메인 프로그램 ROM (tbloffs가 있을 때만)과 소리 표 자리.</summary>
        public byte[] MainRom;
        public int TableOffset;

        /// <summary>
        /// hoot 곡 코드 요청: tbloffs가 있으면 메인 CPU의 재생 루틴처럼 표 [코드]의 기록을 메시지로 (형 0 [0][s][p] → 81h s · 86h s p (s의 bit7이면 86h s&7F p 더),
        /// 형 1 [1][s][p][q] → 81h s · 86h s p · 88h s q, 형 2 (8바이트) → 8Dh · 8Eh, 형 3 [n][메시지들] → 그대로), 없으면 81h (코드 &amp; 7Fh).
        /// </summary>
        public void Request(int code)
        {
            if (MainRom == null || TableOffset <= 0) { Play(code); return; }
            int Be32(int a) { return a + 3 < MainRom.Length && a >= 0 ? (MainRom[a] << 24) | (MainRom[a + 1] << 16) | (MainRom[a + 2] << 8) | MainRom[a + 3] : -1; }
            int ptr = Be32(TableOffset + 4 * code);
            if (ptr < 0 || ptr + 8 >= MainRom.Length) { Play(code); return; }
            int B(int k) { return MainRom[ptr + k]; }
            switch (B(0))
            {
                case 0:
                    Send(3, 0x81, B(1)); Send(4, 0x86, B(1), B(2));
                    if ((B(1) & 0x80) != 0) Send(4, 0x86, B(1) & 0x7F, B(2));
                    break;
                case 1:
                    Send(3, 0x81, B(1)); Send(4, 0x86, B(1), B(2)); Send(4, 0x88, B(1), B(3));
                    break;
                case 2:
                    Send(6, 0x8D, B(1), B(3), B(4), B(5)); Send(6, 0x8E, B(1), B(3), B(6), B(2));
                    break;
                case 3:
                    {
                        int n = B(1);
                        for (int k = 0; k < n && ptr + 2 + k < MainRom.Length; k++) Raw(MainRom[ptr + 2 + k]);
                        Commit();
                        break;
                    }
            }
        }

        private int sendPtr = -1;
        private void Raw(int b)
        {
            if (sendPtr < 0) sendPtr = (Dpram[0x480] << 8) | Dpram[0x481];
            Dpram[(sendPtr >> 1) & 0x3FF] = (byte)b;
            sendPtr = (sendPtr + 2) & 0x7FE;
        }
        private void Commit() { if (sendPtr >= 0) { Dpram[0x480] = (byte)(sendPtr >> 8); Dpram[0x481] = (byte)sendPtr; sendPtr = -1; } }
        /// <summary>메시지 하나 [길이][명령][인자…] (길이 = 전체 바이트 수).</summary>
        public void Send(params int[] msg) { foreach (int b in msg) Raw(b); Commit(); }

        public void Play(int code, int command = 0x81)
        {
            int ptr = (Dpram[0x480] << 8) | Dpram[0x481];
            foreach (int b in new[] { 3, command & 0xFF, code & 0x7F })
            {
                Dpram[(ptr >> 1) & 0x3FF] = (byte)b;
                ptr = (ptr + 2) & 0x7FE;
            }
            Dpram[0x480] = (byte)(ptr >> 8); Dpram[0x481] = (byte)ptr;
        }

        /// <summary>메인 CPU가 DPRAM에 쓰는 것처럼 (자리 0~7FFh).</summary>
        public void MainWrite(int index, int value) { Dpram[index & 0x7FF] = (byte)value; }
        public int MainRead(int index) { return Dpram[index & 0x7FF]; }

        // ------------------------------------------------------------ 버스

        private static void Count(Dictionary<int, int> d, int k) { d.TryGetValue(k, out int c); d[k] = c + 1; }

        public byte Read8(uint addr)
        {
            int a = (int)(addr & 0xFFFFFF);
            if (a < 0x40000 || a >= 0xFF0000) return Ram[a & 0xFFFF];
            if (a >= 0xC00000 && a < 0xC80000) return Rom[a - 0xC00000];
            if (a >= 0x140000 && a < 0x141000) { if ((a & 1) != 0) return 0xFF; int i = (a - 0x140000) >> 1; Count(DpramReads, i); if (PcCount != null) Count(DpramReadPcs, (int)(Cpu.PC & 0xFFFFFF)); return Dpram[i]; }
            if (a >= 0x200000 && a < 0x200020) { int w = Otis.Read((a - 0x200000) >> 1); return (byte)((a & 1) == 0 ? w >> 8 : w); }
            if (a >= 0x280000 && a < 0x280020) return (a & 1) != 0 ? (byte)DuartRead((a - 0x280000) >> 1) : (byte)0xFF;
            if (a >= 0x260000 && a < 0x260200) return (a & 1) != 0 ? (byte)EspRead((a - 0x260000) >> 1) : (byte)0xFF;
            Count(OtherReads, a);
            return 0xFF;
        }

        public ushort Read16(uint addr)
        {
            int a = (int)(addr & 0xFFFFFE);
            if (a >= 0x200000 && a < 0x200020) return (ushort)Otis.Read((a - 0x200000) >> 1);
            return (ushort)((Read8((uint)a) << 8) | Read8((uint)a + 1));
        }

        public void Write8(uint addr, byte value)
        {
            int a = (int)(addr & 0xFFFFFF);
            if (a < 0x40000 || a >= 0xFF0000) { Ram[a & 0xFFFF] = value; return; }
            if (a >= 0x140000 && a < 0x141000) { if ((a & 1) == 0) { int i = (a - 0x140000) >> 1; Count(DpramWrites, i); Dpram[i] = value; } return; }
            if (a >= 0x200000 && a < 0x200020) { OtisWrites[(a - 0x200000) >> 1]++; Otis.Write((a - 0x200000) >> 1, (a & 1) == 0 ? value << 8 : value, (a & 1) == 0 ? 2 : 1); return; }
            if (a >= 0x280000 && a < 0x280020) { if ((a & 1) != 0) DuartWrite((a - 0x280000) >> 1, value); return; }
            if (a >= 0x300000 && a < 0x300040) { int v = (a - 0x300000) >> 1; OtisBank[v] = (a & 1) == 0 ? (OtisBank[v] & 0xFF) | (value << 8) : (OtisBank[v] & 0xFF00) | value; return; }
            if (a >= 0x260000 && a < 0x260200) { if ((a & 1) != 0) EspWrite((a - 0x260000) >> 1, value); return; }
            if (a >= 0x340000 && a < 0x340004) return;
            if (a >= 0xC00000 && a < 0xC80000) return;
            Count(OtherWrites, a);
        }

        public void Write16(uint addr, ushort value)
        {
            int a = (int)(addr & 0xFFFFFE);
            if (a >= 0x200000 && a < 0x200020) { OtisWrites[(a - 0x200000) >> 1]++; Otis.Write((a - 0x200000) >> 1, value, 3); return; }
            if (a >= 0x300000 && a < 0x300040) { OtisBank[(a - 0x300000) >> 1] = value; return; }
            Write8((uint)a, (byte)(value >> 8));
            Write8((uint)a + 1, (byte)value);
        }

        public int Acknowledge(int level) { return level == 6 ? duartIvr : -1; }

        // ------------------------------------------------------------ 돌리기

        private double chipAt;

        /// <summary>CPU를 cycles만큼 (칩 표본은 버림 — 진단용).</summary>
        public void Run(long cycles)
        {
            long end = Cpu.Cycles + cycles;
            while (Cpu.Cycles < end)
            {
                DuartTick();
                Cpu.IrqLevel = DuartIrq ? 6 : 0;
                long before = Cpu.Cycles;
                if (PcCount != null) { int k = (int)(Cpu.PC & 0xFFFFFF); PcCount.TryGetValue(k, out int pc); PcCount[k] = pc + 1; }
                Cpu.Step();
                if (Cpu.IrqLevel == 6 && Cpu.Cycles - before > 40) Irqs++;
            }
        }

        public void RunSeconds(double s) { Run((long)(CpuClock * s)); }

        /// <summary>출력 표본 하나 (rate Hz): CPU를 그만큼 돌리고 칩 표본을 모아 평균.</summary>
        public void Generate(int rate, out int left, out int right)
        {
            Run((long)(CpuClock / rate));
            chipAt += Otis.SampleRate / rate;
            long l = 0, r = 0; int n = 0;
            while (chipAt >= 1) { Otis.Step(out int a, out int b); l += a; r += b; n++; chipAt -= 1; }
            if (n > 0) { lastL = (int)(l / n); lastR = (int)(r / n); }
            left = lastL >> 4; right = lastR >> 4;
        }

        private int lastL, lastR;
    }
}
