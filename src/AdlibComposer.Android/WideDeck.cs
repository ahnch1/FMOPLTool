using System;
using System.Collections.Generic;
using System.Linq;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Player;
using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;

namespace AdlibComposer.Droid;

/// <summary>
/// 가로 화면 재생 데크 = Windows Jukebox 배치 (안드로이드 기본 뷰 + 직접 그리기, 외부 라이브러리 없음):
/// <code>
///   [곡 정보: 상태 줄 · 제목 · 형식 · 게임 정보 | 조작: ⏮ −5초 ▶ ■ +5초 ⏭ · 다음 곡 자동 · 반복 / 속도 · 키 · 볼륨 · 초기화 / 진행 막대]
///   [채널 믹서: 채널마다 한 줄 — 켜기/끄기 칸 · 번호 · 악기(+ 울리는 음) · 음량 막대 · 채널 음높이]
///   [건반 C0 ~ B8: 채널마다 울리는 음을 채널 색으로]
/// </code>
/// 세로 화면은 MainActivity의 기존 데크를 그대로 쓴다.
/// </summary>
internal sealed class WideDeck : LinearLayout
{
    // 색 (앱의 어두운 바탕에 맞춤)
    internal static readonly Color Back = Color.ParseColor("#1E2228");
    internal static readonly Color Panel = Color.ParseColor("#2A3039");
    internal static readonly Color Inset = Color.ParseColor("#171B20");
    internal static readonly Color Text = Color.ParseColor("#E6EAF0");
    internal static readonly Color Muted = Color.ParseColor("#7D8794");
    internal static readonly Color Lcd = Color.ParseColor("#5FD3C6");
    internal static readonly Color Accent = Color.ParseColor("#26A69A");
    internal static readonly Color Warn = Color.ParseColor("#E0675A");
    internal static readonly Color MeterOff = Color.ParseColor("#2C333C");

    /// <summary>채널 색 = Windows 보이스 색 기본값 (11색, 넘으면 돌려 씀).</summary>
    internal static readonly Color[] VoiceColors =
    {
        Color.Rgb(38, 166, 154), Color.Rgb(66, 133, 244), Color.Rgb(234, 88, 76), Color.Rgb(244, 180, 0),
        Color.Rgb(142, 106, 216), Color.Rgb(246, 128, 38), Color.Rgb(52, 168, 83), Color.Rgb(222, 84, 148),
        Color.Rgb(0, 151, 199), Color.Rgb(141, 110, 99), Color.Rgb(120, 132, 148),
    };
    internal static Color VoiceColor(int v) { return VoiceColors[((v % VoiceColors.Length) + VoiceColors.Length) % VoiceColors.Length]; }

    /// <summary>재생 묶음 (곡 열기 중이면 busy → 조작하지 않음).</summary>
    private readonly Func<bool> busy;
    private JukeboxSession S { get { return AppEnv.Session; } }

    public event Action PrevSong, NextSong, PlayPause, StopPressed;

    public readonly CheckBox AutoNext, Repeat;
    private readonly TextView statusLine, statusRow, titleText, infoText, detailText, posText, lenText, speedValue, keyValue, volValue, noteText;
    private readonly Button playButton;
    private readonly SeekBar seek;
    private readonly MixerView mixer;
    private readonly KeyboardView keys;
    public bool SeekDragging { get; private set; }

    // 믹서 상태
    private readonly VoiceState[] states = new VoiceState[PlaybackOptions.Voices];
    private readonly float[] levels = new float[PlaybackOptions.Voices];
    private readonly int[] lastCounts = new int[PlaybackOptions.Voices];
    private string lastPath;

    public WideDeck(Context c, Func<bool> busy) : base(c)
    {
        this.busy = busy;
        Orientation = Orientation.Vertical;
        SetBackgroundColor(Back);
        SetPadding(Dp(10), Dp(4), Dp(10), Dp(6));

        // ---------------------------------------------------------------- 맨 위 막대: 음색 · 스피커 · 원래 소리 | MIDI 신디 · 모듈 | 설정 (Windows ControlBar)
        var bar = new FlowRow(c, Dp(14), Dp(0));
        syncSound = SoundControls.Add(c, bar);
        var settingsButton = DeckButton(c, "⚙ 설정", delegate { SettingsRequested?.Invoke(); });
        bar.AddView(settingsButton);
        AddView(bar, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent) { BottomMargin = Dp(4) });

