using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// hoot MSX 범용 드라이버 (driver type="generic" msx): 문서가 없어 hoot 목록의 시작 코드(rom "patch" 등 init_pc의 code)를 거꾸로 읽어 맞춘 규약.
/// <para>기계: Z80 3.58MHz, 64KB 전부 RAM (슬롯 · 매퍼 무시), VDP 수직 귀선 인터럽트 60Hz (IM 1 = 0038h, IM 2 = (I·256+FFh)의 주소),
/// 작은 BIOS (0038h 처리기 = 레지스터 저장 → VDP 상태 읽기 → H.KEYI(FD9Ah) · H.TIMI(FD9Fh) → EI · RET, WRTPSG 93h · RDPSG 96h · GICINI 90h, RDSLT · WRSLT · CALSLT · ENASLT).
/// 칩: PSG (A0h~A2h), OPLL (7Ch · 7Dh), MSX-AUDIO (C0h · C1h), SCC (9000h에 3Fh를 쓰면 9800h~가 SCC — 옵션 use_scc면 늘 켬), SCC+ (BFFEh 모드).</para>
/// <para>hoot 포트 (시작 코드가 쓰는 것): IN 02h = 명령 (1 = 재생, 2 = 정지; 읽으면 비움), OUT 02h = 받음,
/// IN 03h~06h = 곡 코드 바이트 0~3, OUT 03h,n = bgm n 파일을 mdata_addr에 (mfile_size까지, mdata_size까지 0으로 채운 뒤),
/// IN 07h = FM(OPLL)이 있으면 1 (옵션 use_opll).</para>
/// <para>순서: rom code(주소에 파일) · binary(16진 바이트)를 놓고 init_pc부터 돌려 명령을 기다리게 한 뒤, 재생 명령 → 정한 초만큼 돌려 칩 쓰기를 VGM으로 (KSS처럼 미리 렌더).</para>
/// </summary>
public static class HootMsx
{
    public const double CpuClock = 3579545;
    public const double DefaultSeconds = 180, DefaultFade = 4;
    private const double FrameRate = 60;

    /// <summary>항목 · 곡 코드를 seconds초 돌려 VGM으로. dir = zip을 푼 폴더 (rom 파일이 있는 곳).</summary>
    public static VgmSong Render(HootArchive.Entry e, string dir, int code, double seconds = DefaultSeconds, double fade = DefaultFade)
    {
        var m = Boot(e, name => { string f = HootArchive.FindFile(dir, name); return f != null ? File.ReadAllBytes(f) : null; });
        m.Play(code);
        m.RunSeconds(seconds);
        return m.ToVgm(seconds, fade);
    }

    /// <summary>기계를 세우고 init_pc부터 돌려 hoot 명령을 기다리는 곳까지 (최대 2초).</summary>
    public static Machine Boot(HootArchive.Entry e, Func<string, byte[]> file)
    {
        if (e.Type.Equals("ds4", StringComparison.OrdinalIgnoreCase)) return BootDs4(e, file);
        var m = new Machine
        {
            Opll = e.Option("use_opll", 0) != 0,
            SccAlways = e.Option("use_scc", 0) != 0,
            DataAddr = e.Options.ContainsKey("mdata_addr") ? e.Option("mdata_addr", 0) & 0xFFFF : -1,   // 0도 주소
            DataSize = Math.Max(0, e.Option("mdata_size", 0)),
            FileSize = Math.Max(0, e.Option("mfile_size", 0)),
        };
        m.InstallBios();
        var bgm = new Dictionary<int, string>();
        foreach (var (type, off, text) in e.Roms)
        {
            switch (type)
            {
                case "code":
                    {
                        var d = file(text);
                        if (d == null) { m.Missing.Add(text); break; }
                        if (off + d.Length > 0x10000 && d.Length > 0x4000) m.PutBanked((int)off, d);   // FM-PAC 64KB 등: 16KB 뱅크 카트리지
                        else m.Put((int)off, d);
                        break;
                    }
                case "binary":
                    {
                        var d = HexBytes(text) ?? file(text);
                        if (d != null) m.Put((int)off, d);
                        break;
                    }
                case "sccp":
                    {
                        // Konami SCC+ 사운드 카트리지 RAM (offset = 카트리지 RAM 안 위치): 본체 RAM과 따로 — A8h로 카트리지 슬롯을 고른 쪽에만 보인다
                        // (kgc3 grascc: GRA.BIN = 본체 RAM 4000h, GRASCC.BIN = 카트리지; 같은 자리에 겹쳐 쓰면 재생 루틴이 지워졌다)
                        var d = file(text);
                        if (d == null) { m.Missing.Add(text); break; }
                        m.Cart = m.Cart ?? new byte[0x20000];
                        for (int i = 0; i < d.Length && off + i < m.Cart.Length; i++) m.Cart[off + i] = d[i];
                        break;
                    }
                case "bgm": bgm[(int)off] = text; break;
                default: m.Unsupported.Add(type); break;   // fmbios · rom: 아직
            }
        }
        m.Bgm = n =>
        {
            if (!bgm.TryGetValue(n, out var name)) return null;
            var d = file(name);
            if (d == null && m.Missing.Count < 64) m.Missing.Add(name);
            return d;
        };
        m.Start(e.Option("init_pc", 0x0400) & 0xFFFF);
        m.Run((long)(CpuClock * 2), untilWaiting: true);
        return m;
    }

