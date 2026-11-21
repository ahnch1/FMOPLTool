using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.Player;

/// <summary>곡 종류 (재생기가 다르다).</summary>
public enum SongKind { None, Adlib, Sop, Tracker, Ext, Midi, Chip }

/// <summary>재생 상태.</summary>
public enum PlayState { Empty, Stopped, Playing, Paused }

/// <summary>재생에 쓰는 파일 위치 (앱이 정한다).</summary>
public sealed class PlayerSettings
{
    /// <summary>ROL · IMS 곡 옆에 같은 이름 BNK가 없을 때 쓰는 뱅크 (STANDARD.BNK).</summary>
    public string DefaultBankPath { get; set; }
    /// <summary>MIDI FM용 GM 뱅크 (GENMIDI.OP2 또는 BNK). 비우면 <see cref="ResourceFolder"/>의 GENMIDI.OP2, 없으면 기본 뱅크.</summary>
    public string MidiBankPath { get; set; }
    /// <summary>프로그램 리소스 폴더 (GENMIDI.OP2 · systems.json).</summary>
    public string ResourceFolder { get; set; }
    /// <summary>MT-32 ROM 폴더 (컨트롤 + PCM ROM, 파일 이름은 상관없음 — SHA-1로 알아본다). 없거나 에뮬레이터 라이브러리가 없으면 MT-32 → GM.</summary>
    public string Mt32RomFolder { get; set; }
}

/// <summary>
/// 플랫폼 없는 Jukebox 한 판: 곡 파일 하나를 열어(ROL · IMS · MUS · SOP · STM · S3M · MOD · XM · IT · MIDI · VGM 계열 · hoot 항목)
/// <see cref="AudioEngine"/>으로 재생 · 일시정지 · 정지 · 이동하고 위치 · 길이를 초로 알려 준다.
/// Windows Jukebox(JukeboxView)의 곡 열기 · 재생 제어를 화면 없이 옮긴 것이다 (가사 · 믹서 · 8-bit 신디 · MT-32 에뮬레이터는 뺐다).
/// <para>화면은 <see cref="Update"/>를 자주(30ms쯤) 불러 곡이 끝났는지 본다. 장치는 앱이 <see cref="AudioEngine.OpenDevice"/>로 연다.</para>
/// </summary>
public sealed partial class JukeboxSession
{
    private readonly AudioEngine engine;
    private readonly PlayerSettings settings;
    private readonly FormatRegistry formats = FormatRegistry.CreateDefault();

    private Song song;
    private SopSong sopSong;
    private TrackerModule trackerSong;
    private ExtModule extSong;
    private MidiSong midiSong;
    private VgmSong vgmSong;
    private TempoMap tempoMap;
    private bool midiForceMt32;
    private InstrumentBank bank;
    private InstrumentBank defaultBank;
    private string defaultBankPath;
    private GmBank gmBank;
    private string gmBankKey;
    private double pausedTick;

    public JukeboxSession(AudioEngine engine, PlayerSettings settings)
    {
        this.engine = engine ?? throw new ArgumentNullException(nameof(engine));
        this.settings = settings ?? new PlayerSettings();
    }

    public AudioEngine Engine { get { return engine; } }
    /// <summary>재생 조절 (속도 · 키 · 볼륨 · 채널 끄기). 다음 재생부터 들어간다 (속도 · 볼륨은 <see cref="SetSpeed"/> · <see cref="SetGain"/>로 바로).</summary>
    public PlaybackOptions Options { get; } = new PlaybackOptions();

    public PlayState State { get; private set; } = PlayState.Empty;
    public SongKind Kind { get; private set; }
    public string Path { get; private set; }
    public string Title { get; private set; } = "";
    /// <summary>형식 · 칩 · 악기 요약 한 줄.</summary>
    public string Info { get; private set; } = "";
    /// <summary>게임 · 기종 · 작곡자 등 (VGM 계열). 없으면 빈 문자열.</summary>
    public string Detail { get; private set; } = "";
    public double LengthSeconds { get; private set; }

    public bool HasSong { get { return Kind != SongKind.None; } }

    // ---------------------------------------------------------------- 곡 열기

