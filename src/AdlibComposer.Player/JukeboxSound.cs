using System;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.Player;

/// <summary>
/// Jukebox 소리 고르기 · 가사 (Windows JukeboxView의 위 막대 · 가사 상자를 화면 없이):
/// 거친 음색 · 그 시절 스피커, '원래 소리' / 8-bit 칩 소리 (MIDI · ROL · IMS), MIDI 신디 (OPL FM · 8-bit) · 모듈 (GM · MT-32),
/// 같은 이름 ISS 가사 (ROL · IMS · SOP).
/// </summary>
public sealed partial class JukeboxSession
{
    /// <summary>8-bit 칩 소리 풍 (Windows와 같은 순서).</summary>
    public static readonly ChipSound[] ChipStyles = { ChipSound.Psg, ChipSound.PsgScc, ChipSound.FmPac, ChipSound.Pce, ChipSound.Nes, ChipSound.MegaDrive, ChipSound.GameBoy, ChipSound.Snes };

    private MidiSong chipSong;
    private Song chipSongSource;
    private InstrumentBank chipSongBank;
    private bool songChip;

    /// <summary>true = MIDI · ROL · IMS를 8-bit 칩 소리로 (<see cref="ChipStyle"/>), false = 원래 소리 (OPL FM).</summary>
    public bool ChipSynth { get; private set; }
    public ChipSound ChipStyle { get; private set; } = ChipSound.PsgScc;
    /// <summary>MIDI 모듈 MT-32 (에뮬레이터가 없으면 MT-32 음색 번호를 GM으로 바꿔 낸다). 8-bit 소리일 때는 GM.</summary>
    public bool Mt32Module { get; private set; }

    /// <summary>거친 음색 (칩이 낸 소리 그대로). false = 표준 (되먹임을 조금 줄이고 12kHz 위를 살짝 깎음). 재생 중에도 바로.</summary>
    public bool RawTone
    {
        get { return !engine.ToneStandard; }
        set { engine.ToneStandard = !value; }
    }

    /// <summary>그 시절 스피커 (작은 PC 스피커 흉내: 저음이 빠지고 중고음이 튐). 재생 중에도 바로.</summary>
    public bool VintageSpeaker
    {
        get { return engine.VintageSpeaker; }
        set { engine.VintageSpeaker = value; }
    }

    /// <summary>
    /// 소리 고르기: chip = null이면 원래 소리 (OPL FM), 아니면 그 8-bit 풍. mt32 = MIDI 모듈.
    /// 재생 중이면 같은 자리에서 새 소리로 다시 시작한다 (멈춰 있으면 다음 재생부터).
    /// </summary>
    public void SetSound(ChipSound? chip, bool mt32)
    {
        bool changed = ChipSynth != chip.HasValue || (chip.HasValue && ChipStyle != chip.Value) || Mt32Module != mt32;
        ChipSynth = chip.HasValue;
        if (chip.HasValue) ChipStyle = chip.Value;
        Mt32Module = mt32;
        if (changed && State == PlayState.Playing && (Kind == SongKind.Midi || Kind == SongKind.Adlib)) StartAt(engine.PositionTick);
    }

    /// <summary>지금 소리 이름 (곡 정보 줄): "OPL FM" · "8-bit: …" · VGM 등은 빈 글.</summary>
    public string SoundText
    {
        get
        {
            if (Kind != SongKind.Midi && Kind != SongKind.Adlib) return "";
            if (ChipSynth) return ChipMidiSynth.Label(ChipStyle);
            if (Kind != SongKind.Midi) return "OPL2";
            if (!(Mt32Module || midiForceMt32)) return "OPL FM";
            return Mt32Rom != null ? "MT-32 에뮬레이터 · " + Mt32Rom : "OPL FM · MT-32 → GM" + (Mt32Error != null ? " (" + Mt32Error + ")" : "");
        }
    }

    /// <summary>열린 MT-32 ROM 설명 (예: "MT32_CONTROL.ROM + MT32_PCM.ROM"), 아직 안 열었거나 못 열면 null.</summary>
    public string Mt32Rom { get; private set; }
    /// <summary>MT-32 에뮬레이터를 열지 못한 까닭 (라이브러리 · ROM 없음), 없으면 null.</summary>
    public string Mt32Error { get; private set; }

    /// <summary>MT-32 에뮬레이터를 연다 (같은 폴더면 엔진이 다시 쓰므로 처음 한 번만 무겁다).</summary>
    private void EnsureMt32()
    {
        string folder = settings.Mt32RomFolder;
        if (string.IsNullOrEmpty(folder)) { Mt32Rom = null; Mt32Error = "ROM 폴더 없음"; return; }
        Mt32Rom = engine.OpenMt32(folder, out string err);
        Mt32Error = Mt32Rom == null ? err : null;
    }

    private MidiSong ChipSongOf(Song s, InstrumentBank b)
    {
        if (chipSong == null || chipSongSource != s || chipSongBank != b)
        {
            chipSong = SongToMidi.Convert(s, name => b != null ? b.Find(name) : null);
            chipSongSource = s;
            chipSongBank = b;
        }
        return chipSong;
    }

    /// <summary>ROL · IMS를 8-bit으로 낼 때: 보이스 v의 그 틱 악기 (곡의 악기 이벤트, 엔진은 GM 이름을 알려 주므로).</summary>
    private string TimbreAt(int v, double tick)
    {
        if (song == null || v >= song.VoiceCount) return null;
        string name = null;
        foreach (var t in song.Voices[v].Timbres) { if (t.Tick > tick) break; name = t.InstrumentName; }
        return name ?? (song.Voices[v].Timbres.Count > 0 ? song.Voices[v].Timbres[0].InstrumentName : null);
    }

    // ---------------------------------------------------------------- 가사

    /// <summary>같은 이름 ISS 가사 (ROL · IMS · SOP), 없으면 null.</summary>
    public IssLyrics Lyrics { get; private set; }

    /// <summary>가사 파일 이름 + 인코딩 (곡 정보 줄), 없으면 null.</summary>
    public string LyricsText
    {
        get { return Lyrics == null ? null : System.IO.Path.GetFileNameWithoutExtension(Path) + ".ISS" + (Lyrics.Encoding == KoreanText.Kind.Wansung ? " · 완성형" : " · 조합형"); }
    }

    /// <summary>지금 위치의 가사 상태 (지금 줄 · 칠한 칸 · 다음 줄). 가사가 없으면 null.</summary>
    public LyricState LyricStateNow()
    {
        if (Lyrics == null || !HasSong) return null;
        double tick = CurrentTick;
        // SOP의 ISS도 240/박 기준
        if (Kind == SongKind.Sop) return Lyrics.StateAt(tick, sopSong.TickBeat, 0);
        if (Kind == SongKind.Adlib) return Lyrics.StateAt(tick, song.TickBeat, song.FileTickBeat);
        return null;
    }

    private static IssLyrics FindLyrics(string songPath)
    {
        string side = SideFile(songPath, ".iss");
        if (side == null) return null;
        try { return IssLyrics.Load(side); }
        catch (Exception) { return null; }
    }
}
