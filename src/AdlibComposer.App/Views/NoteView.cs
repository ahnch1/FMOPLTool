using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Windows.Forms;
using AdlibComposer.App.Notes;
using AdlibComposer.App.Shell;
using AdlibComposer.Core;
using AdlibComposer.Notation;

namespace AdlibComposer.App.Views;

/// <summary>
/// Note 페이지: 현재 곡을 오선 악보로 보여 주고 내보낸다.
/// 보이스 고르기 · 양자화 · 확대, 음표 클릭 → Composer 같은 위치, MusicXML / MIDI / PNG 내보내기, 인쇄(미리보기).
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]   // 코드로 만든 화면: 디자이너 대신 코드로 연다
internal sealed class NoteView : UserControl, IShellPage
{
    private static readonly string[] QuantizeNames = { "양자화 끄기", "1/4 음표", "1/8 음표", "1/16 음표", "1/32 음표" };
    private static readonly double[] QuantizeBeats = { 0, 1, 0.5, 0.25, 0.125 };
    private static readonly float[] Zooms = { 0.6f, 0.75f, 0.9f, 1f, 1.15f, 1.3f, 1.5f, 1.75f, 2f };

    private readonly Workspace ws;
    private readonly Func<string> exportFolder;
    private readonly ToolStrip bar = new ToolStrip();
    private readonly ToolStripDropDownButton voicesButton = new ToolStripDropDownButton("보이스");
    private readonly ToolStripComboBox quantizeBox = new ToolStripComboBox();
    private readonly ToolStripLabel zoomLabel = new ToolStripLabel("100%");
    private readonly ToolStripLabel infoLabel = new ToolStripLabel();
    private readonly Panel scroller = new Panel();
    private readonly ScoreCanvas canvas = new ScoreCanvas();
    private readonly HashSet<int> hiddenVoices = new HashSet<int>();
    private int zoomIndex = 3;
    private Song lastSong;
    private AdlibComposer.Notation.Score score;

    /// <summary>음표를 클릭했다: (보이스, 틱, 음높이).</summary>
    public event Action<int, int, int> NoteActivated;

    public NoteView(Workspace ws, Func<string> exportFolder)
    {
        this.ws = ws;
        this.exportFolder = exportFolder;
        BackColor = Color.FromArgb(236, 238, 241);

        bar.GripStyle = ToolStripGripStyle.Hidden;
        bar.RenderMode = ToolStripRenderMode.System;
        bar.BackColor = Color.White;
        bar.Padding = new Padding(10, 4, 10, 4);
        bar.Font = ShellTheme.Ui;
        bar.Dock = DockStyle.Top;

        voicesButton.ToolTipText = "악보에 보일 보이스";
        voicesButton.Image = GlyphImages.Get(Glyphs.Composer, 16, Color.FromArgb(50, 54, 62));
        quantizeBox.DropDownStyle = ComboBoxStyle.DropDownList;
        quantizeBox.Items.AddRange(QuantizeNames);
        quantizeBox.SelectedIndex = 0;
        quantizeBox.AutoSize = false;
        quantizeBox.Width = 110;
        quantizeBox.ToolTipText = "음 시작·끝을 이 격자에 맞춰 악보를 깔끔하게";
        quantizeBox.SelectedIndexChanged += delegate { Rebuild(); };

        bar.Items.AddRange(new ToolStripItem[]
        {
            voicesButton,
            new ToolStripSeparator(),
            quantizeBox,
            new ToolStripSeparator(),
            new ToolStripButton("−", null, delegate { SetZoom(zoomIndex - 1); }) { ToolTipText = "축소 (Ctrl+휠)" },
            zoomLabel,
            new ToolStripButton("+", null, delegate { SetZoom(zoomIndex + 1); }) { ToolTipText = "확대 (Ctrl+휠)" },
            new ToolStripSeparator(),
            Button("MusicXML", Glyphs.Save, "MusicXML로 내보내기 (MuseScore 등에서 열기)", delegate { ExportMusicXml(); }),
            Button("MIDI", Glyphs.Save, "Standard MIDI 파일로 내보내기", delegate { ExportMidi(); }),
            Button("PNG", Glyphs.Save, "악보 그림(PNG)으로 저장", delegate { ExportPng(); }),
            Button("인쇄", Glyphs.Note, "인쇄 미리보기 / 인쇄", delegate { PrintPreview(); }),
            infoLabel,
        });
        infoLabel.Alignment = ToolStripItemAlignment.Right;
        infoLabel.ForeColor = ShellTheme.Muted;

        scroller.Dock = DockStyle.Fill;
        scroller.AutoScroll = true;
        scroller.BackColor = BackColor;
        scroller.Padding = new Padding(16);
        scroller.Controls.Add(canvas);
        scroller.Resize += delegate { FitCanvasWidth(); };
        scroller.MouseWheel += OnWheel;
        canvas.MouseWheel += OnWheel;
        canvas.NoteClicked += delegate (ScoreHit h) { if (NoteActivated != null) NoteActivated(h.Voice, h.Tick, h.Pitch); };
        canvas.NoteHovered += delegate (ScoreHit h) { infoLabel.Text = h != null ? h.Label : Hint; };

        Controls.Add(scroller);
        Controls.Add(bar);
    }

