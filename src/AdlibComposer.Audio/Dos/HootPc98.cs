using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio.Chips;
using AdlibComposer.Formats;

namespace AdlibComposer.Audio.Dos;

/// <summary>hoot DOS 기계 (PC-98 · PC/AT) 공통: 칩 쓰기 기록 · 클럭 · hoot 재생 요청.</summary>
public interface IHootMachine
{
    /// <summary>칩 쓰기: 시간 (CPU 클럭), 종류 (0 · 1 = 음원 포트 0 · 1, 2 = BEEP 켜기, 3 = BEEP 카운터), 레지스터, 값.</summary>
    List<(long t, int kind, int reg, int val)> ChipLog { get; }
    double ClockHz { get; }
    /// <summary>hoot 인터럽트 벡터 (funcvect).</summary>
    int HootVector { get; set; }
    /// <summary>재생 중 셸이 끝나도 쉬는 고리에서 계속.</summary>
    bool KeepRunning { get; set; }
    int HootCount { get; }
    void Play(int code);
}

/// <summary>
/// hoot DOS 항목 — pc98dos (PC-98) · pcatdos (PC/AT) (<see cref="HootArchive.Extension"/> 파일 = 항목 XML 하나, 같은 폴더 = zip을 푼 내용) → 곡.
/// <para>곡 목록 = titlelist의 &lt;title code&gt; (range = 효과음은 뺌), "파일::n" = n번째 제목.
/// 재생: <see cref="HootDosChip"/>가 기계를 스스로 돌린다 (VGM에는 길이만). 길이를 알 수 없어 <see cref="DefaultSeconds"/> + 페이드.</para>
/// <para>항목 → 기계 (<see cref="BootAny"/>): pc98dos opn = 26K (YM2203) · opna · 86 = 86 보드 (YM2608) · beep = 보드 없음 (clockmul 기본 8 × 2.4576MHz),
/// pcatdos adlib = OPL2 · soundblaster16 = OPL3 · beep = 스피커만 (clockmul 기본 4 × 1.193MHz). funcvect (기본 7Fh),
/// rom 줄을 차례로: file (offset ≥ 0 = 핸들에 묶기) · conin (글자를 핸들에) · device (config.sys device=) · code · binary (파일을 주소에) · string (글자를 주소에) · shell (명령 줄).</para>
/// </summary>
public static class HootPc98
{
    public static readonly string[] Extensions = { HootArchive.Extension };
    public const double DefaultSeconds = 180, DefaultFade = 8;
    /// <summary>셸 한 줄에 줄 CPU 클럭 (끝나거나 상주하지 않으면 그 자리에서 다음으로 = 계속 도는 재생기).</summary>
    public const long ShellCycles = 40_000_000;
    /// <summary>끝나지 않은 셸 뒤에 셸이 더 있으면 (드라이버 초기화가 오래 걸림) 이만큼 더 기다린다.</summary>
    public const long MoreShellCycles = 400_000_000;
    /// <summary>진단: 세우는 동안부터 포트 · 인터럽트 · BIOS / DOS 호출을 센다 (<see cref="Pc98Machine.Trace"/>, PC-98만).</summary>
    public static bool TraceBoot;

    public static List<(int Code, string Title)> Tracks(string path)
    {
        var e = HootArchive.Load(path);
        return e.Titles.ToList();
    }

    public static bool IsPcAt(HootArchive.Entry e) { return e.Driver.Equals("pcatdos", StringComparison.OrdinalIgnoreCase); }

    public static Pc98Machine.Board BoardOf(HootArchive.Entry e)
    {
        switch (e.Type.ToLowerInvariant())
        {
            case "opn": return Pc98Machine.Board.Opn;
            case "opna": case "86": return Pc98Machine.Board.Opna;
            default: return Pc98Machine.Board.None;
        }
    }

    public static PcAtMachine.Card CardOf(HootArchive.Entry e)
    {
        switch (e.Type.ToLowerInvariant())
        {
            case "adlib": return PcAtMachine.Card.AdLib;
            case "soundblaster16": return PcAtMachine.Card.SoundBlaster16;
            default: return PcAtMachine.Card.Beep;
        }
    }

    /// <summary>항목의 음원 칩 (기계 쓰기 종류 0 · 1을 받는 것) — 없으면 null (BEEP만).</summary>
    public static ISoundChip MainChipOf(HootArchive.Entry e)
    {
        if (IsPcAt(e))
        {
            var c = CardOf(e);
            return c == PcAtMachine.Card.Beep ? null : new OplChip(c == PcAtMachine.Card.SoundBlaster16);
        }
        return HootDosChip.OpnFor(BoardOf(e));
    }