        // ---------------------------------------------------------------- 위: 곡 정보 | 조작
        var top = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        AddView(top, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        var info = Box(c);
        statusRow = Label(c, 13, Lcd, true);
        statusRow.SetSingleLine(true);
        statusRow.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        titleText = Label(c, 22, Accent, true);
        titleText.SetSingleLine(true);
        titleText.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        titleText.SetPadding(0, Dp(6), 0, Dp(4));
        infoText = Label(c, 14, Text, false);
        detailText = Label(c, 14, Muted, false);
        statusLine = Label(c, 13, Color.ParseColor("#E0A060"), false);
        statusLine.Visibility = ViewStates.Gone;
        noteText = Label(c, 12, Muted, false);
        noteText.Text = "믹서: 왼쪽 칸을 누르면 채널 켜기/끄기 · 길게 누르면 그 채널만 · − + = 채널 음높이";
        lyricsFileText = Label(c, 13, Muted, false);
        info.AddView(InsetBox(statusRow));
        info.AddView(titleText);
        info.AddView(infoText);
        info.AddView(detailText);
        info.AddView(lyricsFileText);
        info.AddView(statusLine);
        info.AddView(noteText);
        // 표시 방식 단추 (고른 것 = 청록)
        var modes = new FlowRow(c, Dp(4), Dp(2));
        modes.SetPadding(0, Dp(6), 0, 0);
        modeButtons = new Button[JukeboxPrefs.ViewModeNames.Length];
        for (int i = 0; i < modeButtons.Length; i++)
        {
            int m = i;
            modeButtons[i] = DeckButton(c, JukeboxPrefs.ViewModeNames[i], delegate { JukeboxPrefs.SetViewMode(m); ApplyViewMode(); });
            modes.AddView(modeButtons[i]);
        }
        info.AddView(modes);
        top.AddView(info, new LayoutParams(0, ViewGroup.LayoutParams.MatchParent, 1) { RightMargin = Dp(5) });

        var ctl = Box(c);
        // 재생 버튼 + 다음 곡 자동 · 반복
        var transport = new FlowRow(c, Dp(4), Dp(2));
        transport.AddView(DeckButton(c, "⏮", delegate { PrevSong?.Invoke(); }));
        transport.AddView(DeckButton(c, "−5초", delegate { if (!busy()) S.SeekBy(-5); }));
        playButton = DeckButton(c, "▶ 재생", delegate { PlayPause?.Invoke(); });
        playButton.SetBackgroundColor(Accent);
        transport.AddView(playButton);
        transport.AddView(DeckButton(c, "■ 정지", delegate { StopPressed?.Invoke(); }));
        transport.AddView(DeckButton(c, "+5초", delegate { if (!busy()) S.SeekBy(5); }));
        transport.AddView(DeckButton(c, "⏭", delegate { NextSong?.Invoke(); }));
        AutoNext = new CheckBox(c) { Text = "다음 곡 자동" };
        Repeat = new CheckBox(c) { Text = "반복 재생" };
        transport.AddView(AutoNext);
        transport.AddView(Repeat);
        ctl.AddView(transport);

        // 속도 · 키 · 볼륨 · 초기화
        var adjust = new FlowRow(c, Dp(12), Dp(2));
        adjust.SetPadding(0, Dp(6), 0, 0);
        adjust.AddView(Stepper(c, "속도", "느리게", "빠르게", out speedValue, d => { if (!busy()) S.SetSpeed(Math.Round(S.Options.Speed * 100 + d * 5) / 100.0); }));
        adjust.AddView(Stepper(c, "키", "♂", "♀", out keyValue, d => { if (!busy()) S.SetKey(S.Options.Transpose + d); }));
        adjust.AddView(Stepper(c, "볼륨", "−", "+", out volValue, d => { if (!busy()) S.SetGain(Math.Round(S.Options.Gain * 10 + d) / 10.0); }));
        adjust.AddView(DeckButton(c, "초기화", delegate { if (!busy()) S.ResetAdjustments(); }));
        ctl.AddView(adjust);

        // 진행 막대 + 시간
        var seekRow = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        seekRow.SetGravity(GravityFlags.CenterVertical);
        seekRow.SetPadding(0, Dp(6), 0, 0);
        posText = Label(c, 15, Lcd, true);
        lenText = Label(c, 15, Lcd, true);
        posText.Text = lenText.Text = "00:00";
        seek = new SeekBar(c) { Max = 1000 };
        seek.StartTrackingTouch += delegate { SeekDragging = true; };
        seek.StopTrackingTouch += delegate
        {
            SeekDragging = false;
            if (!busy() && S.HasSong) S.Seek(seek.Progress / 1000.0 * S.LengthSeconds);
        };
        seek.ProgressChanged += (o, e) => { if (e.FromUser) posText.Text = Clock(e.Progress / 1000.0 * S.LengthSeconds); };
        seekRow.AddView(InsetBox(posText));
        seekRow.AddView(seek, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1));
        seekRow.AddView(InsetBox(lenText));
        ctl.AddView(seekRow);
        top.AddView(ctl, new LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1) { LeftMargin = Dp(5) });

        // ---------------------------------------------------------------- 가운데: 표시 묶음 (표시 방식에 따라)
        // Implay = 가사 + 채널 믹서, Ocplay = 가사 + 작은 악보 · 세로 음량 막대, 노래방 = 그림 + 가사, Cyberfunk = 별 · 도형 + 가사
        var stage = new LinearLayout(c) { Orientation = Orientation.Vertical };
        lyricsView = new LyricsView(c);
        stage.AddView(lyricsView, new LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(92)) { BottomMargin = Dp(6) });
        var screen = new FrameLayout(c);
        mixer = new MixerView(c, this);
        ocView = new OcplayView(c);
        karaoke = new KaraokeView(c);
        cyber = new CyberView(c);
        foreach (var v in new View[] { mixer, ocView, karaoke, cyber }) screen.AddView(v, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        stage.AddView(screen, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1));
        AddView(stage, new LayoutParams(ViewGroup.LayoutParams.MatchParent, 0, 1) { TopMargin = Dp(8), BottomMargin = Dp(8) });

        // ---------------------------------------------------------------- 아래: 건반
        keys = new KeyboardView(c);
        AddView(keys, new LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent));

        ApplyViewMode();
        JukeboxPrefs.Changed += () => { syncSound(); ApplyViewMode(); };
    }

    public event Action SettingsRequested;
    private readonly Action syncSound;
    private readonly Button[] modeButtons;
    private readonly TextView lyricsFileText;
    private readonly LyricsView lyricsView;
    private readonly OcplayView ocView;
    private readonly KaraokeView karaoke;
    private readonly CyberView cyber;
    private int shownMode = -1;

    /// <summary>표시 방식에 따라 보이는 것을 바꾼다. 가사 상자는 Implay · Ocplay에서, 가사를 쓸 수 없는 곡(VGM · MIDI · 트래커)이면 숨김.</summary>
    private void ApplyViewMode()
    {
        int m = JukeboxPrefs.ViewMode;
        var s = S;
        bool lyricKind = s.Kind == SongKind.Adlib || s.Kind == SongKind.Sop || !s.HasSong;
        lyricsView.Visibility = (m == JukeboxPrefs.Implay || m == JukeboxPrefs.Ocplay) && lyricKind ? ViewStates.Visible : ViewStates.Gone;
        mixer.Visibility = m == JukeboxPrefs.Implay ? ViewStates.Visible : ViewStates.Gone;
        ocView.Visibility = m == JukeboxPrefs.Ocplay ? ViewStates.Visible : ViewStates.Gone;
        karaoke.Visibility = m == JukeboxPrefs.Karaoke ? ViewStates.Visible : ViewStates.Gone;
        cyber.Visibility = m == JukeboxPrefs.Cyber ? ViewStates.Visible : ViewStates.Gone;
        noteText.Visibility = m == JukeboxPrefs.Implay ? ViewStates.Visible : ViewStates.Gone;
        for (int i = 0; i < modeButtons.Length; i++) modeButtons[i].SetBackgroundColor(i == m ? Accent : Color.ParseColor("#3A414B"));
        if (m == JukeboxPrefs.Karaoke && shownMode != m) karaoke.Reload(s.Path);
        if (m == JukeboxPrefs.Cyber) cyber.Invalidate();
        shownMode = m;
    }

    /// <summary>노래방 그림 폴더 설정이 바뀐 뒤.</summary>
    public void ReloadKaraoke() { karaoke.Reload(S.Path); }

    // ================================================================ 갱신 (MainActivity.Refresh가 부른다)

    /// <summary>상태 글 (여는 중 · zip 푸는 중 · 오류). 빈 글이면 숨김.</summary>
    public void SetStatus(string text)
    {
        statusLine.Text = text ?? "";
        statusLine.Visibility = string.IsNullOrEmpty(text) ? ViewStates.Gone : ViewStates.Visible;
    }

    /// <param name="load">렌더 부하 글 (재생 중일 때), 상태 줄 끝에.</param>
    public void Update(string load)
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

        titleText.Text = s.HasSong ? s.Title : "곡을 고르세요";
        string sound = s.SoundText;
        infoText.Text = s.HasSong ? s.Info + (sound.Length > 0 && (s.ChipSynth || s.Kind == SongKind.Midi) ? " → " + sound : "") : "왼쪽 목록에서 곡을 누르면 재생합니다";
        detailText.Text = s.Detail;
        detailText.Visibility = s.Detail.Length > 0 ? ViewStates.Visible : ViewStates.Gone;
        bool lyricKind = s.Kind == SongKind.Adlib || s.Kind == SongKind.Sop;
        lyricsFileText.Text = "가사  " + (s.LyricsText ?? "없음");
        lyricsFileText.Visibility = lyricKind ? ViewStates.Visible : ViewStates.Gone;

        // 가사 · 표시 방식
        var lyr = s.Lyrics;
        var lst = s.LyricStateNow();
        lyricsView.Lyrics = lyr; lyricsView.State = lst;
        karaoke.Lyrics = lyr; karaoke.State = lst; karaoke.Title = s.HasSong ? s.Title : "";
        cyber.Lyrics = lyr; cyber.State = lst; cyber.Title = s.HasSong ? s.Title : "";
        if (lyricsView.Visibility == ViewStates.Visible) lyricsView.Invalidate();
        if (karaoke.Visibility == ViewStates.Visible) { karaoke.Tick(); karaoke.Invalidate(); }
        if (cyber.Visibility == ViewStates.Visible) cyber.Invalidate();   // 화면을 돌려 다시 붙었을 때도 움직이게

        double len = s.LengthSeconds, pos = s.PositionSeconds;
        string state = playing ? "▶ 재생 중" : s.State == PlayState.Paused ? "❚❚ 일시정지" : s.State == PlayState.Stopped ? "■ 정지" : "";
        double bpm = s.Bpm;
        statusRow.Text = Clock(pos) + " / " + Clock(len) + "    " + (bpm > 0 ? "♩=" + Math.Round(bpm) + "  " : "")
            + "속도 " + Percent(s.Options.Speed) + "  키 " + KeyText(s.Options.Transpose) + "  볼륨 " + Percent(s.Options.Gain) + "    " + state
            + (string.IsNullOrEmpty(load) ? "" : "    " + load);
        lenText.Text = Clock(len);
        if (!SeekDragging)
        {
            seek.Progress = len > 0 ? (int)(pos / len * 1000) : 0;
            posText.Text = Clock(pos);
        }
        seek.Enabled = s.HasSong;
        playButton.Text = playing ? "❚❚ 일시정지" : "▶ 재생";
        speedValue.Text = Percent(s.Options.Speed);
        keyValue.Text = KeyText(s.Options.Transpose);
        volValue.Text = Percent(s.Options.Gain);

        // 믹서: 엔진 보이스 상태 → 줄. 음량 막대는 노트온에 볼륨만큼 올라가고 천천히 내려온다 (Windows와 같은 식)
        if (!s.GetVoiceStates(states)) for (int v = 0; v < states.Length; v++) states[v] = new VoiceState { Note = -1 };
        var chs = s.Channels;
        var rows = mixer.Rows;
        while (rows.Count < chs.Count) rows.Add(new MixerRow());
        if (rows.Count > chs.Count) rows.RemoveRange(chs.Count, rows.Count - chs.Count);
        keys.Lit.Clear();
        for (int v = 0; v < chs.Count; v++)
        {
            var row = rows[v];
            var vs = states[v];
            row.Label = chs[v].Label;
            row.Used = chs[v].Used;
            row.Color = VoiceColor(v);
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
                if (n >= 0) keys.Lit[n] = rows[v].Color;
            }
        if (mixer.Visibility == ViewStates.Visible) mixer.Invalidate();
        if (ocView.Visibility == ViewStates.Visible) ocView.Sample(rows, playing);
        keys.Invalidate();
    }

    // 믹서 조작 (MixerView가 부른다)
    internal void ToggleMute(int v) { if (!busy()) S.SetMute(v, !S.Options.VoiceMute[v]); }
    internal void Solo(int v) { if (!busy()) S.Solo(v); }
    internal void Pitch(int v, int d) { if (!busy()) S.SetVoicePitch(v, S.Options.VoiceTranspose[v] + d); }

    // ================================================================ 작은 도우미

    private static string Percent(double x) { return (int)Math.Round(x * 100) + "%"; }
    internal static string KeyText(int semis) { return semis == 0 ? "±0" : semis > 0 ? "+" + semis : "−" + (-semis); }

    internal static string Clock(double sec)
    {
        int t = (int)Math.Max(0, sec);
        return (t / 60).ToString("00") + ":" + (t % 60).ToString("00");
    }

    private int Dp(int v) { return (int)(v * Resources.DisplayMetrics.Density + 0.5f); }

    private LinearLayout Box(Context c)
    {
        var b = new LinearLayout(c) { Orientation = Orientation.Vertical };
        b.SetBackgroundColor(Panel);
        b.SetPadding(Dp(10), Dp(8), Dp(10), Dp(8));
        return b;
    }

    private View InsetBox(TextView t)
    {
        t.SetBackgroundColor(Inset);
        t.SetPadding(Dp(8), Dp(4), Dp(8), Dp(4));
        return t;
    }

    private static TextView Label(Context c, float size, Color color, bool bold)
    {
        var t = new TextView(c) { TextSize = size };
        t.SetTextColor(color);
        if (bold) t.SetTypeface(Typeface.Monospace, TypefaceStyle.Bold);
        return t;
    }

    private Button DeckButton(Context c, string text, EventHandler click)
    {
        var b = new Button(c) { Text = text, TextSize = 15 };
        b.SetAllCaps(false);
        b.SetMinWidth(0);
        b.SetMinimumWidth(Dp(52));
        b.SetMinHeight(0);
        b.SetMinimumHeight(Dp(44));
        b.SetPadding(Dp(12), 0, Dp(12), 0);
        b.Click += click;
        return b;
    }

    /// <summary>[이름] [−] 값 [+] (속도 · 키 · 볼륨).</summary>
    private View Stepper(Context c, string caption, string minus, string plus, out TextView value, Action<int> step)
    {
        var row = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        var cap = Label(c, 14, Muted, false);
        cap.Text = caption;
        cap.SetPadding(0, 0, Dp(6), 0);
        row.AddView(cap);
        row.AddView(DeckButton(c, minus, delegate { step(-1); }));
        var v = Label(c, 15, Lcd, true);
        v.Gravity = GravityFlags.Center;
        v.SetMinWidth(Dp(56));
        row.AddView(v);
        row.AddView(DeckButton(c, plus, delegate { step(1); }));
        value = v;
        return row;
    }
}

