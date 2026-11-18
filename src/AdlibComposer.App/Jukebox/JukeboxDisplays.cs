using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using AdlibComposer.Formats;

namespace AdlibComposer.App.Jukebox;

/// <summary>
/// Jukebox 표시 방식 (곡 정보 상자의 단추로 고른다):
/// Implay = 가사 + 채널 믹서, Ocplay = 가사 + 작은 악보(피아노롤) · 세로 음량 막대, Karaoke = 곡 폴더의 그림 + 그 위에 가사.
/// </summary>
internal enum JukeboxViewMode { Implay, Ocplay, Karaoke }

/// <summary>
/// OCPlay 풍 표시: 검은 상자 위쪽 = Composer 악보처럼 채널 색 막대가 흐르는 작은 피아노롤 (오른쪽 끝 = 지금, 최근 몇 초),
/// 아래쪽 = 채널마다 세로 음량 막대 (회색 칸, 켜진 칸은 채널 색, 맨 위 켜진 칸은 빨강).
/// 곡 형식과 상관없이 엔진의 채널 상태(울리는 음 · 음량)를 받아 그린다 (ROL · MIDI · 트래커 · VGM 모두).
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class OcplayView : DeckControl
{
    private const int History = 360;        // 칸 수 (≈ 12초, 30번/초)
    private const int MeterH = 78, Segments = 10;
    private static readonly Color Screen = Color.Black;
    private static readonly Color Grid = Color.FromArgb(26, 30, 34);
    private static readonly Color Octave = Color.FromArgb(44, 50, 58);
    private static readonly Color CellOff = Color.FromArgb(150, 154, 160);
    private static readonly Color Peak = Color.FromArgb(226, 52, 44);

    /// <summary>한 칸 = 그 순간 채널마다 울리는 음 (−1 = 없음).</summary>
    private readonly List<int[]> notes = new List<int[]>();
    private int[] lastNotes = new int[0];
    private List<ChannelRow> rows = new List<ChannelRow>();

    /// <summary>곡이 바뀌면 흐름을 비운다.</summary>
    public void Clear() { notes.Clear(); Invalidate(); }

    /// <summary>믹서 줄 (음 · 음량 · 색 · 끔)을 한 칸으로 받는다. 재생 중이 아니면 흐름은 멈추고 막대만 다시 그린다.</summary>
    public void Sample(List<ChannelRow> source, bool playing)
    {
        rows = source;
        if (playing)
        {
            var col = new int[source.Count];
            for (int i = 0; i < col.Length; i++) col[i] = source[i].Muted ? -1 : source[i].Note;
            notes.Add(col);
            if (notes.Count > History) notes.RemoveRange(0, notes.Count - History);
            lastNotes = col;
        }
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        DeckTheme.Bevel(g, new Rectangle(0, 0, Width, Height), true, DeckTheme.Panel, 2);
        // 낮으면 막대 = 높이의 1/3 (24px 이상), 나머지 = 악보
        int meterH = Math.Min(MeterH, Math.Max(24, (Height - 16) / 3));
        var roll = new Rectangle(8, 8, Width - 16, Height - 16 - meterH - 4);
        var meters = new Rectangle(8, roll.Bottom + 4, Width - 16, meterH);
        if (roll.Height > 12) DrawRoll(g, roll);
        DrawMeters(g, meters);
    }

    private void DrawRoll(Graphics g, Rectangle r)
    {
        DeckTheme.Bevel(g, r, false, Screen, 2);
        var inner = Rectangle.Inflate(r, -3, -3);
        // 음 범위: 흐름에 나온 음 (위아래 여유), 적어도 두 옥타브
        int lo = 127, hi = 0;
        foreach (var col in notes) foreach (int n in col) if (n >= 0) { lo = Math.Min(lo, n); hi = Math.Max(hi, n); }
        if (lo > hi) { lo = 48; hi = 72; }
        lo -= 2; hi += 2;
        if (hi - lo < 24) { int mid = (lo + hi) / 2; lo = mid - 12; hi = mid + 12; }
        double rowH = inner.Height / (double)(hi - lo + 1);
        using (var gp = new Pen(Grid))
        using (var op = new Pen(Octave))
            for (int n = lo; n <= hi; n++)
            {
                int y = inner.Bottom - (int)((n - lo + 1) * rowH);
                if (n % 12 == 0) g.DrawLine(op, inner.Left, y, inner.Right, y);
                else if (rowH >= 4 && n % 12 == 5) g.DrawLine(gp, inner.Left, y, inner.Right, y);
            }
        if (notes.Count == 0) return;
        double colW = inner.Width / (double)History;
        int start = History - notes.Count;   // 오른쪽 끝 = 지금
        int barH = Math.Max(2, Math.Min(5, (int)rowH - 1));
        var old = g.Clip;
        g.SetClip(inner);
        for (int ch = 0; ch < rows.Count; ch++)
        {
            Color color = rows[ch].Color;
            using (var b = new SolidBrush(color))
            {
                int runStart = -1, runNote = -1;
                for (int k = 0; k <= notes.Count; k++)
                {
                    int n = k < notes.Count && ch < notes[k].Length ? notes[k][ch] : -1;
                    if (n == runNote && k < notes.Count) continue;
                    if (runNote >= 0)
                    {
                        int x0 = inner.Left + (int)((start + runStart) * colW), x1 = inner.Left + (int)((start + k) * colW);
                        int y = inner.Bottom - (int)((runNote - lo + 1) * rowH) + Math.Max(0, ((int)rowH - barH) / 2);
                        g.FillRectangle(b, x0, y, Math.Max(2, x1 - x0 - 1), barH);
                    }
                    runStart = k; runNote = n;
                }
            }
        }
        g.Clip = old;
    }

    private void DrawMeters(Graphics g, Rectangle r)
    {
        DeckTheme.Bevel(g, r, false, DeckTheme.Panel, 1);
        int n = Math.Max(1, rows.Count);
        var inner = Rectangle.Inflate(r, -4, -4);
        int colW = Math.Max(4, Math.Min(44, inner.Width / Math.Max(n, 11)));   // OCPlay처럼 좁은 기둥
        int segments = Math.Max(4, Math.Min(Segments, (inner.Height + 1) / 4));   // 낮으면 칸 수를 줄인다
        int segH = Math.Max(2, (inner.Height - (segments - 1)) / segments);
        for (int c = 0; c < rows.Count; c++)
        {
            var row = rows[c];
            int x = inner.Left + c * colW;
            if (x + colW > inner.Right + 1) break;
            int on = row.Muted ? 0 : (int)Math.Round(Math.Max(0, Math.Min(1, row.Level)) * segments);
            for (int s = 0; s < segments; s++)
            {
                int y = inner.Bottom - (s + 1) * segH - s;
                Color col = s < on ? (s == on - 1 ? Peak : row.Color) : CellOff;
                var cell = new Rectangle(x + 1, y, colW - 3, segH);
                using (var b = new SolidBrush(col)) g.FillRectangle(b, cell);
                if (segH >= 6) using (var p = new Pen(Color.FromArgb(110, 114, 120))) g.DrawRectangle(p, cell.X, cell.Y, cell.Width - 1, cell.Height - 1);
            }
        }
    }
}

