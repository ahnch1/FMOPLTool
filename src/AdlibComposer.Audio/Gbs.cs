using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Audio.Cpu;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio;

/// <summary>
/// GBS · GBR (게임보이 사운드 드라이버 덤프): 드라이버를 LR35902(<see cref="Lr35902"/>)로 돌려 APU 쓰기($FF10~$FF3F)를
/// 시간과 함께 모아 VGM(<see cref="VgmSong"/>, Game Boy DMG 0xB3)으로 바꾼다.
/// <para>GBS (판 1, 0x70바이트 머리말): 0x04 곡 수, 0x05 첫 곡(1부터), 0x06 읽어 둘 주소, 0x08 init, 0x0A play, 0x0C 스택, 0x0E TMA, 0x0F TAC,
/// 0x10 제목 · 0x30 작곡 · 0x50 저작권 (32바이트씩). 데이터는 읽어 둘 주소부터, RST n = 읽어 둘 주소 + n. 뱅크 = $2000~$3FFF 쓰기 (16KB, $4000~$7FFF).
/// 재생 간격 = TAC bit2가 켜져 있으면 타이머 ((256 − TMA) × 1024 · 16 · 64 · 256 클럭, bit7 = 컬러 두 배 속도 → 반), 아니면 VBlank (70224 클럭 ≈ 59.7Hz).
/// init은 A = 곡 번호(0부터)로 부르고, 그 뒤 간격마다 play를 부른다. 드라이버가 TMA · TAC를 바꾸면 간격도 따라 바꾼다.</para>
/// <para>GBR ('GBRF', 0x20바이트 머리말 + 16KB 뱅크들 · 0번 = $0000~): 0x07 인터럽트 (bit0 VBlank, bit1 타이머), 0x08 init, 0x0A VBlank, 0x0C 타이머 주소,
/// 0x0E TMA, 0x0F TAC. 곡 수가 없어 곡 0~255를 돌려 소리 나는 곡만 고른다 (HES와 같은 방법). 뱅크는 원래 게임의 번호로 바꾸므로
/// 파일 뱅크 첫 바이트(드라이버가 적어 둔 자기 번호)로 찾는다. GBR 뱅크 쓰기는 MBC5 방식 ($2000~$2FFF 아래 8비트, $3000~ bit8).</para>
/// <para>곡 길이 = 같은 이름의 .m3u (<see cref="NezM3u"/>, 종류 GBS, 번호 0부터), 없으면 2분.</para>
/// </summary>
public static class Gbs
{
    public static readonly string[] Extensions = { ".gbs", ".gbr" };
    public const double DefaultSeconds = 120, DefaultFade = 4;
    public const double CpuClock = 4194304;
    public const int VBlankCycles = 70224;
    /// <summary>GBR 스택 (머리말에 없음): 게임은 높은 RAM($FF80~)에 변수를 두기도 하므로 사운드 드라이버가 쓰지 않는 VRAM 끝에 둔다.</summary>
    public const int GbrStack = 0xA000;

    public sealed class Header
    {
        public int Songs, FirstSong, LoadAddr, InitAddr, PlayAddr, Stack, Tma, Tac;
        /// <summary>GBR: 타이머 때 부를 주소 (VBlank 주소는 <see cref="PlayAddr"/>).</summary>
        public int TimerAddr = -1;
        /// <summary>GBR: bit0 VBlank, bit1 타이머.</summary>
        public int Interrupts;
        public string Title = "", Author = "", Copyright = "";
        /// <summary>$0000부터의 ROM 그림 (16KB 단위).</summary>
        public byte[] Rom;
        public bool Gbr;
        /// <summary>GBR: 쓴 뱅크 번호 → 파일 뱅크.</summary>
        public readonly Dictionary<int, int> BankIds = new Dictionary<int, int>();
        public int BankCount { get { return Rom.Length / 0x4000; } }
        public bool UsesTimer { get { return Gbr ? (Interrupts & 2) != 0 : (Tac & 4) != 0; } }
    }

    public sealed class Track
    {
        /// <summary>곡 번호 (0부터, init에 넘기는 값).</summary>
        public int Song;
        public string Title = "";
        public double Seconds = DefaultSeconds, Fade = DefaultFade;
    }

