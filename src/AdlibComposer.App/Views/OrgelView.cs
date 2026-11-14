using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Windows.Forms;
using AdlibComposer.App.Orgel;
using AdlibComposer.App.Shell;
using AdlibComposer.Core;
using AdlibComposer.Notation;

namespace AdlibComposer.App.Views;

/// <summary>
/// Orgel Note 페이지: 현재 곡을 30음 오르골 종이 띠의 구멍 위치로 보여 준다.
/// 음의 시작점에만 구멍, 길이 · 쉼표 · 템포 변화는 무시 (손으로 돌리므로 간격만 맞춘다).
/// 조옮김(자동 맞춤), 빗살에 없는 음의 옥타브 옮기기, 격자(구멍 간격) · 격자에 맞추기, PNG, 실제 크기 인쇄.
/// 띠에 넣을 보이스 = 오른쪽 체크 목록(모두 켜기/끄기, 리듬 곡은 드럼도 넣기). 시험 연주(오르골 소리) = 툴바의 공통 재생 · 현재 위치부터 재생 · 정지.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class OrgelView : UserControl, IShellPage, IPlaybackPage
{
    private static readonly string[] GridNames = { "격자 1/4", "격자 1/8", "격자 1/16" };
    private static readonly double[] GridBeats = { 1, 0.5, 0.25 };
    private static readonly string[] BaseNames = { "빗살 C = C3", "빗살 C = C4", "빗살 C = C5" };
    private static readonly int[] BaseNotes = { 48, 60, 72 };
    private static readonly float[] Zooms = { 0.6f, 0.75f, 0.9f, 1f, 1.2f, 1.45f, 1.75f };

    /// <summary>실제 띠: 줄 간격 2mm, 격자 한 칸 8mm (30음 오르골 띠).</summary>
    private const float PrintRowPitchMm = 2f, PrintGridMm = 8f;

    private readonly Workspace ws;
    private readonly Func<bool> openAudio;
    private readonly Func<string> exportFolder;
    private readonly ToolStrip bar = new ToolStrip();
    private readonly VoicePanel voicePanel = new VoicePanel("보이스");
    private readonly ToolStripLabel transposeLabel = new ToolStripLabel();
    private readonly ToolStripButton foldButton = new ToolStripButton("옥타브 옮기기") { CheckOnClick = true, Checked = true };
    private readonly ToolStripComboBox gridBox = new ToolStripComboBox();
    private readonly ToolStripButton snapButton = new ToolStripButton("격자에 맞추기") { CheckOnClick = true, Checked = true };
    private readonly ToolStripComboBox baseBox = new ToolStripComboBox();
    private readonly ToolStripLabel zoomLabel = new ToolStripLabel("100%");
    private readonly ToolStripLabel speedLabel = new ToolStripLabel();
    private readonly OrgelCanvas canvas = new OrgelCanvas();
    private readonly Label summary = new Label();
    private readonly Timer timer = new Timer { Interval = 30 };

    private readonly HashSet<int> hiddenVoices = new HashSet<int>();
    private bool includeDrums;
    private int transpose;
    private bool transposeSet;
    private int zoomIndex = 3;
    private int bpm = 120;
    private Song lastSong;
    private OrgelStrip strip;
    private OrgelOptions options;
    private bool playing;

    public OrgelView(Workspace ws, Func<bool> openAudio, Func<string> exportFolder)
    {
        this.ws = ws;
        this.openAudio = openAudio;
        this.exportFolder = exportFolder;
        BackColor = Color.FromArgb(236, 238, 241);

        bar.GripStyle = ToolStripGripStyle.Hidden;
        bar.RenderMode = ToolStripRenderMode.System;
        bar.BackColor = Color.White;
        bar.Padding = new Padding(10, 4, 10, 4);
        bar.Font = ShellTheme.Ui;
        bar.Dock = DockStyle.Top;

        foldButton.ToolTipText = "빗살에 없는 음(범위 밖 · 낮은 옥타브의 반음)을 옥타브를 옮겨 넣는다 (파란 구멍)";
        foldButton.CheckedChanged += delegate { Rebuild(); };
        gridBox.DropDownStyle = ComboBoxStyle.DropDownList;
        gridBox.Items.AddRange(GridNames);
        gridBox.SelectedIndex = 1;
        gridBox.AutoSize = false;
        gridBox.Width = 86;
        gridBox.ToolTipText = "격자 한 칸 = 띠의 세로선 간격 (인쇄하면 8mm)";
        gridBox.SelectedIndexChanged += delegate { Rebuild(); };
        snapButton.ToolTipText = "구멍 위치를 가까운 격자선에 맞춘다";
        snapButton.CheckedChanged += delegate { Rebuild(); };
        baseBox.DropDownStyle = ComboBoxStyle.DropDownList;
        baseBox.Items.AddRange(BaseNames);
        baseBox.SelectedIndex = 1;
        baseBox.AutoSize = false;
        baseBox.Width = 104;
        baseBox.ToolTipText = "띠의 가장 낮은 줄 C가 어느 음인지 (곡의 음을 줄에 맞추는 기준, 시험 연주 음높이)";
        baseBox.SelectedIndexChanged += delegate { transposeSet = false; Rebuild(); };

        bar.Items.AddRange(new ToolStripItem[]
        {
            new ToolStripLabel("돌리는 빠르기"),
            new ToolStripButton("−", null, delegate { SetBpm(bpm - 10); }) { ToolTipText = "느리게 돌리기" },
            speedLabel,
            new ToolStripButton("+", null, delegate { SetBpm(bpm + 10); }) { ToolTipText = "빠르게 돌리기" },
            new ToolStripSeparator(),
            new ToolStripLabel("조옮김"),
            new ToolStripButton("−", null, delegate { SetTranspose(transpose - 1); }) { ToolTipText = "반음 내리기" },
            transposeLabel,
            new ToolStripButton("+", null, delegate { SetTranspose(transpose + 1); }) { ToolTipText = "반음 올리기" },
            new ToolStripButton("자동", null, delegate { AutoTranspose(); }) { ToolTipText = "옮기지 않고 들어가는 음이 가장 많은 조옮김" },
            foldButton,
            new ToolStripSeparator(),
            baseBox,
            gridBox,
            snapButton,
            new ToolStripSeparator(),
            new ToolStripButton("−", null, delegate { SetZoom(zoomIndex - 1); }) { ToolTipText = "축소 (Ctrl+휠)" },
            zoomLabel,
            new ToolStripButton("+", null, delegate { SetZoom(zoomIndex + 1); }) { ToolTipText = "확대 (Ctrl+휠)" },
            new ToolStripSeparator(),
            Button("PNG", Glyphs.Save, "띠 그림(PNG)으로 저장", delegate { ExportPng(); }),
            Button("인쇄", Glyphs.Note, "실제 크기(줄 간격 2mm, 격자 8mm)로 인쇄 · 미리보기", delegate { PrintPreview(); }),
        });

        canvas.Dock = DockStyle.Fill;
        canvas.CursorMoved += t => { if (playing) StartPlay(t); };
        canvas.ZoomRequested += d => SetZoom(zoomIndex + d);
        canvas.DescribePunch = DescribePunch;
        canvas.DescribeMissing = m => "못 내는 음: " + NoteHelper.Name(m.SourceNote + transpose) + " (원래 " + NoteHelper.Name(m.SourceNote) + ", 보이스 " + (m.Voice + 1) + ")  " + Where(m.Tick);

        summary.Dock = DockStyle.Bottom;
        summary.Height = 30;
        summary.Font = ShellTheme.Ui;
        summary.ForeColor = Color.FromArgb(60, 64, 72);
        summary.BackColor = Color.White;
        summary.Padding = new Padding(12, 0, 0, 0);
        summary.TextAlign = ContentAlignment.MiddleLeft;

        voicePanel.VoiceToggled += (v, on) => { if (on) hiddenVoices.Remove(v); else hiddenVoices.Add(v); Rebuild(); };
        voicePanel.AllToggled += on => { hiddenVoices.Clear(); if (!on) hiddenVoices.UnionWith(voicePanel.Voices); RebuildVoiceMenu(); Rebuild(); };
        voicePanel.Extra.Text = "드럼(BD SD TO CY HH)도 넣기";
        voicePanel.Extra.CheckedChanged += delegate
        {
            if (includeDrums == voicePanel.Extra.Checked) return;
            includeDrums = voicePanel.Extra.Checked;
            RebuildVoiceMenu();
            Rebuild();
        };

        Controls.Add(canvas);
        Controls.Add(voicePanel);
        Controls.Add(summary);
        Controls.Add(bar);

        timer.Tick += delegate { OnTimer(); };
        UpdateLabels();
    }

    private static ToolStripButton Button(string text, char glyph, string tip, EventHandler click)
    {
        return new ToolStripButton(text, GlyphImages.Get(glyph, 16, Color.FromArgb(50, 54, 62)), click)
        {
            DisplayStyle = ToolStripItemDisplayStyle.ImageAndText, ToolTipText = tip, Padding = new Padding(3, 1, 3, 1)
        };
    }

    // ---------------------------------------------------------------- IShellPage

    public void OnShown()
    {
        if (!ReferenceEquals(lastSong, ws.Song))
        {
            hiddenVoices.Clear();
            lastSong = ws.Song;
            transposeSet = false;
            bpm = (int)Math.Round(Math.Max(20, Math.Min(400, ws.Song.BasicTempo)));
            canvas.SetCursor(0);
        }
        RebuildVoiceMenu();
        Rebuild();
        canvas.Focus();
    }

    public void OnHidden() { StopPlay(); }

    // ---------------------------------------------------------------- 띠 만들기

    private int GridTicks { get { return Math.Max(1, (int)Math.Round(ws.Song.TickBeat * GridBeats[Math.Max(0, gridBox.SelectedIndex)])); } }
    private int LowestC { get { return BaseNotes[Math.Max(0, baseBox.SelectedIndex)]; } }

    private OrgelOptions MakeOptions()
    {
        var o = new OrgelOptions
        {
            LowestC = LowestC, Transpose = transpose, FoldOctaves = foldButton.Checked, IncludeDrums = includeDrums,
            QuantizeTicks = snapButton.Checked ? GridTicks : 0, MinGapTicks = GridTicks,
        };
        foreach (int v in hiddenVoices) o.HiddenVoices.Add(v);
        return o;
    }

    /// <summary>현재 곡으로 띠를 다시 만든다. 처음엔 조옮김을 자동으로 맞춘다.</summary>
    public void Rebuild()
    {
        if (!transposeSet)
        {
            transpose = OrgelBuilder.BestTranspose(ws.Song, MakeOptions());
            transposeSet = true;
        }
        options = MakeOptions();
        strip = OrgelBuilder.Build(ws.Song, options);
        canvas.Renderer.GridTicks = GridTicks;
        ApplyZoom();
        canvas.Strip = strip;
        UpdateLabels();
        if (playing) StartPlay(ws.Audio.PositionTick);
    }

    /// <summary>오른쪽 보이스 목록을 곡에 맞춰 다시 채운다 (노트가 있는 보이스, 드럼은 '드럼도 넣기'일 때만).</summary>
    private void RebuildVoiceMenu()
    {
        var song = ws.Song;
        string[] labels = song.Labels;
        var items = new List<(int, string, bool)>();
        for (int v = 0; v < song.VoiceCount; v++)
        {
            if (song.Voices[v].Notes.Count == 0) continue;
            bool drum = song.IsDrumVoice(v);
            if (drum && !includeDrums) continue;
            var t = song.Voices[v].TimbreAt(0);
            string text = (drum ? "드럼 " : "Voice ") + labels[v] + (t != null ? "  (" + t.InstrumentName + ")" : "") + "  " + song.Voices[v].Notes.Count + "음";
            items.Add((v, text, !hiddenVoices.Contains(v)));
        }
        voicePanel.SetVoices(items);
        voicePanel.Extra.Visible = !song.IsMelodic;
        voicePanel.Extra.Checked = includeDrums;
    }

    private void SetTranspose(int t)
    {
        transpose = Math.Max(-48, Math.Min(48, t));
        transposeSet = true;
        Rebuild();
    }

    private void AutoTranspose()
    {
        transposeSet = false;
        Rebuild();
        AppLog.Info("오르골", "자동 조옮김: " + Signed(transpose) + " 반음");
    }

    private void SetZoom(int index)
    {
        zoomIndex = Math.Max(0, Math.Min(Zooms.Length - 1, index));
        ApplyZoom();
        UpdateLabels();
    }

    /// <summary>줄 간격은 화면 높이에 맞추고, 가로는 실제 띠처럼 격자 한 칸 = 줄 간격 4배(8mm : 2mm) × 확대.</summary>
    private void ApplyZoom()
    {
        canvas.Zoom = Zooms[zoomIndex];
        canvas.GridRatio = PrintGridMm / PrintRowPitchMm;
        canvas.ApplySize();
    }

    private void UpdateLabels()
    {
        transposeLabel.Text = Signed(transpose);
        zoomLabel.Text = Math.Round(Zooms[zoomIndex] * 100) + "%";
        speedLabel.Text = "♩ " + bpm;
        voicePanel.UpdateTitle();
        if (strip == null) { summary.Text = ""; return; }
        int measures = strip.LengthTicks / Math.Max(1, strip.TickBeat * strip.BeatsPerMeasure) + 1;
        double gridCount = Math.Ceiling(strip.LengthTicks / (double)GridTicks);
        summary.Text = "구멍 " + strip.Punches.Count + "개"
            + (strip.FoldedCount > 0 ? "   ·   옥타브 옮김 " + strip.FoldedCount + " (파란 구멍)" : "")
            + (strip.Missing.Count > 0 ? "   ·   못 내는 음 " + strip.Missing.Count + " (띠 아래 빨간 ×)" : "")
            + (strip.TooCloseCount > 0 ? "   ·   같은 줄 너무 가까움 " + strip.TooCloseCount + " (주황 고리, 한 칸 미만)" : "")
            + "   ·   " + measures + "마디, 띠 길이 약 " + Math.Round(gridCount * PrintGridMm / 10.0) + "cm";
        summary.ForeColor = strip.Missing.Count > 0 ? Color.FromArgb(180, 30, 30) : Color.FromArgb(60, 64, 72);
    }

    private static string Signed(int v) { return v > 0 ? "+" + v : v.ToString(); }

    private string Where(int tick)
    {
        var s = ws.Song;
        int m = tick / Math.Max(1, s.MeasureTicks) + 1;
        double b = (tick % Math.Max(1, s.MeasureTicks)) / (double)Math.Max(1, s.TickBeat) + 1;
        return m + "마디 " + Math.Round(b, 2) + "박";
    }

    private string DescribePunch(OrgelPunch p)
    {
        int playNote = OrgelComb.NoteOf(p.Row, options.LowestC);
        return OrgelComb.Label(p.Row).Replace("#", "♯") + " 줄  (" + NoteHelper.Name(playNote) + ")   " + Where(p.Tick)
            + "\n원래 음 " + NoteHelper.Name(p.SourceNote) + " · 보이스 " + (p.Voice + 1)
            + (p.Folded ? "\n빗살에 없어 옥타브를 옮김" : "")
            + (p.TooClose ? "\n같은 줄 앞 구멍과 한 칸이 안 됨 (빗살이 다시 울릴 시간이 모자랄 수 있음)" : "");
    }

    // ---------------------------------------------------------------- 시험 연주

    private void SetBpm(int v)
    {
        bpm = Math.Max(20, Math.Min(400, v));
        if (playing) ws.Audio.SetMusicBoxSpeed(TicksPerSecond);
        UpdateLabels();
    }

    private double TicksPerSecond { get { return bpm / 60.0 * Math.Max(1, ws.Song.TickBeat); } }

    private void TogglePlay()
    {
        if (playing) StopPlay(); else StartPlay(canvas.CursorTick);
    }

    // IPlaybackPage: 툴바의 공통 재생 버튼
    public bool CanPlay { get { return strip != null && strip.Punches.Count > 0; } }
    public bool IsPlaying { get { return playing; } }
    public void PlayFromStart() { canvas.SetCursor(0); StartPlay(0); }
    public void PlayFromCursor() { StartPlay(canvas.CursorTick); }

    private void StartPlay(double from)
    {
        if (strip == null || strip.Punches.Count == 0 || !openAudio()) return;
        var hits = new List<KeyValuePair<int, int>>();
        foreach (var p in strip.Punches) hits.Add(new KeyValuePair<int, int>(p.Tick, OrgelComb.NoteOf(p.Row, options.LowestC)));
        ws.Audio.PlayMusicBox(hits, TicksPerSecond, (int)Math.Max(0, from), strip.LengthTicks);
        playing = true;
        timer.Start();
    }

    /// <summary>멈추고, 현재 위치를 멈춘 곳으로 (현재 위치부터 재생 = 이어 듣기).</summary>
    public void StopPlay()
    {
        if (!playing) return;
        if (ws.Audio.IsPlaying) canvas.SetCursor(ws.Audio.PositionTick);
        ws.Audio.Stop();
        playing = false;
        timer.Stop();
        canvas.PlayTick = -1;
        canvas.Invalidate();
    }

    private void OnTimer()
    {
        if (!playing) return;
        if (!ws.Audio.IsPlaying) { StopPlay(); canvas.SetCursor(0); return; }
        double t = ws.Audio.PositionTick;
        canvas.PlayTick = t;
        canvas.Follow(t);
        canvas.Invalidate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Space: TogglePlay(); return true;
            case Keys.Escape: StopPlay(); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    // ---------------------------------------------------------------- 내보내기

    private string Title
    {
        get { return string.IsNullOrEmpty(ws.Song.FilePath) ? ws.Song.Title : Path.GetFileNameWithoutExtension(ws.Song.FilePath); }
    }

    private string AskSave(string filter, string ext, string title)
    {
        using (var dlg = new SaveFileDialog { Filter = filter, DefaultExt = ext, AddExtension = true, Title = title, FileName = Title + "_orgel" })
        {
            string dir = exportFolder != null ? exportFolder() : null;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            return dlg.ShowDialog(FindForm()) == DialogResult.OK ? dlg.FileName : null;
        }
    }

    private void ExportPng()
    {
        if (strip == null || strip.Punches.Count == 0) { Nothing(); return; }
        var r = new OrgelRenderer { RowPitch = 14f, GridWidth = 56f, GridTicks = GridTicks, LabelWidth = 64f };
        int ticks = strip.LengthTicks + GridTicks * 2;
        int w = (int)Math.Ceiling(16 + r.LabelWidth + ticks * r.UnitsPerTick + 16);
        int h = (int)Math.Ceiling(r.PaperHeight + r.RowPitch * 6);
        if (w > 60000) { MessageBox.Show(FindForm(), "띠가 너무 길어 한 장의 그림으로 저장할 수 없습니다. 인쇄(PDF 프린터)를 쓰세요.", "Orgel Note"); return; }
        string path = AskSave("PNG image (*.png)|*.png", "png", "Export PNG");
        if (path == null) return;
        try
        {
            using (var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    float top = r.RowPitch * 2.5f;
                    r.DrawStrip(g, strip, 16 + r.LabelWidth, top, -GridTicks, ticks - GridTicks);
                    r.DrawLabels(g, 16, top);
                }
                bmp.Save(path, ImageFormat.Png);
            }
            AppLog.Info("오르골", "PNG 저장: " + path + " (" + w + "x" + h + ", 구멍 " + strip.Punches.Count + ")");
        }
        catch (Exception ex) { Failed("PNG를 저장할 수 없습니다", ex); }
    }

    /// <summary>실제 크기 인쇄: 줄 간격 2mm · 격자 8mm로, 가로(A4 등) 용지에 띠를 여러 줄로 잘라 싣는다. 이어 붙일 곳에 구간 번호.</summary>
    private void PrintPreview()
    {
        if (strip == null || strip.Punches.Count == 0) { Nothing(); return; }
        var doc = BuildPrintDocument();
        using (var dlg = new PrintPreviewDialog { Document = doc, Width = 1100, Height = 800, UseAntiAlias = true, ShowIcon = false })
        {
            try { dlg.ShowDialog(FindForm()); AppLog.Info("오르골", "인쇄 미리보기: " + Title); }
            catch (Exception ex) { Failed("인쇄 미리보기를 열 수 없습니다 (프린터가 없을 수 있습니다)", ex); }
        }
    }

    private PrintDocument BuildPrintDocument()
    {
        var doc = new PrintDocument { DocumentName = Title + " (orgel)" };
        doc.DefaultPageSettings.Landscape = true;
        var r = new OrgelRenderer { RowPitch = PrintRowPitchMm, GridWidth = PrintGridMm, GridTicks = GridTicks, LabelWidth = 13f, LineScale = 0.25f };
        int total = strip.LengthTicks + GridTicks;
        int nextTick = -GridTicks, segment = 0, page = 0;   // 앞에 빈 칸 한 칸
        doc.BeginPrint += delegate { nextTick = -GridTicks; segment = 0; page = 0; };
        doc.PrintPage += delegate (object o, PrintPageEventArgs e)
        {
            var g = e.Graphics;
            g.PageUnit = GraphicsUnit.Millimeter;
            // 여백 (1/100인치 → mm)
            float left = e.MarginBounds.Left * 0.254f, top = e.MarginBounds.Top * 0.254f;
            float width = e.MarginBounds.Width * 0.254f, height = e.MarginBounds.Height * 0.254f;
            float segH = r.PaperHeight + 12f;
            float stripW = width - r.LabelWidth - 2f;
            int ticksPerSeg = Math.Max(GridTicks, (int)(stripW / r.UnitsPerTick) / GridTicks * GridTicks);
            using (var f = new Font("Arial", 3f, FontStyle.Bold, GraphicsUnit.World))
            {
                if (page == 0) g.DrawString(Title + "  —  30음 오르골 띠 (줄 간격 2mm, 격자 8mm = " + GridNames[gridBox.SelectedIndex].Replace("격자 ", "") + " 음표)", f, Brushes.Black, left, top - 1f);
                float y = top + 6f;
                while (nextTick < total && y + segH <= top + height + 0.1f)
                {
                    int to = Math.Min(total, nextTick + ticksPerSeg);
                    segment++;
                    r.DrawLabels(g, left, y);
                    r.DrawStrip(g, strip, left + r.LabelWidth, y, nextTick, to);
                    using (var sf = new Font("Arial", 2.4f, GraphicsUnit.World))
                        g.DrawString("구간 " + segment + (to < total ? "  →  구간 " + (segment + 1) + "에 이어 붙이기" : "  (끝)"), sf, Brushes.DimGray, left + r.LabelWidth, y + r.PaperHeight + 2.2f);
                    nextTick = to;
                    y += segH;
                }
            }
            page++;
            e.HasMorePages = nextTick < total;
        };
        return doc;
    }

    private void Nothing() { MessageBox.Show(FindForm(), "구멍을 낼 노트가 없습니다.", "Orgel Note"); }

    private void Failed(string what, Exception ex)
    {
        AppLog.Error("오르골", what, ex);
        MessageBox.Show(FindForm(), what + "\n\n" + ex.Message, "Orgel Note", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }
}