/// <summary>
/// 노래방 표시: 곡이 있는 폴더(zip 안 포함)의 그림(JPG · PNG · BMP · GIF)을 칸에 꽉 차게 확대해 무작위로 보이고 (몇 초마다 바꿈),
/// 아래쪽 반투명 띠에 가사를 노래방처럼 겹쳐 그린다 (부른 글자 = 노랑). 가사가 없으면 곡 제목.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class KaraokeView : DeckControl
{
    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };
    private const int MaxImageSide = 2048;
    private readonly Timer slide = new Timer { Interval = 10000 };
    private readonly Random rnd = new Random();
    private string folder;
    private List<string> images = new List<string>();
    private int current = -1;
    private Bitmap image;      // 읽은 그림 (줄인 것)
    private Bitmap scaled;     // 지금 크기에 맞춰 자른 것
    private IssLyrics lyrics;
    private LyricState state;
    private Font bigFont, nextFont;
    private int fontFor = -1;

    public string Title = "";
    /// <summary>곡 폴더에 그림이 없을 때 쓸 폴더 (설정 KaraokeFolder, null = 없음).</summary>
    public Func<string> FallbackFolder;
    /// <summary>그림이 없을 때 칸을 두 번 클릭: 그림 폴더를 고르게 한다 (Jukebox가 설정에 남기고 다시 부른다).</summary>
    public event Action ChooseFolderRequested;
    /// <summary>지금 그림을 가져온 폴더 (곡 폴더 또는 설정 폴더, 없으면 null).</summary>
    public string ImageFolder { get; private set; }
    public bool HasImages { get { return images.Count > 0; } }

    public KaraokeView()
    {
        slide.Tick += delegate { NextImage(); };
        DoubleClick += delegate { if (images.Count == 0) ChooseFolderRequested?.Invoke(); };
        VisibleChanged += delegate { if (Visible) slide.Start(); else slide.Stop(); };
    }

    public IssLyrics Lyrics
    {
        get { return lyrics; }
        set { if (!ReferenceEquals(lyrics, value)) { lyrics = value; Invalidate(); } }
    }

    public LyricState State
    {
        get { return state; }
        set { if (!ReferenceEquals(state, value)) { state = value; Invalidate(); } }
    }

    /// <summary>곡 파일이 바뀌면: 그 폴더의 그림 목록 (폴더가 같으면 다시 읽지 않는다)과 새 그림 하나.</summary>
    public void SetSong(string songPath)
    {
        string dir = null;
        try { dir = string.IsNullOrEmpty(songPath) ? null : Path.GetDirectoryName(songPath); } catch (Exception) { }
        string fallback = FallbackFolder != null ? FallbackFolder() : null;
        string key = dir + "|" + fallback;
        if (!string.Equals(key, folder, StringComparison.OrdinalIgnoreCase))
        {
            folder = key;
            // 곡 폴더의 그림, 없으면 설정의 그림 폴더
            images = ImagesIn(dir);
            ImageFolder = images.Count > 0 ? dir : null;
            if (images.Count == 0 && fallback != null) { images = ImagesIn(fallback); ImageFolder = images.Count > 0 ? fallback : null; }
            current = -1;
        }
        NextImage();
    }

    /// <summary>설정의 그림 폴더가 바뀐 뒤: 목록을 다시 읽는다.</summary>
    public void Reload(string songPath) { folder = null; SetSong(songPath); }

    private static List<string> ImagesIn(string dir)
    {
        try
        {
            if (dir != null)
                return ZipPath.GetFiles(dir).Where(f => ImageExt.Any(x => string.Equals(Path.GetExtension(f), x, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { }
        return new List<string>();
    }

    /// <summary>다른 그림으로 (하나뿐이면 그대로).</summary>
    public void NextImage()
    {
        if (images.Count == 0) { SetImage(null); return; }
        int next = images.Count == 1 ? 0 : rnd.Next(images.Count - 1);
        if (images.Count > 1 && next >= current && current >= 0) next++;
        if (next == current && image != null) return;
        current = next;
        Bitmap bmp = null;
        try
        {
            using (var ms = new MemoryStream(ZipPath.ReadAllBytes(images[current])))
            using (var img = Image.FromStream(ms))
            {
                double k = Math.Min(1.0, MaxImageSide / (double)Math.Max(img.Width, img.Height));
                bmp = new Bitmap(img, Math.Max(1, (int)(img.Width * k)), Math.Max(1, (int)(img.Height * k)));
            }
        }
        catch (Exception ex) { Shell.AppLog.Warn("Jukebox", "그림을 읽을 수 없습니다: " + Path.GetFileName(images[current]), ex.Message); }
        SetImage(bmp);
    }

    private void SetImage(Bitmap bmp)
    {
        image?.Dispose();
        image = bmp;
        scaled?.Dispose();
        scaled = null;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        scaled?.Dispose();
        scaled = null;
    }

    /// <summary>칸에 꽉 차게 (가운데 기준으로 넘치는 쪽을 잘라) 확대한 그림.</summary>
    private Bitmap Scaled(Rectangle r)
    {
        if (image == null || r.Width <= 0 || r.Height <= 0) return null;
        if (scaled != null && scaled.Width == r.Width && scaled.Height == r.Height) return scaled;
        scaled?.Dispose();
        scaled = new Bitmap(r.Width, r.Height);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            double k = Math.Max(r.Width / (double)image.Width, r.Height / (double)image.Height);
            int w = (int)Math.Ceiling(image.Width * k), h = (int)Math.Ceiling(image.Height * k);
            g.DrawImage(image, (r.Width - w) / 2, (r.Height - h) / 2, w, h);
        }
        return scaled;
    }

    private void EnsureFonts(int bandH)
    {
        int size = Math.Max(14, Math.Min(40, bandH * 30 / 100));
        if (size == fontFor) return;
        bigFont?.Dispose(); nextFont?.Dispose();
        bigFont = new Font("Malgun Gothic", size, FontStyle.Bold, GraphicsUnit.Pixel);
        nextFont = new Font("Malgun Gothic", Math.Max(11, size * 62 / 100), FontStyle.Bold, GraphicsUnit.Pixel);
        fontFor = size;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        DeckTheme.Bevel(g, new Rectangle(0, 0, Width, Height), true, DeckTheme.Panel, 2);
        var r = new Rectangle(8, 8, Width - 16, Height - 16);
        if (r.Width <= 0 || r.Height <= 0) return;
        var pic = Scaled(r);
        if (pic != null) g.DrawImageUnscaled(pic, r.Location);
        else
        {
            using (var b = new LinearGradientBrush(r, Color.FromArgb(24, 40, 52), Color.FromArgb(10, 96, 92), 60f)) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, "그림이 없습니다 (JPG · PNG · BMP · GIF) — 두 번 클릭해서 그림 폴더 고르기", DeckTheme.Info, new Rectangle(r.X, r.Y + 10, r.Width, 24), Color.FromArgb(170, 200, 200), TextFormatFlags.HorizontalCenter);
        }

        // 아래쪽 가사 띠 (반투명)
        int bandH = Math.Max(56, Math.Min(r.Height * 2 / 5, 170));
        var band = new Rectangle(r.X, r.Bottom - bandH, r.Width, bandH);
        using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(b, band);
        EnsureFonts(bandH);
        var white = Color.FromArgb(250, 250, 250);
        var sung = Color.FromArgb(255, 214, 64);
        var shadow = Color.FromArgb(20, 20, 20);
        if (lyrics != null && lyrics.Lines.Count > 0 && state != null && state.Line >= 0)
        {
            LyricsPainter.DrawLine(g, lyrics.Lines[state.Line], state.Lit, bigFont, band.Y + bandH * 0.38f, band.X + 16, band.Width - 32, white, sung, shadow);
            if (state.NextLine >= 0 && state.NextLine < lyrics.Lines.Count)
                LyricsPainter.DrawLine(g, lyrics.Lines[state.NextLine], null, nextFont, band.Y + bandH * 0.78f, band.X + 16, band.Width - 32, Color.FromArgb(200, 210, 214), sung, shadow);
        }
        else
        {
            string t = lyrics == null ? (Title.Length > 0 ? Title : "♪") : "♪";
            var tr = new Rectangle(band.X + 16, band.Y, band.Width - 32, bandH);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, t, nextFont, new Rectangle(tr.X + 2, tr.Y + 2, tr.Width, tr.Height), shadow, flags);
            TextRenderer.DrawText(g, t, nextFont, tr, white, flags);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { slide.Dispose(); image?.Dispose(); scaled?.Dispose(); bigFont?.Dispose(); nextFont?.Dispose(); }
        base.Dispose(disposing);
    }
}