    /// <summary>곡을 연다 (재생 중이던 곡은 멈춘다). 실패하면 false와 까닭.</summary>
    public bool Load(string path, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(path)) { error = "경로가 없습니다."; return false; }
        string file = ChipSongs.FilePart(path);
        try
        {
            if (SopFormat.IsSop(file)) return Set(path, LoadSop(path));
            if (ExtModule.IsExtModule(file)) return Set(path, LoadExt(path));
            if (TrackerFiles.IsTracker(file)) return Set(path, LoadTracker(path));
            if (MidiFiles.IsMidi(file) || HootSongs.IsMidi(path)) return Set(path, LoadMidi(path));
            if (ChipSongs.IsChipSong(path)) { var commit = LoadChip(ref path); return Set(path, commit); }
            return Set(path, LoadAdlib(path));
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private bool Set(string path, Action commit)
    {
        // 읽기가 모두 끝난 뒤에 바꾼다 (실패하면 지금 곡을 그대로 둔다)
        StopEngine();
        song = null; sopSong = null; trackerSong = null; extSong = null; midiSong = null; vgmSong = null; tempoMap = null; bank = null;
        Detail = "";
        commit();
        Path = path;
        pausedTick = 0;
        for (int v = 0; v < PlaybackOptions.Voices; v++) { Options.VoiceMute[v] = false; Options.VoiceTranspose[v] = 0; }
        BuildChannels();
        Lyrics = Kind == SongKind.Adlib || Kind == SongKind.Sop ? FindLyrics(path) : null;
        State = PlayState.Stopped;
        return true;
    }

    private Action LoadAdlib(string path)
    {
        var fmt = formats.FindSong(path);
        if (fmt == null || !fmt.CanRead) throw new InvalidDataException("읽을 수 없는 곡 형식입니다.");
        Song s;
        using (var fs = ZipPath.OpenRead(path)) s = fmt.Read(fs);
        s.FilePath = path;
        string title = System.Text.RegularExpressions.Regex.Replace(s.Title ?? "", @"\s{2,}", " ").Trim();
        if (title.Length == 0 || title == "Untitled") title = System.IO.Path.GetFileNameWithoutExtension(path);
        var b = FindBank(path, out bool sameName);
        var tm = new TempoMap(s);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int v = 0; v < s.VoiceCount; v++)
            foreach (var t in s.Voices[v].Timbres)
                if (!string.IsNullOrWhiteSpace(t.InstrumentName)) used.Add(t.InstrumentName.Trim());
        int loaded = used.Count(n => b != null && b.Find(n) != null);
        string bankName = b != null && b.FilePath != null ? System.IO.Path.GetFileName(b.FilePath) : "(뱅크 없음)";
        return () =>
        {
            song = s; tempoMap = tm; bank = b;
            Kind = SongKind.Adlib;
            Title = title;
            LengthSeconds = tm.SecondsAt(s.LengthTicks);
            Info = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant() + " · OPL2 · 악기 " + loaded + "/" + used.Count + " · " + bankName + (sameName ? " (같은 이름)" : "");
        };
    }

    private Action LoadSop(string path)
    {
        var s = SopFormat.Load(path);
        string title = System.Text.RegularExpressions.Regex.Replace(s.Title ?? "", @"\s{2,}", " ").Trim();
        if (title.Length == 0) title = System.IO.Path.GetFileNameWithoutExtension(path);
        return () =>
        {
            sopSong = s;
            Kind = SongKind.Sop;
            Title = title;
            LengthSeconds = s.SecondsAt(s.LengthTicks);
            Info = "SOP · OPL3 " + s.TrackCount + "트랙" + (s.Percussive ? " · 리듬" : "");
        };
    }

    private Action LoadExt(string path)
    {
        var m = ExtModule.Load(path);
        double seconds = ExtModulePlayer.MeasureSeconds(m);
        string title = (m.Title ?? "").Trim();
        if (title.Length == 0) title = System.IO.Path.GetFileNameWithoutExtension(path);
        return () =>
        {
            extSong = m;
            Kind = SongKind.Ext;
            Title = title;
            LengthSeconds = seconds;
            Info = (m.IsIt ? "IT · Impulse Tracker" : "XM · FastTracker 2") + " · " + m.Channels + "채널";
        };
    }