    private const string Hint = "음표를 클릭하면 Composer의 같은 위치로 이동";

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
        if (!ReferenceEquals(lastSong, ws.Song)) { hiddenVoices.Clear(); lastSong = ws.Song; }
        RebuildVoiceMenu();
        Rebuild();
        canvas.Focus();
    }

    public void OnHidden() { }

    // ---------------------------------------------------------------- 악보 만들기

    private ScoreOptions Options()
    {
        double beats = QuantizeBeats[Math.Max(0, quantizeBox.SelectedIndex)];
        return new ScoreOptions { QuantizeTicks = beats <= 0 ? 0 : Math.Max(1, (int)Math.Round(ws.Song.TickBeat * beats)) };
    }

    private void RebuildVoiceMenu()
    {
        voicesButton.DropDownItems.Clear();
        var song = ws.Song;
        string[] labels = Song.VoiceLabels(song.IsMelodic);
        var all = new ToolStripMenuItem("모두 보이기", null, delegate { hiddenVoices.Clear(); RebuildVoiceMenu(); Rebuild(); });
        voicesButton.DropDownItems.Add(all);
        voicesButton.DropDownItems.Add(new ToolStripSeparator());
        for (int v = 0; v < song.VoiceCount; v++)
        {
            if (song.Voices[v].Notes.Count == 0) continue;
            int voice = v;
            var t = song.Voices[v].TimbreAt(0);
            var drum = StaffRules.DrumFor(v, song.IsMelodic);
            string text = (drum != DrumKind.None ? labels[v] + "  " + StaffRules.DrumName(drum) : "Voice " + labels[v]) + (t != null ? "  (" + t.InstrumentName + ")" : "");
            var it = new ToolStripMenuItem(text) { Checked = !hiddenVoices.Contains(v), CheckOnClick = true };
            it.CheckedChanged += delegate
            {
                if (it.Checked) hiddenVoices.Remove(voice); else hiddenVoices.Add(voice);
                Rebuild();
            };
            voicesButton.DropDownItems.Add(it);
        }
    }

    /// <summary>현재 곡으로 악보를 다시 만든다 (보이스 선택 · 양자화 반영).</summary>
    public void Rebuild()
    {
        var full = ScoreBuilder.Build(ws.Song, Options());
        full.Voices.RemoveAll(v => hiddenVoices.Contains(v.VoiceIndex));
        full.Title = string.IsNullOrEmpty(ws.Song.FilePath) ? ws.Song.Title : Path.GetFileNameWithoutExtension(ws.Song.FilePath);
        score = full;
        var r = new ScoreRenderer(score) { Zoom = Zooms[zoomIndex], Title = score.Title };
        canvas.Renderer = r;
        FitCanvasWidth();
        int shown = score.Voices.Count;
        voicesButton.Text = "보이스 " + shown + (hiddenVoices.Count > 0 ? " / " + (shown + hiddenVoices.Count) : "");
        infoLabel.Text = Hint;
    }

    private void FitCanvasWidth()
    {
        int w = Math.Max(400, scroller.ClientSize.Width - scroller.Padding.Horizontal - (scroller.VerticalScroll.Visible ? 0 : SystemInformation.VerticalScrollBarWidth));
        canvas.Location = new Point(scroller.Padding.Left + scroller.AutoScrollPosition.X, scroller.Padding.Top + scroller.AutoScrollPosition.Y);
        if (canvas.Width != w) canvas.Width = w;
        else canvas.Relayout();
    }

    private void SetZoom(int index)
    {
        zoomIndex = Math.Max(0, Math.Min(Zooms.Length - 1, index));
        zoomLabel.Text = Math.Round(Zooms[zoomIndex] * 100) + "%";
        if (canvas.Renderer != null) { canvas.Renderer.Zoom = Zooms[zoomIndex]; canvas.Relayout(); }
    }

    private void OnWheel(object sender, MouseEventArgs e)
    {
        if ((ModifierKeys & Keys.Control) == 0) return;
        SetZoom(zoomIndex + (e.Delta > 0 ? 1 : -1));
        var h = e as HandledMouseEventArgs;
        if (h != null) h.Handled = true;
    }

    // ---------------------------------------------------------------- 내보내기

    private string AskSave(string filter, string ext, string title)
    {
        using (var dlg = new SaveFileDialog { Filter = filter, DefaultExt = ext, AddExtension = true, Title = title, FileName = score != null ? score.Title : "score" })
        {
            string dir = exportFolder != null ? exportFolder() : null;
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) dlg.InitialDirectory = dir;
            return dlg.ShowDialog(FindForm()) == DialogResult.OK ? dlg.FileName : null;
        }
    }

    private void ExportMusicXml()
    {
        if (score == null || score.Voices.Count == 0) { NothingToExport(); return; }
        string path = AskSave("MusicXML (*.musicxml)|*.musicxml|MusicXML (*.xml)|*.xml", "musicxml", "Export MusicXML");
        if (path == null) return;
        try
        {
            using (var fs = File.Create(path)) new MusicXmlExporter().Export(score, fs);
            AppLog.Info("악보", "MusicXML 내보내기: " + path + " (보이스 " + score.Voices.Count + ", 마디 " + MeasureCount() + ")");
        }
        catch (Exception ex) { Failed("MusicXML을 저장할 수 없습니다", ex); }
    }

    private void ExportMidi()
    {
        if (score == null || score.Voices.Count == 0) { NothingToExport(); return; }
        string path = AskSave("Standard MIDI file (*.mid)|*.mid", "mid", "Export MIDI");
        if (path == null) return;
        try
        {
            // 화면에 보이는 보이스만 (숨긴 보이스의 노트는 빼고)
            var song = ws.Song.Clone();
            foreach (int v in hiddenVoices) song.Voices[v].Notes.Clear();
            using (var fs = File.Create(path)) new MidiExporter().Export(song, fs);
            AppLog.Info("악보", "MIDI 내보내기: " + path + " (PPQ " + song.TickBeat + ")");
        }
        catch (Exception ex) { Failed("MIDI를 저장할 수 없습니다", ex); }
    }

    private void ExportPng()
    {
        if (canvas.Renderer == null || canvas.Renderer.IsEmpty) { NothingToExport(); return; }
        if (canvas.Height > 30000) { MessageBox.Show(FindForm(), "악보가 너무 길어 한 장의 그림으로 저장할 수 없습니다. 확대 비율을 줄이거나 인쇄(PDF 프린터)를 쓰세요.", "PNG"); return; }
        string path = AskSave("PNG image (*.png)|*.png", "png", "Export PNG");
        if (path == null) return;
        try
        {
            using (var bmp = new Bitmap(canvas.Width, canvas.Height, PixelFormat.Format24bppRgb))
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    float total;
                    var r = canvas.Renderer;
                    var systems = r.Layout(canvas.Width, out total);
                    r.DrawTitle(g, canvas.Width, 0);
                    int keepHover = r.HoverIndex;
                    r.HoverIndex = -1;
                    foreach (var s in systems) r.DrawSystem(g, s, 0, canvas.Width, false);
                    r.HoverIndex = keepHover;
                }
                bmp.Save(path, ImageFormat.Png);
            }
            AppLog.Info("악보", "PNG 저장: " + path + " (" + canvas.Width + "x" + canvas.Height + ")");
        }
        catch (Exception ex) { Failed("PNG를 저장할 수 없습니다", ex); }
    }

    private void PrintPreview()
    {
        if (score == null || score.Voices.Count == 0) { NothingToExport(); return; }
        var doc = new PrintDocument { DocumentName = score.Title ?? "score" };
        var r = new ScoreRenderer(score) { Zoom = 0.9f, Title = score.Title };
        List<ScoreSystem> systems = null;
        int next = 0, page = 0;
        doc.BeginPrint += delegate { systems = null; next = 0; page = 0; };
        doc.PrintPage += delegate (object o, PrintPageEventArgs e)
        {
            var bounds = e.MarginBounds;
            var g = e.Graphics;
            g.TranslateTransform(bounds.Left, 0);
            if (systems == null) { float total; systems = r.Layout(bounds.Width, out total); }
            float y = bounds.Top;
            if (page == 0)
            {
                r.DrawTitle(g, bounds.Width, bounds.Top);
                y = bounds.Top + (systems.Count > 0 ? systems[0].Y : 0);
            }
            while (next < systems.Count)
            {
                var s = systems[next];
                if (y + s.Height > bounds.Bottom && y > bounds.Top + 1 && !(page == 0 && next == 0)) break;
                r.DrawSystem(g, s, y - s.Y, bounds.Width, false);
                y += s.Height + 20f * r.Zoom;
                next++;
            }
            page++;
            using (var f = new Font("Segoe UI", 8f))
                g.DrawString(page.ToString(), f, Brushes.Gray, bounds.Width / 2f, bounds.Bottom + 10);
            e.HasMorePages = next < systems.Count;
        };
        using (var dlg = new PrintPreviewDialog { Document = doc, Width = 1000, Height = 800, UseAntiAlias = true, ShowIcon = false })
        {
            try { dlg.ShowDialog(FindForm()); AppLog.Info("악보", "인쇄 미리보기: " + score.Title); }
            catch (Exception ex) { Failed("인쇄 미리보기를 열 수 없습니다 (프린터가 없을 수 있습니다)", ex); }
        }
    }

    private int MeasureCount()
    {
        int n = 0;
        foreach (var v in score.Voices) n = Math.Max(n, v.Measures.Count);
        return n;
    }

    private void NothingToExport() { MessageBox.Show(FindForm(), "내보낼 노트가 없습니다.", "Note"); }

    private void Failed(string what, Exception ex)
    {
        AppLog.Error("악보", what, ex);
        MessageBox.Show(FindForm(), what + "\n\n" + ex.Message, "Note", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
