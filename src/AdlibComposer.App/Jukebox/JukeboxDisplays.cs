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
/// Implay = 가사 + 채널 믹서, Ocplay = 가사 + 작은 악보(피아노롤) · 세로 음량 막대, Karaoke = 곡 폴더의 그림 + 그 위에 가사,
/// Cyber = 노래방(Cyberfunk): 사이버펑크 화면 보호기 (별 · 워프 · 튕기는 도형) + 가사.
/// </summary>
internal enum JukeboxViewMode { Implay, Ocplay, Karaoke, Cyber }

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

/// <summary>
/// 노래방(Cyberfunk): _incoming/cyberpunk_pixel_screensaver.html 화면 + 가사.
/// 반투명 검정으로 덮어 잔상을 남기는 화면에 [좌우로 흐르는 픽셀 별] [가운데서 퍼지는 워프 별 (Starfield Simulation)]
/// [잔상을 끌며 튕기는 삼각형 · 사각형 (Mystify Your Mind)]을 그리고, 오른쪽 위 구석 체크박스로 하나씩 켜고 끈다.
/// 아래쪽에 네온 가사 띠 (부른 글자 = 분홍), 왼쪽 위 HUD 글자.
/// </summary>
[System.ComponentModel.DesignerCategory("Code")]
internal sealed class CyberView : DeckControl
{
    private static readonly Color Pink = Color.FromArgb(255, 0, 127), Cyan = Color.FromArgb(0, 255, 255), Yellow = Color.FromArgb(255, 234, 0), Bg = Color.FromArgb(5, 5, 10);
    /// <summary>켜고 끄는 층: 0 좌우 별, 1 우주 유영, 2 삼각형, 3 사각형.</summary>
    public static readonly string[] LayerNames = { "좌우로 흐르는 별", "우주 유영 (Starfield)", "튕기는 삼각형 (Mystify)", "튕기는 사각형 (Mystify)" };
    private readonly bool[] layers = { true, true, true, true };
    /// <summary>체크박스를 바꾸면 (설정에 남기도록).</summary>
    public event Action LayersChanged;

    private sealed class SideStar { public double X, Y, Speed; public int Size; public Color Color; }
    private sealed class WarpStar { public double X, Y, Z, Speed, Size; }
    private sealed class Shape
    {
        public bool Triangle; public Color Color; public double Size, X, Y, Vx, Vy, Rot, VRot;
        public readonly List<(double x, double y, double rot)> History = new List<(double, double, double)>();
    }

    private readonly Random rnd = new Random();
    private readonly List<SideStar> side = new List<SideStar>();
    private readonly List<WarpStar> warp = new List<WarpStar>();
    private readonly List<Shape> shapes = new List<Shape>();
    private readonly Timer anim = new Timer { Interval = 33 };
    private Bitmap canvas;       // 잔상이 남는 그림판 (안쪽 칸 크기)
    private readonly Rectangle[] checkRects = new Rectangle[4];
    private int hoverCheck = -1;
    private readonly Font hudBig = new Font("Courier New", 13f, FontStyle.Bold), hud = new Font("Courier New", 9f, FontStyle.Bold);
    private Font bigFont, nextFont;
    private int fontFor = -1;
    private IssLyrics lyrics;
    private LyricState state;
    public string Title = "";

    public CyberView()
    {
        anim.Tick += delegate { Step(); Invalidate(); };
        VisibleChanged += delegate { if (Visible) anim.Start(); else anim.Stop(); };
    }

    public IssLyrics Lyrics { get { return lyrics; } set { lyrics = value; Invalidate(); } }
    public LyricState State { get { return state; } set { state = value; } }

    /// <summary>켜진 층 "1111" (좌우 별 · 우주 · 삼각형 · 사각형).</summary>
    public string LayerText
    {
        get { var c = new char[4]; for (int i = 0; i < 4; i++) c[i] = layers[i] ? '1' : '0'; return new string(c); }
        set { for (int i = 0; i < 4; i++) layers[i] = value == null || i >= value.Length || value[i] != '0'; Invalidate(); }
    }