    private Action LoadTracker(string path)
    {
        var m = TrackerFiles.Load(path);
        double seconds = TrackerPlayer.MeasureSeconds(m);
        string title = (m.Title ?? "").Trim();
        if (title.Length == 0) title = System.IO.Path.GetFileNameWithoutExtension(path);
        string kind = m.Kind == TrackerKind.Stm ? "STM · Scream Tracker 2" : TrackerFiles.IsMod(path) ? "MOD · ProTracker" : "S3M · Scream Tracker 3";
        return () =>
        {
            trackerSong = m;
            Kind = SongKind.Tracker;
            Title = title;
            LengthSeconds = seconds;
            Info = kind + " · " + m.UsedChannelCount + "채널";
        };
    }

    private Action LoadMidi(string path)
    {
        var m = HootSongs.IsMidi(path) ? HootSongs.LoadMidi(path) : MidiFiles.Load(path);
        var gm = LoadGmBank();
        bool forceMt32 = m.Extend != null && m.Standard == MidiStandard.Mt32;
        return () =>
        {
            midiSong = m;
            gmBank = gm;
            midiForceMt32 = forceMt32;
            Kind = SongKind.Midi;
            Title = string.IsNullOrWhiteSpace(m.Title) ? System.IO.Path.GetFileNameWithoutExtension(ChipSongs.FilePart(path)) : m.Title;
            LengthSeconds = m.LengthSeconds;
            Info = m.FormatName + (m.Standard != MidiStandard.None ? " · " + MidiSysEx.Name(m.Standard) : "") + " · " + m.ChannelUsed.Count(u => u) + "채널 · GM FM " + (gm != null ? gm.Source : "(뱅크 없음)")
                + (forceMt32 ? " (MT-32 → GM)" : "");
        };
    }

    private Action LoadChip(ref string path)
    {
        // 곡이 여럿 든 파일을 그냥 열면 첫 곡 ("경로::0")
        if (path.IndexOf("::", 2, StringComparison.Ordinal) < 0)
        {
            var tracks = ChipSongs.Tracks(path);
            if (tracks.Count > 1) path = tracks[0].path;
        }
        var v = ChipSongs.Load(path);
        if (!v.IsPlayable)
            throw new InvalidDataException("소리 낼 수 있는 칩이 없습니다" + (v.UnsupportedChips.Count > 0 ? " (" + string.Join(", ", v.UnsupportedChips) + ")" : "") + ".");
        double seconds = VgmPlayer.PlaySeconds(v);
        var info = new List<string>();
        foreach (var t in new[] { v.Game, v.SystemName, v.Author, v.ReleaseDate }) if (!string.IsNullOrEmpty(t)) info.Add(t);
        string fmtName = v.SourceFormat == "VGM" ? "VGM " + v.VersionText : v.SourceFormat;
        string p = path;
        return () =>
        {
            vgmSong = v;
            Kind = SongKind.Chip;
            Title = string.IsNullOrWhiteSpace(v.Title) ? System.IO.Path.GetFileNameWithoutExtension(ChipSongs.FilePart(p)) : v.Title;
            LengthSeconds = seconds;
            Info = fmtName + " · " + v.ChipSummary + (v.UnsupportedChips.Count > 0 ? " (소리 없음: " + string.Join(", ", v.UnsupportedChips) + ")" : "");
            Detail = string.Join(" · ", info);
        };
    }

    // ---------------------------------------------------------------- 뱅크