    private static string ChipLabel(HootArchive.Entry e)
    {
        if (IsPcAt(e)) { var c = CardOf(e); return "PC/AT (" + (c == PcAtMachine.Card.AdLib ? "OPL2" : c == PcAtMachine.Card.SoundBlaster16 ? "OPL3" : "BEEP") + ")"; }
        var b = BoardOf(e);
        return "PC-98 (" + (b == Pc98Machine.Board.Opn ? "YM2203" : b == Pc98Machine.Board.Opna ? "YM2608" : "BEEP") + ")";
    }

    /// <summary>"항목.hoot::n" → 곡 (길이만 든 VGM + 스스로 도는 기계 칩).</summary>
    public static VgmSong Load(string path)
    {
        string file = ChipSongs.FilePart(path);
        NezM3u.SplitTrack(path, out int n);
        var e = HootArchive.Load(file);
        if (!HootArchive.Playable(e.Driver, e.Type)) throw new InvalidDataException("흉내 낼 수 없는 hoot 항목입니다: " + e.Driver + " / " + e.Type);
        if (e.Titles.Count == 0) throw new InvalidDataException("hoot 항목에 곡 목록(titlelist)이 없습니다.");
        n = Math.Max(0, Math.Min(n, e.Titles.Count - 1));
        var (code, title) = e.Titles[n];
        string dir = Path.GetDirectoryName(Path.GetFullPath(file));
        bool at = IsPcAt(e);
        var w = new VgmWriter { Title = title, Game = e.Name, System = at ? "IBM PC/AT" : "PC-9801" };
        w.Wait((long)Math.Round((DefaultSeconds + DefaultFade) * VgmSong.Rate));
        var v = VgmFormat.Read(w.ToArray());
        v.FilePath = path;
        v.FadeSeconds = DefaultFade;
        v.SelfRunningName = ChipLabel(e);
        // 기계 세우기 (드라이버 상주 · 초기화, 0.2~1초)는 미리 뒤에서: 처음 소리 내는 칩이 이것을 받는다 (오디오 스레드에서 기다리지 않게).
        // 그 뒤의 칩 (위치 옮기기 등)은 새로 세운다
        var pre = System.Threading.Tasks.Task.Run(() => BootDirAny(dir, e, out _));
        int taken = 0;
        double beepClock = at ? Beeper.PcAtClock : Beeper.Pc98Clock;
        v.SelfRunning = () => new HootDosChip(() => System.Threading.Interlocked.Exchange(ref taken, 1) == 0 ? pre.GetAwaiter().GetResult() : BootDirAny(dir, e, out _),
            MainChipOf(e), beepClock, code, at ? "PC/AT DOS" : "PC-98 DOS");
        v.SourceFormat = "hoot " + e.Driver + " (" + e.Type + ", 곡 코드 0x" + code.ToString("X") + ", 길이 " + DefaultSeconds + "초 고정)";
        return v;
    }

    /// <summary>항목대로 PC-98 기계를 세우고 셸을 돌린다 (재생 요청 전까지).</summary>
    public static Pc98Machine Boot(DosFileSystem fs, HootArchive.Entry e) { return Boot(fs, e, out _); }

    public static Pc98Machine Boot(DosFileSystem fs, HootArchive.Entry e, out List<DosMachine.ShellResult> shells, string library = null, string ownZip = null)
    {
        return (Pc98Machine)BootAny(fs, e, out shells, library, ownZip);
    }

    /// <summary>zip을 푼 폴더의 PC-98 항목으로 세우기 (<see cref="BootDirAny"/>).</summary>
    public static Pc98Machine BootDir(string dir, HootArchive.Entry e, out List<DosMachine.ShellResult> shells) { return (Pc98Machine)BootDirAny(dir, e, out shells); }

    /// <summary>zip을 푼 폴더의 항목으로 세우기: 파일 = 원래 zip (Shift_JIS 이름), 없는 셸 프로그램 = 같은 폴더의 다른 zip (<see cref="SharedPrograms"/>).</summary>
    public static DosMachine BootDirAny(string dir, HootArchive.Entry e, out List<DosMachine.ShellResult> shells)
    {
        string zip = ZipFolder.SourceZip(dir);
        return BootAny(DosFileSystem.ForHootDir(dir), e, out shells, zip != null ? Path.GetDirectoryName(zip) : null, zip);
    }