/// <summary>줄 바꿈 배치: 자식을 가로로 놓다가 넓이가 모자라면 다음 줄로 (Windows FlowLayoutPanel처럼).</summary>
internal sealed class FlowRow : ViewGroup
{
    private readonly int hGap, vGap;

    public FlowRow(Context c, int hGap, int vGap) : base(c) { this.hGap = hGap; this.vGap = vGap; }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int maxW = MeasureSpec.GetSize(widthMeasureSpec) - PaddingLeft - PaddingRight;
        int x = 0, y = 0, lineH = 0, usedW = 0;
        for (int i = 0; i < ChildCount; i++)
        {
            var ch = GetChildAt(i);
            if (ch.Visibility == ViewStates.Gone) continue;
            ch.Measure(MeasureSpec.MakeMeasureSpec(maxW, MeasureSpecMode.AtMost), MeasureSpec.MakeMeasureSpec(0, MeasureSpecMode.Unspecified));
            int cw = ch.MeasuredWidth, chh = ch.MeasuredHeight;
            if (x > 0 && x + cw > maxW) { y += lineH + vGap; x = 0; lineH = 0; }
            x += cw + hGap;
            usedW = Math.Max(usedW, x - hGap);
            lineH = Math.Max(lineH, chh);
        }
        int w = MeasureSpec.GetMode(widthMeasureSpec) == MeasureSpecMode.Exactly ? MeasureSpec.GetSize(widthMeasureSpec) : usedW + PaddingLeft + PaddingRight;
        SetMeasuredDimension(w, y + lineH + PaddingTop + PaddingBottom);
    }

    protected override void OnLayout(bool changed, int l, int t, int r, int b)
    {
        int maxW = r - l - PaddingLeft - PaddingRight;
        int x = 0, y = 0, lineH = 0;
        for (int i = 0; i < ChildCount; i++)
        {
            var ch = GetChildAt(i);
            if (ch.Visibility == ViewStates.Gone) continue;
            int cw = ch.MeasuredWidth, chh = ch.MeasuredHeight;
            if (x > 0 && x + cw > maxW) { y += lineH + vGap; x = 0; lineH = 0; }
            ch.Layout(PaddingLeft + x, PaddingTop + y, PaddingLeft + x + cw, PaddingTop + y + chh);
            x += cw + hGap;
            lineH = Math.Max(lineH, chh);
        }
    }
}