    /// <summary>같은 이름 BNK (MUS는 SND) → 기본 뱅크 → 빈 뱅크.</summary>
    private InstrumentBank FindBank(string songPath, out bool sameName)
    {
        sameName = false;
        if (string.Equals(System.IO.Path.GetExtension(songPath), ".mus", StringComparison.OrdinalIgnoreCase))
        {
            string snd = SideFile(songPath, ".snd");
            if (snd != null) { var sb = ReadBank(snd); if (sb != null) { sameName = true; return sb; } }
            try
            {
                foreach (var f in ZipPath.GetFiles(System.IO.Path.GetDirectoryName(songPath), ".snd").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                {
                    var sb = ReadBank(f);
                    if (sb != null) return sb;
                }
            }
            catch (Exception) { }
        }
        string side = SideFile(songPath, ".bnk");
        if (side != null)
        {
            var b = ReadBank(side);
            if (b != null) { sameName = true; return b; }
        }
        string def = settings.DefaultBankPath;
        if (def != null && File.Exists(def))
        {
            if (defaultBank == null || !string.Equals(defaultBankPath, def, StringComparison.OrdinalIgnoreCase))
            {
                defaultBank = ReadBank(def);
                defaultBankPath = def;
            }
            if (defaultBank != null) return defaultBank;
        }
        return InstrumentBank.CreateNew();
    }

    private InstrumentBank ReadBank(string path)
    {
        try
        {
            var fmt = formats.FindBank(path);
            if (fmt == null) return null;
            InstrumentBank b;
            using (var fs = ZipPath.OpenRead(path)) b = fmt.Read(fs);
            b.FilePath = path;
            return b;
        }
        catch (Exception) { return null; }
    }

    /// <summary>곡과 같은 폴더의 같은 이름 다른 확장자 파일 (대소문자 무시: 안드로이드 파일 시스템은 대소문자를 가린다).</summary>
    internal static string SideFile(string songPath, string ext)
    {
        try
        {
            string dir = System.IO.Path.GetDirectoryName(songPath), stem = System.IO.Path.GetFileNameWithoutExtension(songPath);
            foreach (var f in ZipPath.GetFiles(dir))
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(System.IO.Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase))
                    return f;
        }
        catch (Exception) { }
        return null;
    }

