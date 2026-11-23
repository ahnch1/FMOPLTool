using System;
using System.Collections.Generic;
using AdlibComposer.Audio;
using AdlibComposer.Player;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace AdlibComposer.Droid;

/// <summary>
/// 표시 묶음 (가로 · 세로 데크 공용): [표시 방식 단추 Implay · Ocplay · 노래방 · 노래방(Cyberfunk)] / [가사 상자] / [표시 화면].
/// Implay = 가사 + 채널 믹서, Ocplay = 가사 + 작은 악보 · 세로 음량 막대, 노래방 = 그림 + 가사, Cyberfunk = 별 · 도형 + 가사.
/// 채널 상태(음 · 음량 막대)도 여기서 계산하고, 건반에 칠할 음은 <see cref="Lit"/>로 내준다.
/// <para>가로 데크(wide)는 표시 방식 단추 없이 채널 믹서(위) · 작은 악보(아래)를 함께 보이고, 가사 상자는 <see cref="LyricsBox"/>로 내줘 데크가 건반 바로 위에 놓는다.</para>
/// </summary>
internal sealed class JukeboxStage : LinearLayout
{
    private readonly Func<bool> busy;
    private JukeboxSession S { get { return AppEnv.Session; } }

    private readonly Button[] modeButtons;
    private readonly LyricsView lyricsView;
    private readonly MixerView mixer;
    private readonly OcplayView ocView;
    private readonly KaraokeView karaoke;
    private readonly CyberView cyber;
    private int shownMode = -1;
    private readonly bool wide;

    /// <summary>가사 상자 (wide면 이 묶음에 들어 있지 않음 — 데크가 붙인다).</summary>
    public View LyricsBox { get { return lyricsView; } }

    // 채널 상태
    private readonly VoiceState[] states = new VoiceState[PlaybackOptions.Voices];
    private readonly float[] levels = new float[PlaybackOptions.Voices];
    private readonly int[] lastCounts = new int[PlaybackOptions.Voices];
    private string lastPath;

    /// <summary>건반에 칠할 음 → 채널 색 (Update가 채운다).</summary>
    public readonly Dictionary<int, Color> Lit = new Dictionary<int, Color>();