/// <summary>믹서 한 줄의 표시값.</summary>
internal sealed class MixerRow
{
    public string Label = "", Instrument;
    public bool Used, Found = true, Muted;
    public int Note = -1, Pitch;
    public float Level;
    public Color Color;
}

/// <summary>
/// 채널 믹서 (Windows ChannelMixer 배치): 채널마다 한 줄 [켜기/끄기 칸] [번호] [악기 + 울리는 음] [음량 막대 40칸] [− 음높이 +].
/// 칸 누르기 = 켜기/끄기, 길게 누르기 = 그 채널만 (솔로), − + = 채널 음높이. 줄이 넘치면 위아래로 끌어 넘긴다.
/// </summary>
internal sealed class MixerView : View
{
    public readonly List<MixerRow> Rows = new List<MixerRow>();
    private readonly WideDeck deck;
    private readonly GestureDetector gestures;
    private readonly Paint paint = new Paint(PaintFlags.AntiAlias);
    private readonly float d;
    private float scroll;
    private const int Segments = 40;

    public MixerView(Context c, WideDeck deck) : base(c)
    {
        this.deck = deck;
        d = c.Resources.DisplayMetrics.Density;
        gestures = new GestureDetector(c, new Listener(this));
        SetBackgroundColor(WideDeck.Panel);
    }