    public static bool IsGbr(byte[] d) { return d.Length >= 0x20 && d[0] == 'G' && d[1] == 'B' && d[2] == 'R' && d[3] == 'F'; }

    public static Header ReadHeader(byte[] d)
    {
        if (IsGbr(d)) return ReadGbr(d);
        if (d.Length < 0x70 || d[0] != 'G' || d[1] != 'B' || d[2] != 'S') throw new InvalidDataException("GBS 파일이 아닙니다 (머리말 GBS가 없음).");
        if (d[3] != 1) throw new InvalidDataException("GBS 판 " + d[3] + "은 모릅니다 (판 1만).");
        var h = new Header
        {
            Songs = Math.Max(1, (int)d[4]), FirstSong = Math.Max(1, (int)d[5]),
            LoadAddr = U16(d, 6), InitAddr = U16(d, 8), PlayAddr = U16(d, 0x0A), Stack = U16(d, 0x0C),
            Tma = d[0x0E], Tac = d[0x0F],
            Title = Text(d, 0x10), Author = Text(d, 0x30), Copyright = Text(d, 0x50),
        };
        int dataLen = d.Length - 0x70;
        int size = Math.Max(0x8000, ((h.LoadAddr + dataLen + 0x3FFF) / 0x4000) * 0x4000);
        h.Rom = new byte[size];
        Array.Copy(d, 0x70, h.Rom, h.LoadAddr, Math.Min(dataLen, size - h.LoadAddr));
        // RST 0x00~0x38 → 읽어 둘 주소 + n (그 앞이 비어 있을 때)
        for (int v = 0; v <= 0x38; v += 8)
            if (v + 3 <= h.LoadAddr) { int to = h.LoadAddr + v; h.Rom[v] = 0xC3; h.Rom[v + 1] = (byte)to; h.Rom[v + 2] = (byte)(to >> 8); }
        return h;
    }

    private static Header ReadGbr(byte[] d)
    {
        var h = new Header
        {
            Gbr = true, Songs = 256, FirstSong = 1,
            Interrupts = d[7], InitAddr = U16(d, 8), PlayAddr = U16(d, 0x0A), TimerAddr = U16(d, 0x0C),
            Tma = d[0x0E], Tac = d[0x0F], Stack = GbrStack,
        };
        if ((h.Interrupts & 3) == 0) h.Interrupts = 1;
        int n = Math.Max(2, (d.Length - 0x20 + 0x3FFF) / 0x4000);
        h.Rom = new byte[n * 0x4000];
        Array.Copy(d, 0x20, h.Rom, 0, d.Length - 0x20);
        // 뱅크 첫 바이트 = 원래 게임의 뱅크 번호 (겹치지 않을 때만)
        var ids = new Dictionary<int, int>();
        var dup = new HashSet<int>();
        for (int b = 1; b < n; b++)
        {
            int id = h.Rom[b * 0x4000];
            if (ids.ContainsKey(id)) dup.Add(id); else ids[id] = b;
        }
        foreach (var kv in ids) if (!dup.Contains(kv.Key)) h.BankIds[kv.Key] = kv.Value;
        return h;
    }

    private static string Text(byte[] d, int at)
    {
        int n = 0;
        while (n < 32 && d[at + n] != 0) n++;
        string s = KoreanText.ShiftJis.GetString(d, at, n).Trim();
        return s == "<?>" ? "" : s;
    }

    // ------------------------------------------------------------ 곡 목록 · 열기

    private static readonly Dictionary<string, List<int>> probed = new Dictionary<string, List<int>>();