    private Rectangle Inner { get { return new Rectangle(8, 8, Math.Max(1, Width - 16), Math.Max(1, Height - 16)); } }

    // ---------------------------------------------------------------- 움직임 (HTML 그대로, 칸 크기 기준)

    private void Reset(SideStar s, int w, int h)
    {
        s.Y = rnd.NextDouble() * h;
        s.Size = rnd.NextDouble() < 0.8 ? 2 : 4;
        s.Speed = (rnd.NextDouble() * 2 + 0.5) * (rnd.NextDouble() < 0.5 ? 1 : -1);
        s.X = s.Speed > 0 ? -10 : w + 10;
        s.Color = rnd.NextDouble() < 0.1 ? Pink : Color.FromArgb(128, 255, 255, 255);
    }

    private void Reset(WarpStar s, int w, int h)
    {
        s.X = (rnd.NextDouble() - 0.5) * w * 2;
        s.Y = (rnd.NextDouble() - 0.5) * h * 2;
        s.Z = w;
        s.Speed = rnd.NextDouble() * 5 + 2;
        s.Size = rnd.NextDouble() * 2 + 1;
    }

    private Shape NewShape(bool triangle, Color color, int w, int h)
    {
        var s = new Shape { Triangle = triangle, Color = color, Size = rnd.NextDouble() * 30 + 30 };
        s.X = rnd.NextDouble() * Math.Max(1, w - s.Size * 2) + s.Size;
        s.Y = rnd.NextDouble() * Math.Max(1, h - s.Size * 2) + s.Size;
        s.Vx = (rnd.NextDouble() * 6 + 3) * (rnd.NextDouble() < 0.5 ? 1 : -1);
        s.Vy = (rnd.NextDouble() * 6 + 3) * (rnd.NextDouble() < 0.5 ? 1 : -1);
        s.VRot = rnd.NextDouble() * 0.1 - 0.05;
        return s;
    }

    private void Step()
    {
        var r = Inner;
        int w = r.Width, h = r.Height;
        if (w < 8 || h < 8) return;
        if (canvas == null || canvas.Width != w || canvas.Height != h)
        {
            canvas?.Dispose();
            canvas = new Bitmap(w, h);
            using (var g0 = Graphics.FromImage(canvas)) g0.Clear(Bg);
            side.Clear(); warp.Clear(); shapes.Clear();
            for (int i = 0; i < 80; i++) { var s = new SideStar(); Reset(s, w, h); s.X = rnd.NextDouble() * w; side.Add(s); }
            for (int i = 0; i < 300; i++) { var s = new WarpStar(); Reset(s, w, h); s.Z = rnd.NextDouble() * w + 1; warp.Add(s); }
            shapes.Add(NewShape(false, Pink, w, h));
            shapes.Add(NewShape(true, Yellow, w, h));
            shapes.Add(NewShape(false, Cyan, w, h));
        }
        using (var g = Graphics.FromImage(canvas))
        {
            // 잔상: 완전히 지우지 않고 반투명 검정으로 덮는다
            using (var fade = new SolidBrush(Color.FromArgb(77, Bg))) g.FillRectangle(fade, 0, 0, w, h);
            g.SmoothingMode = SmoothingMode.None;
            if (layers[0])
                foreach (var s in side)
                {
                    s.X += s.Speed;
                    if ((s.Speed > 0 && s.X > w) || (s.Speed < 0 && s.X < 0)) Reset(s, w, h);
                    using (var b = new SolidBrush(s.Color)) g.FillRectangle(b, (int)s.X, (int)s.Y, s.Size, s.Size);
                }
            if (layers[1])
                using (var b = new SolidBrush(Cyan))
                    foreach (var s in warp)
                    {
                        s.Z -= s.Speed;
                        if (s.Z <= 1) { Reset(s, w, h); continue; }
                        double px = w / 2.0 + s.X / s.Z * 300, py = h / 2.0 + s.Y / s.Z * 300;
                        if (px < 0 || px > w || py < 0 || py > h) { Reset(s, w, h); continue; }
                        double size = (1 - s.Z / w) * s.Size * 4;
                        if (size >= 0.5) g.FillRectangle(b, (float)px, (float)py, (float)Math.Max(1, size), (float)Math.Max(1, size));
                    }
            g.SmoothingMode = SmoothingMode.AntiAlias;
            foreach (var s in shapes)
            {
                if (!(s.Triangle ? layers[2] : layers[3])) { s.History.Clear(); continue; }
                s.History.Insert(0, (s.X, s.Y, s.Rot));
                if (s.History.Count > 15) s.History.RemoveAt(s.History.Count - 1);
                s.X += s.Vx; s.Y += s.Vy; s.Rot += s.VRot;
                if (s.X - s.Size < 0 || s.X + s.Size > w) { s.Vx = -s.Vx; s.X = Math.Max(s.Size, Math.Min(s.X, w - s.Size)); }
                if (s.Y - s.Size < 0 || s.Y + s.Size > h) { s.Vy = -s.Vy; s.Y = Math.Max(s.Size, Math.Min(s.Y, h - s.Size)); }
                for (int i = 0; i < s.History.Count; i++)
                {
                    int a = (int)(255 * (1 - i / (double)s.History.Count));
                    using (var p = new Pen(Color.FromArgb(a, s.Color), 2)) DrawShape(g, s, s.History[i].x, s.History[i].y, s.History[i].rot, p, null);
                }
                // 네온 빛: 크게 흐린 것 위에 본체
                using (var glow = new SolidBrush(Color.FromArgb(60, s.Color))) DrawShape(g, s, s.X, s.Y, s.Rot, null, glow, 1.35);
                using (var b = new SolidBrush(s.Color)) DrawShape(g, s, s.X, s.Y, s.Rot, null, b);
            }
        }
    }