    // hoot ds4.cpp IPL: IM 1 · SP F380h, 뱅크 2 · 3, CALL 48F2h (초기화), 고리: IN 00h (재생 깃발) → IN 01h (곡) → (C094h) → CALL 729Dh, 0038h = 처리기 (CALL 7236h)
    private static readonly byte[] Ds4Ipl =
    {
        0xed,0x56,0x31,0x80,0xf3,0x3e,0x02,0x32,0x00,0x70,0x3c,0x32,0x00,0x78,0xcd,0xf2,
        0x48,0xdb,0x00,0xb7,0x20,0x04,0xdb,0x02,0x18,0xf7,0xf3,0x3e,0x02,0x32,0x00,0x70,
        0x3c,0x32,0x00,0x78,0xdb,0x01,0x32,0x94,0xc0,0xcd,0x9d,0x72,0xfb,0x18,0xe2,0xff,
        0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xff,0xf3,0xf5,0xc5,0xd5,0xe5,0xdd,0xe5,0xfd,
        0xe5,0x3e,0x02,0x32,0x00,0x70,0x3c,0x32,0x00,0x78,0xcd,0x36,0x72,0xfd,0xe1,0xdd,
        0xe1,0xe1,0xd1,0xc1,0xf1,0xfb,0xc9,
    };

    /// <summary>
    /// hoot ds4.cpp (msx ds4 = Legacy of the Wizard 롬 256KB): 4000h~7FFFh = 롬 0~3FFFh, 8000h · A000h = 8KB 뱅크 (7000h · 7800h에 쓰기),
    /// 0000h~3FFFh · C000h~ = RAM, IPL + PSG 진입점 (93h · 96h → 1102h · 110Eh), 10000클럭 돌리고 C042h · C043h · C095h · C098h를 채운다.
    /// </summary>
    private static Machine BootDs4(HootArchive.Entry e, Func<string, byte[]> file)
    {
        var m = new Machine();
        foreach (var (type, off, text) in e.Roms)
        {
            if (type != "code") { m.Unsupported.Add(type); continue; }
            var d = file(text);
            if (d == null) { m.Missing.Add(text); continue; }
            var rom = m.Ds4Rom ?? new byte[0x40000];
            long o = off < 0 ? 0 : off;   // offset 없음 = 0
            for (int i = 0; i < d.Length && o + i < rom.Length; i++) rom[o + i] = d[i];
            m.Ds4Rom = rom;
        }
        if (m.Ds4Rom == null) return m;
        m.Put(0, Ds4Ipl);
        m.Put(0x93, new byte[] { 0xC3, 0x02, 0x11, 0xC3, 0x0E, 0x11 });
        m.Put(0x1102, new byte[] { 0xF3, 0xD3, 0xA0, 0xF5, 0x7B, 0xD3, 0xA1, 0xFB, 0xF1, 0xC9, 0x00, 0x00, 0xD3, 0xA0, 0xDB, 0xA2, 0xC9 });
        m.Start(0);
        m.Run(10000);
        m.Ram[0xC042] = 0x02; m.Ram[0xC043] = 0x03; m.Ram[0xC095] = 0xFF; m.Ram[0xC098] = 0xBF;
        m.Run((long)(CpuClock * 2), untilWaiting: true);
        return m;
    }