    public static List<Track> Tracks(string path)
    {
        var list = new List<Track>();
        foreach (var e in NezM3u.Read(path, "GBS"))
        {
            var t = new Track { Song = Math.Max(0, e.Number), Title = e.Title };
            if (e.Seconds > 0) t.Seconds = e.Seconds;
            if (e.Fade >= 0) t.Fade = e.Fade;
            list.Add(t);
        }
        if (list.Count > 0) return list;
        Header h;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(path); h = ReadHeader(bytes); }
        catch (Exception) { list.Add(new Track()); return list; }
        string name = h.Title.Length > 0 ? h.Title : Path.GetFileNameWithoutExtension(path);
        if (h.Gbr)
        {
            foreach (int s in SoundingSongs(path, bytes)) list.Add(new Track { Song = s, Title = name + " #" + s.ToString("X2") });
            if (list.Count == 0) list.Add(new Track { Title = name });
            return list;
        }
        for (int s = 0; s < h.Songs; s++) list.Add(new Track { Song = s, Title = name + " #" + (s + 1) });
        return list;
    }

    private static string CacheKey(string path)
    {
        var fi = new FileInfo(path);
        return fi.FullName + "|" + (fi.Exists ? fi.Length + "|" + fi.LastWriteTimeUtc.Ticks : "");
    }

    /// <summary>곡 목록을 이미 알거나 바로 알 수 있는가 (GBR은 처음에 곡을 돌려 찾아야 한다).</summary>
    public static bool TracksKnown(string path)
    {
        if (File.Exists(Path.ChangeExtension(path, ".m3u"))) return true;
        if (!string.Equals(Path.GetExtension(path), ".gbr", StringComparison.OrdinalIgnoreCase)) return true;
        lock (probed) return probed.ContainsKey(CacheKey(path));
    }

    /// <summary>GBR: 곡 0~255를 1.5초씩 돌려 소리를 내는 곡만 (앞부분 APU 쓰기가 앞 곡과 똑같으면 같은 곡으로 보고 뺀다). 파일마다 한 번.</summary>
    public static List<int> SoundingSongs(string path, byte[] bytes)
    {
        string key = CacheKey(path);
        lock (probed)
            if (probed.TryGetValue(key, out var hit)) return hit;
        var l = new List<int>();
        try
        {
            var sig = new ulong?[256];
            System.Threading.Tasks.Parallel.For(0, 256, s => { sig[s] = Signature(bytes, s, 1.5); });
            var seen = new HashSet<ulong>();
            for (int s = 0; s < 256; s++)
                if (sig[s].HasValue && seen.Add(sig[s].Value)) l.Add(s);
        }
        catch (Exception) { }
        lock (probed) probed[key] = l;
        return l;
    }

    /// <summary>곡을 secs초 돌려 소리 나는 쓰기(트리거)가 있으면 쓰기 흐름의 지문, 없으면 null.</summary>
    private static ulong? Signature(byte[] file, int song, double secs)
    {
        var h = ReadHeader(file);
        var m = Run(h, song, secs);
        if (!m.Sounded) return null;
        ulong x = 1469598103934665603;
        foreach (var (t, r, v) in m.Log) { x = (x ^ (ulong)((r << 8) | v)) * 1099511628211; x = (x ^ (ulong)(t / 4096)) * 1099511628211; }
        return x;
    }

    /// <summary>"파일.gbs" 또는 "파일.gbs::n" (n = 곡 목록 안 번호, 0부터).</summary>
    public static VgmSong Load(string path)
    {
        path = NezM3u.SplitTrack(path, out int track);
        var tracks = Tracks(path);
        var t = tracks[Math.Max(0, Math.Min(track, tracks.Count - 1))];
        var bytes = File.ReadAllBytes(path);
        var h = ReadHeader(bytes);
        var v = Render(bytes, t.Song, t.Seconds, t.Fade);
        v.FilePath = path;
        v.TitleEn = t.Title.Length > 0 ? t.Title : Path.GetFileNameWithoutExtension(path) + " #" + (t.Song + 1);
        v.GameEn = h.Title;
        v.AuthorEn = h.Author;
        v.SourceFormat = (h.Gbr ? "GBR" : "GBS") + " · 곡 " + (h.Gbr ? "$" + t.Song.ToString("X2") : (t.Song + 1).ToString()) + " (" + (track + 1) + "/" + tracks.Count + ")";
        return v;
    }

    // ------------------------------------------------------------ 돌리기

    private sealed class Machine : IGbBus
    {
        private readonly Header h;
        private readonly byte[] ram = new byte[0x10000];   // $8000~$FFFF (ROM 자리는 안 씀)
        private int bankOfs = 0x4000, bankLow = 1, bankHigh;
        public Lr35902 Cpu;
        public readonly List<(long t, int reg, int val)> Log = new List<(long, int, int)>();
        public readonly GbApu Apu = new GbApu();
        public bool Sounded, TimerChanged;

        public Machine(Header h) { this.h = h; }

        public byte Read(int a)
        {
            if (a < 0x4000) return h.Rom[a];
            if (a < 0x8000) { int o = bankOfs + a - 0x4000; return o < h.Rom.Length ? h.Rom[o] : (byte)0xFF; }
            if (a >= 0xFF10 && a < 0xFF40) return (byte)Apu.Read(a - 0xFF10);
            if (a == 0xFF04) return (byte)(Cpu.Cycles >> 8);
            if (a == 0xFF44) return (byte)(Cpu.Cycles / 456 % 154);
            if (a == 0xFF00) return 0xCF;
            return ram[a];
        }

        public void Write(int a, byte v)
        {
            if (a < 0x8000)
            {
                // GBS: $2000~$3FFF = 뱅크. GBR (원래 롬, 컬러 게임은 MBC5): $2000~$2FFF = 아래 8비트, $3000~$3FFF = bit8
                if (h.Gbr && a >= 0x3000 && a < 0x4000) { bankHigh = v & 1; SetBank(bankLow); }
                else if (a >= 0x2000 && a < 0x4000) { bankLow = v; SetBank(v); }
                return;
            }
            if (a >= 0xFF10 && a < 0xFF40)
            {
                int r = a - 0xFF10;
                Apu.Write(0, r, v);
                Log.Add((Cpu.Cycles, r, v));
                if (r < 0x14 && r % 5 == 4 && (v & 0x80) != 0 && Apu.ChannelLevel(r / 5) > 0) Sounded = true;
                return;
            }
            if (a == 0xFF06 || a == 0xFF07) TimerChanged = true;
            ram[a] = v;
        }

        private void SetBank(int v)
        {
            int n = h.BankCount;
            int b;
            if (h.Gbr && bankHigh == 0 && h.BankIds.TryGetValue(v, out int mapped)) b = mapped;
            else { b = v == 0 ? 1 : v; if (b >= n) b %= n; if (b == 0) b = 1; }
            bankOfs = b * 0x4000;
        }

        public int Tma { get { return ram[0xFF06]; } }
        public int Tac { get { return ram[0xFF07]; } }
    }

    /// <summary>돌아올 곳 (함정): 여기로 RET하면 호출이 끝난 것 (I/O 자리라 코드가 없다).</summary>
    private const int Trap = 0xFF7E;

    /// <summary>재생 간격 (클럭). 타이머 = (256 − TMA) × 나눔 (TAC bit7 = 반), 아니면 VBlank.</summary>
    private static long Period(int tma, int tac, bool timer)
    {
        if (!timer) return VBlankCycles;
        int[] shift = { 10, 4, 6, 8 };
        int sh = shift[tac & 3] - ((tac & 0x80) != 0 ? 1 : 0);
        return (long)(256 - (tma & 0xFF)) << sh;
    }

    private static Machine Run(Header h, int song, double seconds)
    {
        var m = new Machine(h);
        var cpu = new Lr35902(m);
        m.Cpu = cpu;
        cpu.Cycles = 0;
        // APU 처음 상태 (전원 → 소리 레지스터 → 파형)
        byte[] init =
        {
            0x80, 0xBF, 0x00, 0x00, 0xBF, 0x00, 0x3F, 0x00, 0x00, 0xBF, 0x7F, 0xFF, 0x9F, 0x00, 0xBF, 0x00, 0xFF, 0x00, 0x00, 0xBF, 0x77, 0xF3,
        };
        m.Write(0xFF26, 0x80);
        for (int i = 0; i < init.Length; i++) m.Write(0xFF10 + i, init[i]);
        byte[] waveInit = { 0xAC, 0xDD, 0xDA, 0x48, 0x36, 0x02, 0xCF, 0x16, 0x2C, 0x04, 0xE5, 0x2C, 0xAC, 0xDD, 0xDA, 0x48 };
        for (int i = 0; i < 16; i++) m.Write(0xFF30 + i, waveInit[i]);
        m.Write(0xFF06, (byte)h.Tma); m.Write(0xFF07, (byte)h.Tac);
        m.TimerChanged = false;
        m.Sounded = false;

        Call(cpu, h.Stack, h.InitAddr, song, (long)CpuClock * 2);
        long total = (long)(seconds * CpuClock);
        bool timer = h.UsesTimer;
        long period = Period(m.Tma, m.Tac, timer);
        int play = h.Gbr && timer ? h.TimerAddr : h.PlayAddr;
        long next = cpu.Cycles;
        while (next < total)
        {
            cpu.Cycles = Math.Max(cpu.Cycles, next);
            Call(cpu, h.Stack, play, -1, period);
            if (h.Gbr && (h.Interrupts & 3) == 3) Call(cpu, h.Stack, h.TimerAddr, -1, period);
            if (m.TimerChanged && timer) { period = Period(m.Tma, m.Tac, true); m.TimerChanged = false; }
            next += period;
        }
        return m;
    }

    /// <summary>곡 하나(song = 0부터)를 seconds초 돌려 VGM으로.</summary>
    public static VgmSong Render(byte[] file, int song, double seconds, double fade = DefaultFade)
    {
        var h = ReadHeader(file);
        var m = Run(h, song, seconds);
        var w = new VgmWriter { System = "Game Boy (" + (h.Gbr ? "GBR" : "GBS") + ")", Notes = h.Gbr ? "GBR" : "GBS", Title = h.Title, Author = h.Author };
        w.SetClock(VgmWriter.ClockGbDmg, CpuClock);
        double total = seconds * VgmSong.Rate;
        long at = 0;
        foreach (var (t, r, val) in m.Log)
        {
            long ts = (long)Math.Min(total, Math.Max(0, t * VgmSong.Rate / CpuClock));
            if (ts > at) { w.Wait(ts - at); at = ts; }
            w.Write((byte)0xB3, r, val);
        }
        if ((long)total > at) w.Wait((long)total - at);
        var v = VgmFormat.Read(w.ToArray());
        v.FadeSeconds = Math.Max(0, Math.Min(fade, seconds / 2));
        if (!m.Sounded) v.UnsupportedChips.Add("(소리 쓰기 없음)");
        if (m.Cpu.UnknownOps > 0) v.UnsupportedChips.Add("(LR35902 없는 명령 " + m.Cpu.UnknownOps + ")");
        return v;
    }

    /// <summary>진단: 곡을 돌린 뒤 CPU · 쓰기 · APU 레지스터 상태.</summary>
    public static string Describe(byte[] file, int song, double secs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var h = ReadHeader(file);
        var m = Run(h, song, secs);
        var c = m.Cpu;
        var counts = m.Log.GroupBy(e => e.reg).OrderBy(g => g.Key).Select(g => g.Key.ToString("X2") + ":" + g.Count() + "=" + g.Last().val.ToString("X2"));
        return (h.Gbr ? "GBR" : "GBS") + " load " + h.LoadAddr.ToString("X4") + " init " + h.InitAddr.ToString("X4") + " play " + h.PlayAddr.ToString("X4") + " TMA " + h.Tma.ToString("X2") + " TAC " + h.Tac.ToString("X2") +
            " banks " + h.BankCount + " | PC " + c.PC.ToString("X4") + " SP " + c.SP.ToString("X4") + (c.Halted ? " halted" : "") + (c.Jammed ? " jammed" : "") + " unknown " + c.UnknownOps +
            " | writes " + m.Log.Count + " sounded " + m.Sounded + " | " + string.Join(" ", counts) + " | " + sw.ElapsedMilliseconds + " ms";
    }

    /// <summary>addr를 부르고 함정으로 돌아오거나 한도까지 돌린다. a ≥ 0이면 A = a.</summary>
    private static void Call(Lr35902 cpu, int sp, int addr, int a, long maxCycles)
    {
        if (a >= 0) cpu.A = a & 0xFF;
        cpu.SP = sp & 0xFFFF;
        cpu.Halted = false; cpu.Jammed = false;
        cpu.Push(Trap);
        cpu.PC = addr;
        long end = cpu.Cycles + maxCycles;
        while (cpu.Cycles < end && cpu.PC != Trap && !cpu.Halted && !cpu.Jammed) cpu.Step();
    }

    private static int U16(byte[] d, int o) { return d[o] | (d[o + 1] << 8); }
}