    private float RowH { get { return 36 * d; } }
    private float Gap { get { return 3 * d; } }
    private float Pad { get { return 6 * d; } }
    private float ContentH { get { return Pad * 2 + Rows.Count * (RowH + Gap); } }

    private struct Cols { public RectF Box, Select, Label, Inst, Meter, Minus, Pitch, Plus; }

    private Cols RowCols(int i)
    {
        var c = new Cols();
        float top = Pad + i * (RowH + Gap) - scroll;
        c.Box = new RectF(Pad, top, Width - Pad, top + RowH);
        float y = top + 4 * d, h = RowH - 8 * d, bottom = y + h;
        float right = c.Box.Right - 5 * d;
        float bw = 34 * d;
        c.Plus = new RectF(right - bw, y, right, bottom); right -= bw + 2 * d;
        c.Pitch = new RectF(right - 48 * d, y, right, bottom); right -= 48 * d + 2 * d;
        c.Minus = new RectF(right - bw, y, right, bottom); right -= bw + 8 * d;
        float x = c.Box.Left + 5 * d;
        c.Select = new RectF(x, y, x + h, bottom); x += h + 6 * d;
        c.Label = new RectF(x, y, x + 36 * d, bottom); x += 38 * d;
        float instW = Math.Max(90 * d, Math.Min(260 * d, right - x - 100 * d));
        c.Inst = new RectF(x, y, x + instW, bottom); x += instW + 8 * d;
        c.Meter = right - x >= 60 * d ? new RectF(x, y, right, bottom) : new RectF(x, y, x, bottom);
        return c;
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        scroll = Math.Max(0, Math.Min(Math.Max(0, ContentH - Height), scroll));
        paint.TextSize = 14 * d;
        paint.SetTypeface(Typeface.Create(Typeface.Monospace, TypefaceStyle.Bold));
        if (Rows.Count == 0)
        {
            paint.Color = WideDeck.Muted;
            paint.TextAlign = Paint.Align.Center;
            canvas.DrawText("채널 믹서 — 곡을 재생하면 채널마다 악기 · 음 · 음량이 보입니다", Width / 2f, Height / 2f, paint);
            paint.TextAlign = Paint.Align.Left;
            return;
        }
        for (int i = 0; i < Rows.Count; i++)
        {
            var c = RowCols(i);
            if (c.Box.Bottom < 0 || c.Box.Top > Height) continue;
            var row = Rows[i];
            bool playing = row.Note >= 0 && !row.Muted;
            Fill(canvas, c.Box, WideDeck.Inset);

            // 켜기/끄기 칸: 켜짐 = 채널 색 (울리면 밝게, 쉬면 옅게), 끔 = 회색 + OFF
            Color sel = row.Muted ? WideDeck.MeterOff : playing ? row.Color : Blend(row.Color, WideDeck.Inset, row.Used ? 0.45f : 0.75f);
            Fill(canvas, c.Select, sel);
            if (row.Muted) CenterText(canvas, c.Select, "OFF", WideDeck.Warn, 10 * d);
            CenterText(canvas, c.Label, row.Label, row.Used ? WideDeck.Text : WideDeck.Muted, 14 * d);

            // 악기 (찾지 못한 악기는 빨강 + 취소선, 안 쓰는 채널은 흐리게) + 오른쪽에 울리는 음
            Fill(canvas, c.Inst, Color.ParseColor("#101317"));
            string inst = string.IsNullOrEmpty(row.Instrument) ? "—" : row.Instrument.ToUpperInvariant();
            string note = row.Note >= 0 ? NoteHelper.Name(row.Note) : "";
            paint.TextSize = 13 * d;
            float noteW = note.Length > 0 ? paint.MeasureText(note) + 6 * d : 0;
            float nameW = c.Inst.Width() - 10 * d - noteW;
            string shown = Ellipsize(inst, nameW);
            paint.Color = !row.Used ? WideDeck.Muted : row.Found ? WideDeck.Text : WideDeck.Warn;
            float baseY = c.Inst.CenterY() - (paint.Ascent() + paint.Descent()) / 2;
            canvas.DrawText(shown, c.Inst.Left + 5 * d, baseY, paint);
            if (row.Used && !row.Found && !string.IsNullOrEmpty(row.Instrument))
                canvas.DrawLine(c.Inst.Left + 5 * d, c.Inst.CenterY(), c.Inst.Left + 5 * d + paint.MeasureText(shown), c.Inst.CenterY(), paint);
            if (noteW > 0)
            {
                paint.Color = WideDeck.Lcd;
                canvas.DrawText(note, c.Inst.Right - noteW, baseY, paint);
            }

            if (c.Meter.Width() > 0) DrawMeter(canvas, c.Meter, row.Muted ? 0 : row.Level);

            // 음높이 [−] +00 [+]
            Fill(canvas, c.Minus, WideDeck.MeterOff);
            Fill(canvas, c.Plus, WideDeck.MeterOff);
            CenterText(canvas, c.Minus, "−", WideDeck.Text, 16 * d);
            CenterText(canvas, c.Plus, "+", WideDeck.Text, 16 * d);
            string pt = (row.Pitch < 0 ? "−" : "+") + Math.Abs(row.Pitch).ToString("00");
            CenterText(canvas, c.Pitch, pt, row.Pitch != 0 ? WideDeck.Warn : WideDeck.Lcd, 13 * d);
        }
    }