    /// <summary>"00 14 ab" → 바이트 (16진이 아니면 null).</summary>
    private static byte[] HexBytes(string s)
    {
        var parts = (s ?? "").Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var b = new byte[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i].StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? parts[i].Substring(2) : parts[i];
            if (p.Length == 0 || p.Length > 2 || !byte.TryParse(p, System.Globalization.NumberStyles.HexNumber, null, out b[i])) return null;
        }
        return b;
    }

    /// <summary>MSX 기계 (hoot 범용 드라이버 흉내).</summary>
    public sealed class Machine : IZ80Bus
    {
        public readonly byte[] Ram = new byte[0x10000];
        public readonly Z80 Cpu;
        public bool Opll, SccAlways;
        /// <summary>hoot ds4.cpp 롬 (msx ds4), 아니면 null.</summary>
        public byte[] Ds4Rom;
        /// <summary>SCC+ 사운드 카트리지 RAM 128KB (sccp 줄), 아니면 null. 4000h~BFFFh 중 A8h 슬롯이 3 (본체 RAM)이 아닌 페이지에 8KB 뱅크 4개로.</summary>
        public byte[] Cart;
        private readonly int[] cartBank = { 0, 1, 2, 3 };
        private int cartMode;

        private bool CartAt(int a) { return Cart != null && a >= 0x4000 && a < 0xC000 && ((slot >> (2 * (a >> 14))) & 3) != 3; }
        private int CartIndex(int a) { return ((cartBank[(a - 0x4000) >> 13] & 0x0F) * 0x2000 + (a & 0x1FFF)) % Cart.Length; }
        private int ds4Bank8 = 2, ds4BankA = 3;
        public int DataAddr, DataSize, FileSize;
        public Func<int, byte[]> Bgm = n => null;
        public readonly List<string> Missing = new List<string>();
        public readonly HashSet<string> Unsupported = new HashSet<string>();
        /// <summary>칩 쓰기 (시간 = 44100Hz 샘플, 재생 명령부터) · VGM 명령.</summary>
        public readonly List<(double t, byte[] cmd)> Log = new List<(double, byte[])>();
        public bool UsedPsg, UsedScc, UsedSccPlus, UsedOpll, UsedAudio;
        public int Loads, Commands;

        private int psgAddr, opllAddr, audioAddr, slot;
        private readonly byte[] psgReg = new byte[16];
        private bool sccOn, sccPlusMode;
        private int command;
        private int code;
        private bool vdpPending;
        private long playStart = -1;
        private int waitReads;

        public Machine() { Cpu = new Z80(this); }

        // 뱅크 카트리지 (FM-PAC 64KB = 16KB × 4, 7FF7h = 뱅크): 첫 뱅크만 보이고 바꾸면 그 16KB를 다시 놓는다
        private byte[] banked;
        private int bankAt = -1;

        /// <summary>주소 공간을 넘는 rom: 16KB 뱅크 카트리지로 (보이는 곳 = at~at+16KB, at+3FF7h에 쓰면 뱅크).</summary>
        public void PutBanked(int at, byte[] d)
        {
            banked = d;
            bankAt = at & 0xC000;
            SelectBank(0);
        }

        private void SelectBank(int n)
        {
            if (banked == null) return;
            int banks = Math.Max(1, banked.Length / 0x4000);
            int src = (n % banks) * 0x4000;
            for (int i = 0; i < 0x4000 && bankAt + i < 0x10000; i++) Ram[bankAt + i] = src + i < banked.Length ? banked[src + i] : (byte)0xFF;
        }

        public void Put(int at, byte[] d)
        {
            for (int i = 0; i < d.Length && at + i < 0x10000; i++) if (at + i >= 0) Ram[at + i] = d[i];
        }

        /// <summary>작은 BIOS (곡 코드가 덮어쓸 수 있다: 시작 코드가 0038h · 93h 등을 고친다).</summary>
        public void InstallBios()
        {
            // hoot kss.cpp: BIOS 영역을 RET (C9h)으로 채운 뒤 필요한 것만 — 없는 BIOS를 부르면 NOP으로 미끄러져 시작 코드를 다시 돌던 문제
            for (int i = 0; i < 0x4000; i++) Ram[i] = 0xC9;
            Ram[0x06] = 0x98; Ram[0x07] = 0x98;                  // BIOS 자료: VDP 읽기 · 쓰기 포트
            for (int i = 0x2B; i <= 0x2F; i++) Ram[i] = 0;       // 문자 · 60Hz · MSX 판 등
            // 0038h: JP 0060h — 처리기: 모든 레지스터 저장, IN A,(99h), CALL FD9Ah, CALL FD9Fh, 되돌리기, EI, RET
            Put(0x38, new byte[] { 0xC3, 0x60, 0x00 });
            Put(0x60, new byte[]
            {
                0xF5, 0xC5, 0xD5, 0xE5, 0x08, 0xD9, 0xF5, 0xC5, 0xD5, 0xE5, 0xDD, 0xE5, 0xFD, 0xE5,
                0x2A, 0x9E, 0xFC, 0x23, 0x22, 0x9E, 0xFC,   // JIFFY (FC9Eh) += 1 (BIOS KEYINT)
                0xDB, 0x99, 0xCD, 0x9A, 0xFD, 0xCD, 0x9F, 0xFD,
                0xFD, 0xE1, 0xDD, 0xE1, 0xE1, 0xD1, 0xC1, 0xF1, 0xD9, 0x08, 0xE1, 0xD1, 0xC1, 0xF1,
                0xFB, 0xC9,
            });
            Put(0x0C, new byte[] { 0x7E, 0xC9 });                // RDSLT: A = (HL)
            Put(0x14, new byte[] { 0x73, 0xC9 });                // WRSLT: (HL) = E
            Put(0x1C, new byte[] { 0xDD, 0xE9 });                // CALSLT: JP (IX)
            Put(0x24, new byte[] { 0xC9 });                      // ENASLT
            Put(0x90, new byte[] { 0xC9 });                      // GICINI
            Put(0x93, new byte[] { 0xC3, 0x90, 0x03 });          // WRTPSG → 0390h (A0h~A7h는 CHPUT · LPTOUT 자리)
            Put(0x96, new byte[] { 0xD3, 0xA0, 0xDB, 0xA2, 0xC9 });   // RDPSG
            Put(0x0390, new byte[] { 0xD3, 0xA0, 0xF5, 0x7B, 0xD3, 0xA1, 0xF1, 0xC9 });
            Put(0xD5, new byte[] { 0xAF, 0xC9 });                // GTSTCK: 0
            Put(0xD8, new byte[] { 0xAF, 0xC9 });                // GTTRIG: 안 눌림
            Put(0x0138, new byte[] { 0xDB, 0xA8, 0xC9 });        // RSLREG
            Put(0x013B, new byte[] { 0xD3, 0xA8, 0xC9 });        // WSLREG
            Put(0x013E, new byte[] { 0xDB, 0x99, 0xC9 });        // RDVDP
            Put(0x0141, new byte[] { 0x3E, 0xFF, 0xC9 });        // SNSMAT: 키 없음
            // CALLF (RST 30h + 슬롯 바이트 + 주소): 슬롯은 무시하고 주소로 (CALSLT처럼 IX를 씀)
            Put(0x30, new byte[] { 0xC3, 0x80, 0x03 });
            Put(0x0380, new byte[] { 0xE3, 0x23, 0xD5, 0x5E, 0x23, 0x56, 0x23, 0xD5, 0xDD, 0xE1, 0xD1, 0xE3, 0xDD, 0xE9 });
            Put(0xFD9A, new byte[] { 0xC9, 0xC9, 0xC9, 0xC9, 0xC9 });   // H.KEYI
            Put(0xFD9F, new byte[] { 0xC9, 0xC9, 0xC9, 0xC9, 0xC9 });   // H.TIMI
        }

        public void Start(int pc)
        {
            // hoot (RAZE z80_reset)처럼 레지스터 0으로 시작
            Cpu.A = Cpu.F = Cpu.B = Cpu.C = Cpu.D = Cpu.E = Cpu.H = Cpu.L = 0;
            Cpu.IX = Cpu.IY = 0;
            Cpu.PC = pc;
            Cpu.SP = 0xF380;
            Cpu.Im = 1;
        }

        /// <summary>재생 명령 (곡 코드 = 포트 03h~06h).</summary>
        public void Play(int songCode)
        {
            bool ds4Effect = Ds4Rom != null && (songCode & 0x80) != 0;
            if (ds4Effect)
            {
                // ds4.cpp: 80h 이상 = 효과음 칸 (C095h)에 — IPL은 곡을 한 번 튼 뒤에야 EI하므로 효과음만 틀 때는 인터럽트를 켠다
                Ram[0xC095] = (byte)(songCode & 0x7F);
                Cpu.Iff1 = Cpu.Iff2 = true;
            }
            code = ds4Effect ? 0 : songCode;
            command = ds4Effect ? 0 : 1;
            Commands++;
            // 재생 전 쓰기(부팅 중 칩 설정)는 0초로 남긴다
            var init = Log.Select(x => (0.0, x.cmd)).ToList();
            Log.Clear();
            Log.AddRange(init);
            playStart = Cpu.Cycles;
        }

        /// <summary>시작 코드가 명령을 기다리는 중인가 (명령 포트를 몇 번 연달아 읽었다).</summary>
        public bool Waiting { get { return waitReads >= 4; } }

        private long nextFrame;
        private readonly double frameCycles = CpuClock / FrameRate;

        /// <summary>cycles만큼 돌린다 (프레임마다 VDP 인터럽트). untilWaiting이면 명령을 기다리기 시작하면 멈춘다.</summary>
        public void Run(long cycles, bool untilWaiting = false)
        {
            long end = Cpu.Cycles + cycles;
            if (nextFrame == 0) nextFrame = Cpu.Cycles + (long)frameCycles;
            while (Cpu.Cycles < end)
            {
                if (Cpu.Cycles >= nextFrame)
                {
                    nextFrame += (long)frameCycles;
                    vdpPending = true;
                }
                if (vdpPending && Cpu.Iff1) Interrupt();
                if (Cpu.Halted)
                {
                    // 다음 인터럽트까지 건너뛴다 (인터럽트가 꺼져 있으면 영영 멈춤: 끝까지)
                    long to = Cpu.Iff1 ? Math.Min(end, nextFrame) : end;
                    if (to > Cpu.Cycles) Cpu.Cycles = to;
                    continue;
                }
                Cpu.Step();
                if (untilWaiting && Waiting) return;
            }
        }

        public void RunSeconds(double seconds) { Run((long)(CpuClock * seconds)); }

        private void Interrupt()
        {
            vdpPending = false;
            Cpu.Halted = false;
            Cpu.Iff1 = Cpu.Iff2 = false;
            Cpu.SP = (Cpu.SP - 2) & 0xFFFF;
            Ram[Cpu.SP] = (byte)Cpu.PC; Ram[(Cpu.SP + 1) & 0xFFFF] = (byte)(Cpu.PC >> 8);
            if (Cpu.Im == 2)
            {
                int v = ((Cpu.I << 8) | 0xFF) & 0xFFFF;
                Cpu.PC = Ram[v] | (Ram[(v + 1) & 0xFFFF] << 8);
            }
            else Cpu.PC = 0x38;
            Cpu.Cycles += 13;
        }

        private void Emit(params byte[] cmd)
        {
            if (playStart < 0) playStart = Cpu.Cycles;   // 재생 전 쓰기 = 처음 설정 (0초에)
            double t = Math.Max(0, (Cpu.Cycles - playStart) * VgmSong.Rate / CpuClock);
            Log.Add((t, cmd));
        }

        public byte Read(int a)
        {
            a &= 0xFFFF;
            if (Ds4Rom != null && a >= 0x4000 && a < 0xC000)
            {
                if (a < 0x8000) return Ds4Rom[a - 0x4000];
                int bank = a < 0xA000 ? ds4Bank8 : ds4BankA;
                return Ds4Rom[(bank * 0x2000 + (a & 0x1FFF)) % Ds4Rom.Length];
            }
            if (CartAt(a)) return Cart[CartIndex(a)];
            return Ram[a];
        }

        public void Write(int a, byte v)
        {
            if (Ds4Rom != null)
            {
                a &= 0xFFFF;
                if (a == 0x7000) ds4Bank8 = v; else if (a == 0x7800) ds4BankA = v;
                else if (a < 0x4000 || a >= 0xC000) Ram[a] = v;
                return;
            }
            a &= 0xFFFF;
            if (CartAt(a)) { CartWrite(a, v); return; }
            if (banked != null && a >= bankAt && a < bankAt + 0x4000)
            {
                if (a == bankAt + 0x3FF7) SelectBank(v & 3);   // FM-PAC 뱅크
                return;                                        // ROM: 쓰지 않음
            }
            if (a >= 0x9000 && a <= 0x97FF) sccOn = (v & 0x3F) == 0x3F;
            else if (a == 0xBFFE || a == 0xBFFF) sccPlusMode = (v & 0x20) != 0;
            if ((sccOn || SccAlways) && !sccPlusMode && a >= 0x9800 && a <= 0x989F) { UsedScc = true; Emit(0xD2, SccPort(a - 0x9800, out int r), (byte)r, v); }
            else if (sccPlusMode && a >= 0xB800 && a <= 0xB8AF) { UsedSccPlus = true; Emit(0xD2, SccPlusPort(a - 0xB800, out int r), (byte)r, v); }
            Ram[a] = v;
        }

        /// <summary>SCC+ 카트리지 쓰기: 뱅크 레지스터 5000h · 7000h · 9000h · B000h (~7FFh), 모드 BFFEh, SCC 9800h~ (뱅크 2 = 3Fh) · SCC+ B800h~ (모드 bit5 · 뱅크 3 bit7), RAM (모드 bit4 = 모두, bit0~2 = 뱅크 0~2).</summary>
        private void CartWrite(int a, byte v)
        {
            int page = (a - 0x4000) >> 13;
            if ((a & 0x1800) == 0x1000)
            {
                cartBank[page] = v;
                if (page == 2) sccOn = (v & 0x3F) == 0x3F;
                return;
            }
            if (a == 0xBFFE || a == 0xBFFF) { cartMode = v; sccPlusMode = (v & 0x20) != 0; return; }
            if ((sccOn || SccAlways) && !sccPlusMode && a >= 0x9800 && a <= 0x98FF)
            {
                int off = a & 0xFF;
                if (off < 0xA0) { UsedScc = true; Emit(0xD2, SccPort(off, out int r), (byte)r, v); }
                return;
            }
            if (sccPlusMode && (cartBank[3] & 0x80) != 0 && a >= 0xB800 && a <= 0xB8FF)
            {
                int off = a & 0xFF;
                if (off < 0xB0) { UsedSccPlus = true; Emit(0xD2, SccPlusPort(off, out int r), (byte)r, v); }
                return;
            }
            bool writable = (cartMode & 0x10) != 0 || (page < 3 && (cartMode & (1 << page)) != 0);
            if (writable) Cart[CartIndex(a)] = v;
        }

        private static byte SccPort(int off, out int r)
        {
            if (off < 0x80) { r = off; return 0; }
            off &= 0x8F;
            if (off < 0x8A) { r = off - 0x80; return 1; }
            if (off < 0x8F) { r = off - 0x8A; return 2; }
            r = 0; return 3;
        }

        private static byte SccPlusPort(int off, out int r)
        {
            if (off < 0xA0) { r = off; return 4; }
            off &= 0xAF;
            if (off < 0xAA) { r = off - 0xA0; return 1; }
            if (off < 0xAF) { r = off - 0xAA; return 2; }
            r = 0; return 3;
        }

        public byte In(int port)
        {
            port &= 0xFF;
            if (Ds4Rom != null)
            {
                // ds4.cpp: 00h = 재생 깃발 (읽으면 지움), 01h = 곡, 02h = 쉼 (기다리는 고리)
                if (port == 0x00) { int c = command; command = 0; return (byte)c; }
                if (port == 0x01) return (byte)code;
                if (port == 0x02) { waitReads++; return 0; }
                waitReads = 0;
                if (port == 0xA2) return psgAddr == 14 ? (byte)0xFF : psgReg[psgAddr];
                return 0xFF;
            }
            if (port != 0x02) waitReads = 0;
            switch (port)
            {
                case 0x02:
                    {
                        int c = command;
                        command = 0;   // 읽으면 비운다 (재생 뒤 다시 기다리는 고리)
                        if (c == 0) waitReads++; else waitReads = 0;
                        return (byte)c;
                    }
                case 0x03: return (byte)code;
                case 0x04: return (byte)(code >> 8);
                case 0x05: return (byte)(code >> 16);
                case 0x06: return (byte)(code >> 24);
                case 0x07: return (byte)(Opll ? 1 : 0);
                case 0x99: vdpPending = false; return 0x80;   // VDP 상태: 수직 귀선
                case 0xA2: return psgAddr == 14 ? (byte)0xFF : psgReg[psgAddr];   // PSG 읽기 = 레지스터 값 (hoot ssAY8910), R14 = 조이스틱 안 눌림
                case 0xA8: return (byte)slot;
                default: return 0xFF;
            }
        }

        public void Out(int port, byte v)
        {
            port &= 0xFF;
            switch (port)
            {
                case 0x02: command = 0; break;
                case 0x03: Load(v); break;
                case 0xA0: psgAddr = v & 0x0F; break;
                case 0xA1: UsedPsg = true; psgReg[psgAddr] = v; Emit(0xA0, (byte)psgAddr, v); break;
                case 0x7C: opllAddr = v; break;
                case 0x7D: UsedOpll = true; Emit(0x51, (byte)opllAddr, v); break;
                case 0xC0: audioAddr = v; break;
                case 0xC1: UsedAudio = true; Emit(0x5C, (byte)audioAddr, v); break;
                case 0xA8: slot = v; break;
            }
        }

        /// <summary>OUT 03h,n: bgm n을 mdata_addr에 (mdata_size까지 0으로 채운 뒤 mfile_size까지 복사).</summary>
        private void Load(int n)
        {
            var d = Bgm(n);
            if (d == null || DataAddr < 0) return;
            for (int i = 0; i < DataSize && DataAddr + i < 0x10000; i++) Ram[DataAddr + i] = 0;
            int len = FileSize > 0 ? Math.Min(FileSize, d.Length) : d.Length;
            for (int i = 0; i < len && DataAddr + i < 0x10000; i++) Ram[DataAddr + i] = d[i];
            Loads++;
        }

        /// <summary>재생 명령부터 seconds초의 칩 쓰기 → VGM (끝 fade초 동안 줄임).</summary>
        public VgmSong ToVgm(double seconds, double fade)
        {
            var w = new VgmWriter { System = "MSX (hoot generic)", Notes = "hoot msx generic" };
            if (UsedPsg) { w.SetClock(VgmWriter.ClockAy8910, CpuClock / 2); w.SetHeaderByte(0x78, 0x00); }
            if (UsedScc || UsedSccPlus) w.SetClock(VgmWriter.ClockK051649, CpuClock, UsedSccPlus ? 0x80000000u : 0);
            if (UsedOpll) w.SetClock(VgmWriter.ClockYm2413, CpuClock);
            if (UsedAudio) w.SetClock(VgmWriter.ClockY8950, CpuClock);
            double total = seconds * VgmSong.Rate;
            long at = 0;
            foreach (var (time, cmd) in Log.OrderBy(x => x.t))
            {
                long ts = (long)Math.Min(total, Math.Max(0, time));
                if (ts > at) { w.Wait(ts - at); at = ts; }
                w.Write(cmd);
            }
            if ((long)total > at) w.Wait((long)total - at);
            var v = VgmFormat.Read(w.ToArray());
            v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
            if (!UsedPsg && !UsedScc && !UsedSccPlus && !UsedOpll && !UsedAudio) v.UnsupportedChips.Add("(칩 쓰기 없음)");
            return v;
        }
    }
}