    private static void DrawShape(Graphics g, Shape s, double x, double y, double rot, Pen pen, Brush brush, double scale = 1)
    {
        var st = g.Save();
        g.TranslateTransform((float)x, (float)y);
        g.RotateTransform((float)(rot * 180 / Math.PI));
        float h = (float)(s.Size * scale / 2);
        if (s.Triangle)
        {
            var pts = new[] { new PointF(0, -h), new PointF(h, h), new PointF(-h, h) };
            if (pen != null) g.DrawPolygon(pen, pts); else g.FillPolygon(brush, pts);
        }
        else if (pen != null) g.DrawRectangle(pen, -h, -h, 2 * h, 2 * h);
        else g.FillRectangle(brush, -h, -h, 2 * h, 2 * h);
        g.Restore(st);
    }

    // ---------------------------------------------------------------- 그리기

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(DeckTheme.Back);
        DeckTheme.Bevel(g, new Rectangle(0, 0, Width, Height), true, DeckTheme.Panel, 2);
        var r = Inner;
        if (canvas != null) g.DrawImageUnscaled(canvas, r.Location);
        else using (var b = new SolidBrush(Bg)) g.FillRectangle(b, r);
        g.SetClip(r);

        // 왼쪽 위 HUD
        var hudColor = Color.FromArgb(180, Cyan);
        TextRenderer.DrawText(g, "CYBER-SPACE // SECTOR 7G", hudBig, new Point(r.X + 14, r.Y + 10), hudColor, TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "SYSTEM: ONLINE", hud, new Point(r.X + 14, r.Y + 12 + hudBig.Height), hudColor, TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(g, "WARP ENGINE: " + (layers[1] ? "ACTIVE" : "IDLE"), hud, new Point(r.X + 14, r.Y + 14 + hudBig.Height + hud.Height), hudColor, TextFormatFlags.NoPrefix);

        DrawChecks(g, r);
        DrawLyrics(g, r);
        g.ResetClip();
    }