    /// <summary>40칸 막대: 켜진 칸 = 청록 (끝 20%는 짙게), 꺼진 칸 = 어두운 회색.</summary>
    private void DrawMeter(Canvas canvas, RectF r, float level)
    {
        Fill(canvas, r, Color.ParseColor("#101317"));
        float gap = 2 * d;
        float inX = r.Left + 3 * d, inW = r.Width() - 6 * d;
        float seg = (inW - gap * (Segments - 1)) / Segments;
        int lit = (int)Math.Round(Math.Max(0, Math.Min(1, level)) * Segments);
        for (int s = 0; s < Segments; s++)
        {
            float x = inX + s * (seg + gap);
            paint.Color = s < lit ? (s >= Segments * 8 / 10 ? Color.ParseColor("#1E7F76") : WideDeck.Accent) : WideDeck.MeterOff;
            canvas.DrawRect(x, r.Top + 4 * d, x + seg, r.Bottom - 4 * d, paint);
        }
    }

    private void Fill(Canvas canvas, RectF r, Color color)
    {
        paint.Color = color;
        canvas.DrawRect(r, paint);
    }

    private void CenterText(Canvas canvas, RectF r, string text, Color color, float size)
    {
        paint.TextSize = size;
        paint.Color = color;
        paint.TextAlign = Paint.Align.Center;
        canvas.DrawText(text, r.CenterX(), r.CenterY() - (paint.Ascent() + paint.Descent()) / 2, paint);
        paint.TextAlign = Paint.Align.Left;
    }