    /// <param name="lyricsDp">가사 상자 높이 (dp).</param>
    /// <param name="wide">가로 데크: 단추 없이 믹서 · 악보를 위아래로, 가사 상자는 밖에서 붙임.</param>
    public JukeboxStage(Context c, Func<bool> busy, int lyricsDp, bool wide = false) : base(c)
    {
        this.busy = busy;
        this.wide = wide;
        Orientation = Orientation.Vertical;
        float d = c.Resources.DisplayMetrics.Density;
        int Dp(int v) => (int)(v * d + 0.5f);

        var modes = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        modeButtons = new Button[wide ? 0 : JukeboxPrefs.ViewModeNames.Length];
        for (int i = 0; i < modeButtons.Length; i++)
        {
            int m = i;
            var b = new Button(c) { Text = JukeboxPrefs.ViewModeNames[i], TextSize = 14 };
            b.SetAllCaps(false);
            b.SetMinWidth(0); b.SetMinimumWidth(0); b.SetMinHeight(0); b.SetMinimumHeight(Dp(40));
            b.SetPadding(Dp(4), 0, Dp(4), 0);
            b.SetSingleLine(true);
            b.Click += delegate { JukeboxPrefs.SetViewMode(m); };
            modeButtons[i] = b;
            modes.AddView(b, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, i == 3 ? 1.6f : 1) { RightMargin = i < modeButtons.Length - 1 ? Dp(4) : 0 });
        }
        if (!wide) AddView(modes, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(6) });

        lyricsView = new LyricsView(c);
        if (!wide) AddView(lyricsView, new LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(lyricsDp)) { BottomMargin = Dp(6) });
        mixer = new MixerView(c, this);
        ocView = new OcplayView(c);
        karaoke = new KaraokeView(c);
        cyber = new CyberView(c);
        if (wide)
        {
            // 믹서 (채널 줄) 위 · 작은 악보 아래. 노래방 · Cyberfunk는 가로 데크에서 쓰지 않는다
            AddView(mixer, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 3) { BottomMargin = Dp(6) });
            AddView(ocView, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 2));
        }
        else
        {
            var screen = new FrameLayout(c);
            foreach (var v in new View[] { mixer, ocView, karaoke, cyber }) screen.AddView(v, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
            AddView(screen, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        }

        ApplyViewMode();
        JukeboxPrefs.Changed += ApplyViewMode;
    }

    /// <summary>표시 방식에 따라 보이는 것을 바꾼다. 가사 상자는 Implay · Ocplay에서, 가사를 쓸 수 없는 곡(VGM · MIDI · 트래커)이면 숨김.</summary>
    private void ApplyViewMode()
    {
        int m = JukeboxPrefs.ViewMode;
        var s = S;
        bool lyricKind = s.Kind == SongKind.Adlib || s.Kind == SongKind.Sop || !s.HasSong;
        if (wide)
        {
            lyricsView.Visibility = lyricKind ? ViewStates.Visible : ViewStates.Gone;
            mixer.Visibility = ocView.Visibility = ViewStates.Visible;
            karaoke.Visibility = cyber.Visibility = ViewStates.Gone;
            shownMode = m;
            return;
        }
        lyricsView.Visibility = (m == JukeboxPrefs.Implay || m == JukeboxPrefs.Ocplay) && lyricKind ? ViewStates.Visible : ViewStates.Gone;
        mixer.Visibility = m == JukeboxPrefs.Implay ? ViewStates.Visible : ViewStates.Gone;
        ocView.Visibility = m == JukeboxPrefs.Ocplay ? ViewStates.Visible : ViewStates.Gone;
        karaoke.Visibility = m == JukeboxPrefs.Karaoke ? ViewStates.Visible : ViewStates.Gone;
        cyber.Visibility = m == JukeboxPrefs.Cyber ? ViewStates.Visible : ViewStates.Gone;
        for (int i = 0; i < modeButtons.Length; i++) modeButtons[i].SetBackgroundColor(i == m ? WideDeck.Accent : Color.ParseColor("#3A414B"));
        if (m == JukeboxPrefs.Karaoke && shownMode != m) karaoke.Reload(s.Path);
        if (m == JukeboxPrefs.Cyber) cyber.Invalidate();
        shownMode = m;
    }

    /// <summary>노래방 그림 폴더 설정이 바뀐 뒤.</summary>
    public void ReloadKaraoke() { karaoke.Reload(S.Path); }

    /// <summary>화면 갱신마다: 가사 상태 · 채널 줄 · 음량 막대 · 건반 음.</summary>
    public void Update()
    {
        var s = S;
        bool playing = s.State == PlayState.Playing;
        if (s.Path != lastPath)
        {
            lastPath = s.Path;
            Array.Clear(levels, 0, levels.Length);
            Array.Clear(lastCounts, 0, lastCounts.Length);
            ocView.Clear();
            karaoke.SetSong(s.Path);
            ApplyViewMode();
        }

        // 가사
        var lyr = s.Lyrics;
        var lst = s.LyricStateNow();
        lyricsView.Lyrics = lyr; lyricsView.State = lst;
        karaoke.Lyrics = lyr; karaoke.State = lst; karaoke.Title = s.HasSong ? s.Title : "";
        cyber.Lyrics = lyr; cyber.State = lst; cyber.Title = s.HasSong ? s.Title : "";
        if (lyricsView.Visibility == ViewStates.Visible) lyricsView.Invalidate();
        if (karaoke.Visibility == ViewStates.Visible) { karaoke.Tick(); karaoke.Invalidate(); }
        if (cyber.Visibility == ViewStates.Visible) cyber.Invalidate();   // 화면을 돌려 다시 붙었을 때도 움직이게

        // 채널: 엔진 보이스 상태 → 줄. 음량 막대는 노트온에 볼륨만큼 올라가고 천천히 내려온다 (Windows와 같은 식)
        if (!s.GetVoiceStates(states)) for (int v = 0; v < states.Length; v++) states[v] = new VoiceState { Note = -1 };
        var chs = s.Channels;
        var rows = mixer.Rows;
        while (rows.Count < chs.Count) rows.Add(new MixerRow());
        if (rows.Count > chs.Count) rows.RemoveRange(chs.Count, rows.Count - chs.Count);
        Lit.Clear();
        for (int v = 0; v < chs.Count; v++)
        {
            var row = rows[v];
            var vs = states[v];
            row.Label = chs[v].Label;
            row.Used = chs[v].Used;
            row.Color = WideDeck.VoiceColor(v);
            row.Muted = s.Options.VoiceMute[v];
            row.Pitch = s.Options.VoiceTranspose[v];
            row.Instrument = s.InstrumentOf(v, vs, playing);
            row.Found = s.InstrumentFound(row.Instrument);
            row.Note = playing ? vs.Note : -1;
            float target = row.Note >= 0 ? Math.Max(0.15f, vs.Volume) : 0f;
            if (playing && vs.NoteOnCount != lastCounts[v]) { levels[v] = Math.Max(levels[v], Math.Max(0.15f, vs.Volume)); lastCounts[v] = vs.NoteOnCount; }
            else levels[v] = row.Note >= 0 ? Math.Max(target * 0.75f, levels[v] * 0.94f) : levels[v] * 0.7f;
            if (!playing) levels[v] *= 0.5f;
            row.Level = levels[v];
        }
        // 건반: 뒤 채널부터 칠해 같은 음이면 앞 채널 색
        if (playing)
            for (int v = chs.Count - 1; v >= 0; v--)
            {
                if (rows[v].Muted) continue;
                int n = s.SoundingNote(v, states[v]);
                if (n >= 0) Lit[n] = rows[v].Color;
            }
        if (mixer.Visibility == ViewStates.Visible) mixer.Invalidate();
        if (ocView.Visibility == ViewStates.Visible) ocView.Sample(rows, playing);
    }

    // 믹서 조작 (MixerView가 부른다)
    internal void ToggleMute(int v) { if (!busy()) S.SetMute(v, !S.Options.VoiceMute[v]); }
    internal void Solo(int v) { if (!busy()) S.Solo(v); }
    internal void Pitch(int v, int d) { if (!busy()) S.SetVoicePitch(v, S.Options.VoiceTranspose[v] + d); }
}