    /// <summary>오른쪽 위 구석: 네온 체크박스 4개 (반투명 검은 판).</summary>
    private void DrawChecks(Graphics g, Rectangle r)
    {
        int rowH = Math.Max(18, hud.Height + 6), boxW = 0;
        foreach (var n in LayerNames) boxW = Math.Max(boxW, TextRenderer.MeasureText(g, n, hud).Width);
        boxW += rowH + 18;
        var panel = new Rectangle(r.Right - boxW - 12, r.Y + 10, boxW, rowH * 4 + 10);
        using (var b = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRectangle(b, panel);
        using (var p = new Pen(Color.FromArgb(160, Cyan))) g.DrawRectangle(p, panel);
        for (int i = 0; i < 4; i++)
        {
            var row = new Rectangle(panel.X + 6, panel.Y + 5 + i * rowH, panel.Width - 12, rowH);
            checkRects[i] = row;
            var box = new Rectangle(row.X, row.Y + (rowH - 12) / 2, 12, 12);
            Color c = hoverCheck == i ? Pink : Cyan;
            using (var p = new Pen(c, 1.5f)) g.DrawRectangle(p, box);
            if (layers[i]) using (var b = new SolidBrush(Pink)) g.FillRectangle(b, box.X + 3, box.Y + 3, box.Width - 5, box.Height - 5);
            TextRenderer.DrawText(g, LayerNames[i], hud, new Point(box.Right + 6, row.Y + (rowH - hud.Height) / 2), layers[i] ? Color.White : Color.FromArgb(130, 140, 150), TextFormatFlags.NoPrefix);
        }
    }

    private void DrawLyrics(Graphics g, Rectangle r)
    {
        int bandH = Math.Max(56, Math.Min(r.Height * 2 / 5, 170));
        var band = new Rectangle(r.X, r.Bottom - bandH, r.Width, bandH);
        using (var b = new LinearGradientBrush(band, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(190, 0, 0, 0), LinearGradientMode.Vertical)) g.FillRectangle(b, band);
        using (var p = new Pen(Color.FromArgb(120, Pink))) g.DrawLine(p, band.X, band.Y + bandH / 6, band.Right, band.Y + bandH / 6);
        int size = Math.Max(14, Math.Min(40, bandH * 30 / 100));
        if (size != fontFor)
        {
            bigFont?.Dispose(); nextFont?.Dispose();
            bigFont = new Font("Malgun Gothic", size, FontStyle.Bold, GraphicsUnit.Pixel);
            nextFont = new Font("Malgun Gothic", Math.Max(11, size * 62 / 100), FontStyle.Bold, GraphicsUnit.Pixel);
            fontFor = size;
        }
        var glow = Color.FromArgb(0, 90, 110);
        if (lyrics != null && lyrics.Lines.Count > 0 && state != null && state.Line >= 0)
        {
            LyricsPainter.DrawLine(g, lyrics.Lines[state.Line], state.Lit, bigFont, band.Y + bandH * 0.45f, band.X + 16, band.Width - 32, Cyan, Pink, glow);
            if (state.NextLine >= 0 && state.NextLine < lyrics.Lines.Count)
                LyricsPainter.DrawLine(g, lyrics.Lines[state.NextLine], null, nextFont, band.Y + bandH * 0.80f, band.X + 16, band.Width - 32, Color.FromArgb(150, 200, 210), Pink, glow);
        }
        else
        {
            string t = lyrics == null ? (Title.Length > 0 ? Title : "♪") : "♪";
            var tr = new Rectangle(band.X + 16, band.Y + bandH / 5, band.Width - 32, bandH * 4 / 5);
            var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, t, nextFont, new Rectangle(tr.X + 2, tr.Y + 2, tr.Width, tr.Height), glow, flags);
            TextRenderer.DrawText(g, t, nextFont, tr, Cyan, flags);
        }
    }

    // ---------------------------------------------------------------- 체크박스

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int h = Array.FindIndex(checkRects, c => c.Contains(e.Location));
        if (h != hoverCheck) { hoverCheck = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverCheck = -1; }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = Array.FindIndex(checkRects, c => c.Contains(e.Location));
        if (i < 0 || e.Button != MouseButtons.Left) return;
        layers[i] = !layers[i];
        LayersChanged?.Invoke();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { anim.Dispose(); canvas?.Dispose(); hudBig.Dispose(); hud.Dispose(); bigFont?.Dispose(); nextFont?.Dispose(); }
        base.Dispose(disposing);
    }
}