    private string Ellipsize(string s, float width)
    {
        if (paint.MeasureText(s) <= width) return s;
        while (s.Length > 1 && paint.MeasureText(s + "…") > width) s = s.Substring(0, s.Length - 1);
        return s + "…";
    }

    private static Color Blend(Color a, Color b, float t)
    {
        return Color.Rgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
    }

    public override bool OnTouchEvent(MotionEvent e)
    {
        gestures.OnTouchEvent(e);
        return true;
    }

    /// <summary>누른 곳: 줄 번호와 칸 (0 = 켜기/끄기, -1 = −, +1 = +, 2 = 그 밖).</summary>
    private int Hit(float x, float y, out int part)
    {
        part = 2;
        for (int i = 0; i < Rows.Count; i++)
        {
            var c = RowCols(i);
            if (!c.Box.Contains(x, y)) continue;
            if (c.Select.Contains(x, y) || c.Label.Contains(x, y)) part = 0;
            else if (c.Minus.Contains(x, y)) part = -1;
            else if (c.Plus.Contains(x, y)) part = 1;
            return i;
        }
        return -1;
    }

    private sealed class Listener : GestureDetector.SimpleOnGestureListener
    {
        private readonly MixerView v;
        public Listener(MixerView v) { this.v = v; }

        public override bool OnDown(MotionEvent e) { return true; }

        public override bool OnSingleTapUp(MotionEvent e)
        {
            int i = v.Hit(e.GetX(), e.GetY(), out int part);
            if (i < 0) return false;
            if (part == 0) v.deck.ToggleMute(i);
            else if (part == -1 || part == 1) v.deck.Pitch(i, part);
            v.Invalidate();
            return true;
        }

        public override void OnLongPress(MotionEvent e)
        {
            int i = v.Hit(e.GetX(), e.GetY(), out int part);
            if (i >= 0 && part == 0) { v.deck.Solo(i); v.PerformHapticFeedback(FeedbackConstants.LongPress); v.Invalidate(); }
        }

        public override bool OnScroll(MotionEvent e1, MotionEvent e2, float distanceX, float distanceY)
        {
            v.scroll = Math.Max(0, Math.Min(Math.Max(0, v.ContentH - v.Height), v.scroll + distanceY));
            v.Invalidate();
            return true;
        }
    }
}

/// <summary>건반 C0 ~ B8 (9옥타브): 울리는 건반은 채널 색으로 꽉 칠한다. 높이 = 흰 건반 넓이의 5배.</summary>
internal sealed class KeyboardView : View
{
    public readonly Dictionary<int, Color> Lit = new Dictionary<int, Color>();
    private const int Low = 12, High = 119, WhiteKeys = 63;
    private static readonly bool[] Black = { false, true, false, true, false, false, true, false, true, false, true, false };
    private readonly Paint paint = new Paint();

    public KeyboardView(Context c) : base(c) { SetBackgroundColor(WideDeck.Panel); }

    protected override void OnMeasure(int widthMeasureSpec, int heightMeasureSpec)
    {
        int w = MeasureSpec.GetSize(widthMeasureSpec);
        float d = Resources.DisplayMetrics.Density;
        int h = (int)Math.Max(48 * d, Math.Min(110 * d, w / (float)WhiteKeys * 5));
        SetMeasuredDimension(w, h + (int)(12 * d));
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float d = Resources.DisplayMetrics.Density;
        float pad = 6 * d;
        float left = pad, top = pad, width = Width - pad * 2, height = Height - pad * 2;
        float kw = width / WhiteKeys;
        // 흰 건반
        int wi = 0;
        for (int n = Low; n <= High; n++)
        {
            if (Black[n % 12]) continue;
            float x = left + wi * kw;
            paint.Color = Lit.TryGetValue(n, out var col) ? col : Color.ParseColor("#C9CED6");
            canvas.DrawRect(x, top, x + kw - 1, top + height, paint);
            if (n % 12 == 0 && !Lit.ContainsKey(n))
            {
                paint.Color = Color.ParseColor("#7D8794");
                paint.TextSize = Math.Min(11 * d, kw * 0.8f);
                paint.TextAlign = Paint.Align.Center;
                canvas.DrawText("C" + (n / 12 - 1), x + kw / 2, top + height - 3 * d, paint);
                paint.TextAlign = Paint.Align.Left;
            }
            wi++;
        }
        // 검은 건반 (흰 건반 경계 위에)
        wi = 0;
        for (int n = Low; n <= High; n++)
        {
            if (!Black[n % 12]) { wi++; continue; }
            float x = left + wi * kw - kw * 0.32f;
            paint.Color = Lit.TryGetValue(n, out var col) ? col : Color.ParseColor("#22262C");
            canvas.DrawRect(x, top, x + kw * 0.64f, top + height * 0.6f, paint);
        }
    }
}