    /// <summary>MIDI FM용 GM 뱅크: 설정의 MIDI 뱅크 → 리소스 폴더의 GENMIDI.OP2 → 기본 뱅크 이름 맞춤.</summary>
    private GmBank LoadGmBank()
    {
        string path = settings.MidiBankPath;
        if (path == null || !File.Exists(path))
        {
            path = null;
            try
            {
                string dir = settings.ResourceFolder;
                if (dir != null && Directory.Exists(dir))
                    path = Directory.GetFiles(dir).FirstOrDefault(f => string.Equals(System.IO.Path.GetFileName(f), "GENMIDI.OP2", StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception) { }
        }
        string key = path ?? ("BNK:" + settings.DefaultBankPath);
        if (gmBank != null && key == gmBankKey) return gmBank;
        GmBank b = null;
        try
        {
            if (path != null && path.EndsWith(".op2", StringComparison.OrdinalIgnoreCase)) b = GmBank.LoadOp2(path);
            else if (path != null) b = GmBank.FromBank(ReadBank(path));
        }
        catch (Exception) { }
        if (b == null)
        {
            string def = settings.DefaultBankPath;
            var bk = def != null && File.Exists(def) ? ReadBank(def) : null;
            b = GmBank.FromBank(bk ?? InstrumentBank.CreateNew());
        }
        gmBankKey = key;
        return b;
    }

    // ---------------------------------------------------------------- 시간

    private double LengthTicks
    {
        get
        {
            switch (Kind)
            {
                case SongKind.Midi: return midiSong.LengthTicks;
                case SongKind.Tracker: case SongKind.Ext: case SongKind.Chip: return LengthSeconds * 1000;
                case SongKind.Sop: return sopSong.LengthTicks;
                case SongKind.Adlib: return song.LengthTicks;
                default: return 0;
            }
        }
    }

    private double SecondsAt(double tick)
    {
        switch (Kind)
        {
            case SongKind.Tracker: case SongKind.Ext: case SongKind.Chip: return tick / 1000.0;
            case SongKind.Sop: return sopSong.SecondsAt(tick);
            case SongKind.Midi: return midiSong.TickToSeconds(tick);
            case SongKind.Adlib: return tempoMap.SecondsAt((int)tick);
            default: return 0;
        }
    }

    private double TickAt(double seconds)
    {
        switch (Kind)
        {
            case SongKind.Tracker: case SongKind.Ext: case SongKind.Chip: return seconds * 1000.0;
            case SongKind.Sop: return sopSong.TickAt(seconds);
            case SongKind.Midi: return midiSong.SecondsToTick(seconds);
            case SongKind.Adlib: return tempoMap.TickAt(seconds);
            default: return 0;
        }
    }

    private double CurrentTick { get { return State == PlayState.Playing ? engine.PositionTick : pausedTick; } }

    /// <summary>지금 위치 (곡 시간, 원래 속도 기준 초).</summary>
    public double PositionSeconds { get { return HasSong ? SecondsAt(Math.Min(CurrentTick, LengthTicks)) : 0; } }

    // ---------------------------------------------------------------- 재생 제어

    private void StartAt(double tick)
    {
        if (!HasSong) return;
        tick = Math.Max(0, tick);
        switch (Kind)
        {
            case SongKind.Chip: engine.PlayVgm(vgmSong, tick, Options); break;
            case SongKind.Tracker: engine.PlayTracker(trackerSong, 0, 0, -1, false, Options, tick); break;
            case SongKind.Ext: engine.PlayExtModule(extSong, Options, tick); break;
            case SongKind.Sop: engine.PlaySop(sopSong, (int)tick, Options); break;
            case SongKind.Midi:
                engine.MidiBank = gmBank;
                engine.MidiSynth = ChipSynth ? MidiSynth.Chip : MidiSynth.Fm;
                engine.MidiChipSound = ChipStyle;
                // MT-32 에뮬레이터 (ROM 폴더 · 라이브러리가 있으면). 없으면 엔진이 음색 번호를 GM에 맞춘다
                bool mt32 = (Mt32Module || midiForceMt32) && !ChipSynth;
                if (mt32) EnsureMt32();
                engine.MidiModule = mt32 ? MidiModule.Mt32 : MidiModule.Gm;
                engine.PlayMidi(midiSong, tick, Options);
                break;
            default:
                if (ChipSynth)
                {
                    // ROL · IMS → 8-bit: 같은 틱의 MIDI 곡으로 바꿔 칩 신디로 (위치 · 가사 · 채널은 그대로)
                    engine.MidiSynth = MidiSynth.Chip;
                    engine.MidiChipSound = ChipStyle;
                    engine.MidiModule = MidiModule.Gm;
                    engine.PlayMidi(ChipSongOf(song, bank), tick, Options);
                    songChip = true;
                }
                else { engine.Play(song, bank, (int)tick, -1, -1, -1, Options); songChip = false; }
                break;
        }
        State = PlayState.Playing;
    }

    private void StopEngine()
    {
        if (State == PlayState.Playing) engine.Stop();
    }

    public void Play()
    {
        if (State == PlayState.Stopped || State == PlayState.Paused) StartAt(pausedTick);
    }

    public void Pause()
    {
        if (State != PlayState.Playing) return;
        pausedTick = engine.PositionTick;
        engine.Stop();
        State = PlayState.Paused;
    }

    /// <summary>재생 ↔ 일시정지.</summary>
    public void Toggle()
    {
        if (State == PlayState.Playing) Pause(); else Play();
    }

    public void Stop()
    {
        if (!HasSong) return;
        StopEngine();
        State = PlayState.Stopped;
        pausedTick = 0;
    }

    /// <summary>곡 시간(초)으로 이동. 재생 중이면 그 자리부터 다시 재생.</summary>
    public void Seek(double seconds)
    {
        if (!HasSong) return;
        double tick = Math.Max(0, Math.Min(LengthTicks, TickAt(Math.Max(0, seconds))));
        if (State == PlayState.Playing) StartAt(tick);
        else pausedTick = tick;
    }

    public void SetSpeed(double speed)
    {
        Options.Speed = Math.Max(0.5, Math.Min(2.0, speed));
        if (State == PlayState.Playing) engine.SetSpeed(Options.Speed);
    }

    public void SetGain(double gain)
    {
        Options.Gain = Math.Max(0, Math.Min(1.5, gain));
        if (State == PlayState.Playing) engine.SetGain(Options.Gain);
    }

    /// <summary>
    /// 자주 부른다: 재생 중이던 곡이 끝났으면 정지 상태로 바꾸고 true (다음 곡 · 반복은 부른 쪽이 정한다).
    /// </summary>
    public bool Update()
    {
        if (State != PlayState.Playing || engine.IsPlaying) return false;
        State = PlayState.Stopped;
        pausedTick = 0;
        return true;
    }
}