    /// <summary>항목의 기계 (PC-98 또는 PC/AT)를 만들고 rom 줄대로 세운다. 돌려주는 기계는 <see cref="IHootMachine"/>.</summary>
    public static DosMachine BootAny(DosFileSystem fs, HootArchive.Entry e, out List<DosMachine.ShellResult> shells, string library = null, string ownZip = null)
    {
        DosMachine m;
        if (IsPcAt(e)) m = new PcAtMachine(fs, CardOf(e), Math.Max(1, e.Option("clockmul", 4)));
        else m = new Pc98Machine(fs, BoardOf(e), Math.Max(1, e.Option("clockmul", 8))) { Trace = TraceBoot };
        var hm = (IHootMachine)m;
        hm.HootVector = e.Option("funcvect", 0x7F) & 0xFF;
        shells = new List<DosMachine.ShellResult>();
        var sjis = KoreanText.ShiftJis;
        foreach (var (type, off, text) in e.Roms)
        {
            switch (type)
            {
                case "file":
                    if (off >= 0) m.BindFile((int)off, text);
                    break;
                case "conin":
                    m.BindText((int)Math.Max(0, off), sjis.GetBytes(text));
                    break;
                case "device":
                    m.LoadDevice(text);
                    if (m.HostReturned) m.Idle();
                    break;
                case "code": case "binary":
                    if (off >= 0 && fs.TryGet(text, out var d)) Array.Copy(d, 0, m.Mem, (int)off, (int)Math.Min(d.Length, m.Mem.Length - off));
                    break;
                case "string":
                    if (off >= 0) { var b = sjis.GetBytes(text); Array.Copy(b, 0, m.Mem, (int)off, (int)Math.Min(b.Length, m.Mem.Length - off)); }
                    break;
                case "shell":
                    {
                        // 이 zip에 없는 프로그램 (여러 게임이 같이 쓰는 드라이버): 같은 폴더의 다른 zip에서
                        string prog = text.Trim().Split(new[] { ' ', '\t', '/' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                        if (library != null && prog.Length > 0 && m.ResolveProgram(prog) == null)
                        {
                            var shared = SharedPrograms.Find(library, prog, ownZip);
                            if (shared != null) fs.Add(shared.Value.name, shared.Value.data);
                        }
                        var r = m.Shell(text, ShellCycles);
                        // 뒤에 셸이 더 있는데 아직 도는 중: 상주 · 끝날 때까지 조금 더
                        if (r == DosMachine.ShellResult.Running && e.Roms.Skip(e.Roms.IndexOf((type, off, text)) + 1).Any(x => x.Type == "shell"))
                        {
                            for (long spent = 0; spent < MoreShellCycles && !m.HostReturned; spent += 20_000_000) m.Run(20_000_000);
                            if (m.HostReturned) r = (m.ReturnCode >> 8) == 3 ? DosMachine.ShellResult.Resident : DosMachine.ShellResult.Exited;
                        }
                        shells.Add(r);
                        if (m.HostReturned || r == DosMachine.ShellResult.NotFound || r == DosMachine.ShellResult.NoMemory) m.Idle();
                        break;
                    }
            }
        }
        if (m.HostReturned) m.Idle();
        hm.KeepRunning = true;
        return m;
    }

    /// <summary>PC-98 항목 · 곡 하나를 seconds초 돌려 칩 쓰기 VGM으로 (내보내기 · 시험). 처음 칩 설정(재생 전 쓰기)은 앞에 기다림 없이.</summary>
    public static byte[] ToVgm(string hootPath, int index, double seconds, out Pc98Machine machine)
    {
        var e = HootArchive.Load(hootPath);
        var (code, title) = e.Titles[Math.Max(0, Math.Min(index, e.Titles.Count - 1))];
        machine = BootDir(Path.GetDirectoryName(Path.GetFullPath(hootPath)), e, out _);
        long t0 = machine.Cpu.Cycles;
        machine.Play(code);
        machine.Run((long)(machine.CpuHz * seconds));
        return machine.ToVgm(t0, seconds, title);
    }
}

/// <summary>
/// DOS 기계 (PC-98 · PC/AT)를 품은 칩: 샘플마다 기계를 그만큼 돌리고 나온 칩 쓰기를 음원 칩(OPN · OPNA · OPL2 · OPL3) · BEEP에 넣는다.
/// 기계는 처음 소리를 낼 때 세운다 (곡 목록 · 믹서 채널만 볼 때는 세우지 않음).
/// </summary>
public class HootDosChip : ISoundChip, ISelfRunningChip
{
    private readonly Func<DosMachine> boot;
    private readonly int code;
    private readonly ISoundChip main;
    private readonly Beeper beep;
    private readonly string label;
    private DosMachine m;
    private IHootMachine hm;
    private double cyclesPerSample, sampleCycle;
    private int logPos, used, sinceScan;
    private bool failed;
    private readonly bool[] muted;

    public static OpnChip OpnFor(Pc98Machine.Board board)
    {
        if (board == Pc98Machine.Board.Opn) return new OpnChip(OpnChip.Variant.Ym2203, Pc98Machine.OpnBaseClock);
        if (board == Pc98Machine.Board.Opna) return new OpnChip(OpnChip.Variant.Ym2608, Pc98Machine.OpnBaseClock * 2);
        return null;
    }

    public HootDosChip(Func<DosMachine> boot, ISoundChip main, double beepClock, int code, string label)
    {
        this.boot = boot;
        this.code = code;
        this.main = main;
        this.label = label;
        NativeRate = main != null ? main.NativeRate : 44100;
        beep = new Beeper(beepClock, NativeRate);
        muted = new bool[ChannelCount];
    }

    /// <summary>세운 기계 (아직이면 null) — 시험 · 진단용.</summary>
    public DosMachine Machine { get { return m; } }
    public string Error { get; private set; } = "";

    public string Name { get { return label + " (" + (main != null ? main.Name + " + " : "") + "BEEP)"; } }
    public int NativeRate { get; }
    private int FmCount { get { return main != null ? main.ChannelCount : 0; } }
    public int ChannelCount { get { return FmCount + 1; } }
    public string ChannelLabel(int ch) { return ch < FmCount ? main.ChannelLabel(ch) : "BEEP"; }

    public void Reset() { }
    public void Write(int port, int reg, int value) { }

    private bool Ensure()
    {
        if (m != null) return true;
        if (failed) return false;
        try
        {
            m = boot();
            hm = (IHootMachine)m;
            cyclesPerSample = hm.ClockHz / NativeRate;
            // 재생 전 쓰기 (드라이버 초기화)는 칩에 바로
            foreach (var e in hm.ChipLog) Apply(e);
            hm.ChipLog.Clear(); logPos = 0;
            sampleCycle = m.Cpu.Cycles;
            hm.Play(code);
            return true;
        }
        catch (Exception ex) { failed = true; Error = ex.Message; return false; }
    }

    private void Apply((long t, int kind, int reg, int val) e)
    {
        switch (e.kind)
        {
            case 0: case 1: main?.Write(e.kind, e.reg, e.val); break;
            case 2: beep.Write(0, 2, e.val); if (e.val != 0) used |= 1 << FmCount; break;
            case 3: beep.Write(0, 0, e.val & 0xFF); beep.Write(0, 1, (e.val >> 8) & 0xFF); break;
        }
    }

    /// <summary>한 샘플만큼 기계를 돌리고 그때까지의 쓰기를 칩에.</summary>
    private void Advance()
    {
        sampleCycle += cyclesPerSample;
        long now = (long)sampleCycle;
        while (m.Cpu.Cycles < now)
        {
            long before = m.Cpu.Cycles;
            m.Run(Math.Max(1, (long)(cyclesPerSample * 64)));
            if (m.Cpu.Cycles == before) { m.Cpu.Cycles = now; break; }   // 멈춘 기계
        }
        var log = hm.ChipLog;
        while (logPos < log.Count && log[logPos].t <= now) Apply(log[logPos++]);
        if (logPos > 8192) { log.RemoveRange(0, logPos); logPos = 0; }
        if (++sinceScan >= 256 && main != null)
        {
            sinceScan = 0;
            for (int i = 0; i < FmCount; i++) if (main.ChannelLevel(i) > 0.01f || main.ChannelActive(i)) used |= 1 << i;
        }
    }

    public void Generate(out int left, out int right)
    {
        if (!Ensure()) { left = right = 0; return; }
        Advance();
        int l = 0, r = 0;
        if (main != null) main.Generate(out l, out r);
        if (!muted[ChannelCount - 1]) { beep.Generate(out int bl, out int br); l += bl / 2; r += br / 2; }
        left = l; right = r;
    }

    public void Skip(double seconds)
    {
        if (!Ensure()) return;
        long n = (long)(seconds * NativeRate);
        for (long i = 0; i < n; i++) Advance();
    }

    public int VoicesUsed { get { return used; } }

    public void SetMute(int ch, bool mute)
    {
        if (ch < 0 || ch >= ChannelCount) return;
        muted[ch] = mute;
        if (ch < FmCount) main.SetMute(ch, mute);
    }

    public void SetPitch(int ch, double ratio) { if (ch < FmCount) main.SetPitch(ch, ratio); else beep.SetPitch(0, ratio); }
    public double ChannelFrequency(int ch) { return ch < FmCount ? main.ChannelFrequency(ch) : beep.ChannelFrequency(0); }
    public float ChannelLevel(int ch) { return ch < FmCount ? main.ChannelLevel(ch) : beep.ChannelLevel(0); }
    public bool ChannelActive(int ch) { return ch < FmCount ? main.ChannelActive(ch) : beep.ChannelActive(0); }
}

/// <summary>PC-98 기계 칩 (<see cref="HootDosChip"/> + OPN / OPNA).</summary>
public sealed class Pc98Chip : HootDosChip
{
    public Pc98Chip(Func<Pc98Machine> boot, Pc98Machine.Board board, int code) : base(() => boot(), OpnFor(board), Beeper.Pc98Clock, code, "PC-98 DOS") { }
}