/// <summary>
/// 소리 고르기 상자들 (Windows Jukebox 위 막대와 같은 것): 거친 음색 · 그 시절 스피커 · 원래 소리 / 8-bit · MIDI 신디 · MIDI 모듈.
/// 가로 데크 막대와 설정 창이 같이 쓴다. '원래 소리' 상자와 MIDI 신디 상자는 같은 설정 (8-bit을 고르면 둘 다 그 풍).
/// </summary>
internal static class SoundControls
{
    /// <summary>parent에 상자들을 넣고, 설정에 맞추는 동작을 돌려준다.</summary>
    public static Action Add(Context c, ViewGroup parent)
    {
        float d = c.Resources.DisplayMetrics.Density;
        var labels = JukeboxSession.ChipStyles.Select(ChipMidiSynth.Label).ToList();
        var raw = new CheckBox(c) { Text = "거친 음색" };
        var vintage = new CheckBox(c) { Text = "그 시절 스피커" };
        var chip = NewSpinner(c, new[] { "원래 소리" }.Concat(labels).ToList());
        var synth = NewSpinner(c, new[] { "OPL FM (GM 뱅크)" }.Concat(labels).ToList());
        var module = NewSpinner(c, new List<string> { "General MIDI (GM)", "Roland MT-32" });

        var g1 = Group(c);
        g1.AddView(raw);
        g1.AddView(vintage);
        g1.AddView(chip);
        parent.AddView(g1);
        var g2 = Group(c);
        g2.AddView(Caption(c, "MIDI 신디", d));
        g2.AddView(synth);
        g2.AddView(Caption(c, "모듈", d));
        g2.AddView(module);
        parent.AddView(g2);

        Action sync = () =>
        {
            if (raw.Checked != JukeboxPrefs.RawTone) raw.Checked = JukeboxPrefs.RawTone;
            if (vintage.Checked != JukeboxPrefs.VintageSpeaker) vintage.Checked = JukeboxPrefs.VintageSpeaker;
            if (chip.SelectedItemPosition != JukeboxPrefs.Chip + 1) chip.SetSelection(JukeboxPrefs.Chip + 1);
            if (synth.SelectedItemPosition != JukeboxPrefs.Chip + 1) synth.SetSelection(JukeboxPrefs.Chip + 1);
            int m = JukeboxPrefs.Mt32 ? 1 : 0;
            if (module.SelectedItemPosition != m) module.SetSelection(m);
        };
        sync();
        // 설정과 같은 값이면 아무것도 안 함 (SetSelection이 뒤늦게 일으키는 이벤트도 걸러짐)
        raw.CheckedChange += (s, e) => { if (e.IsChecked != JukeboxPrefs.RawTone) JukeboxPrefs.SetRawTone(e.IsChecked); };
        vintage.CheckedChange += (s, e) => { if (e.IsChecked != JukeboxPrefs.VintageSpeaker) JukeboxPrefs.SetVintageSpeaker(e.IsChecked); };
        chip.ItemSelected += (s, e) => { if (e.Position - 1 != JukeboxPrefs.Chip) JukeboxPrefs.SetChip(e.Position - 1); };
        synth.ItemSelected += (s, e) => { if (e.Position - 1 != JukeboxPrefs.Chip) JukeboxPrefs.SetChip(e.Position - 1); };
        module.ItemSelected += (s, e) => { if ((e.Position == 1) != JukeboxPrefs.Mt32) JukeboxPrefs.SetMt32(e.Position == 1); };
        return sync;
    }

    private static LinearLayout Group(Context c)
    {
        var g = new LinearLayout(c) { Orientation = Orientation.Horizontal };
        g.SetGravity(GravityFlags.CenterVertical);
        return g;
    }

    private static TextView Caption(Context c, string text, float d)
    {
        var t = new TextView(c) { Text = text, TextSize = 14 };
        t.SetTextColor(WideDeck.Muted);
        t.SetPadding((int)(10 * d), 0, (int)(2 * d), 0);
        return t;
    }

    private static Spinner NewSpinner(Context c, List<string> items)
    {
        var sp = new Spinner(c);
        var ad = new ArrayAdapter<string>(c, Android.Resource.Layout.SimpleSpinnerItem, items);
        ad.SetDropDownViewResource(Android.Resource.Layout.SimpleSpinnerDropDownItem);
        sp.Adapter = ad;
        return sp;
    }
}
