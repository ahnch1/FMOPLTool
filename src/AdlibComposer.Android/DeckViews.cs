using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Formats;
using Android.Content;
using Android.Graphics;
using Android.Views;

namespace AdlibComposer.Droid;

/// <summary>가사 한 줄 그리기 (가사 상자 · 노래방 · Cyberfunk 공용): 칸마다 너비를 재서 가운데에, 부른 칸은 on 색. 넘치면 글자를 줄인다.</summary>
internal static class LyricsPainter
{
    public static void DrawLine(Canvas c, Paint p, IssLine line, bool[] lit, float size, float centerY, float left, float width, Color dim, Color on, Color? shadow)
    {
        int first = 0, last = line.Cells.Count - 1;
        while (first <= last && line.Cells[first].Text.Trim().Length == 0) first++;
        while (last >= first && line.Cells[last].Text.Trim().Length == 0) last--;
        if (first > last || width <= 0) return;
        p.TextSize = size;
        p.TextAlign = Paint.Align.Left;
        float total = Measure(p, line, first, last);
        if (total > width) { p.TextSize = size * width / total; total = Measure(p, line, first, last); }
        float spaceW = p.MeasureText("가") / 2;
        float x = left + (width - total) / 2;
        float baseY = centerY - (p.Ascent() + p.Descent()) / 2;
        for (int i = first; i <= last; i++)
        {
            var cell = line.Cells[i];
            bool lighted = lit != null && cell.Column < lit.Length && lit[cell.Column];
            float w = cell.Text == " " ? spaceW : p.MeasureText(cell.Text);
            if (cell.Text != " ")
            {
                if (shadow.HasValue) { p.Color = shadow.Value; c.DrawText(cell.Text, x + 2, baseY + 2, p); }
                p.Color = lighted ? on : dim;
                c.DrawText(cell.Text, x, baseY, p);
            }
            x += w;
        }
    }

    private static float Measure(Paint p, IssLine line, int first, int last)
    {
        float spaceW = p.MeasureText("가") / 2, total = 0;
        for (int i = first; i <= last; i++) total += line.Cells[i].Text == " " ? spaceW : p.MeasureText(line.Cells[i].Text);
        return total;
    }

    /// <summary>가사 상태로 지금 줄 · 다음 줄 (없으면 제목이나 ♪)을 그린다.</summary>
    public static void DrawLyrics(Canvas c, Paint p, IssLyrics lyrics, LyricState st, string title, RectF r, float big, float next, float y1, float y2, Color dim, Color nextDim, Color on, Color? shadow)
    {
        if (lyrics != null && lyrics.Lines.Count > 0 && st != null && st.Line >= 0 && st.Line < lyrics.Lines.Count)
        {
            DrawLine(c, p, lyrics.Lines[st.Line], st.Lit, big, r.Top + r.Height() * y1, r.Left + 16, r.Width() - 32, dim, on, shadow);
            if (st.NextLine >= 0 && st.NextLine < lyrics.Lines.Count)
                DrawLine(c, p, lyrics.Lines[st.NextLine], null, next, r.Top + r.Height() * y2, r.Left + 16, r.Width() - 32, nextDim, on, shadow);
            return;
        }
        string t = lyrics == null && !string.IsNullOrEmpty(title) ? title : "♪";
        p.TextSize = next;
        p.TextAlign = Paint.Align.Center;
        while (t.Length > 1 && p.MeasureText(t) > r.Width() - 32) t = t.Substring(0, t.Length - 2) + "…";
        float baseY = r.CenterY() - (p.Ascent() + p.Descent()) / 2;
        if (shadow.HasValue) { p.Color = shadow.Value; c.DrawText(t, r.CenterX() + 2, baseY + 2, p); }
        p.Color = dim;
        c.DrawText(t, r.CenterX(), baseY, p);
        p.TextAlign = Paint.Align.Left;
    }
}

/// <summary>가사 상자 (Implay · Ocplay 위): 지금 줄 (부른 칸 = 청록)과 다음 줄. 가사가 없으면 안내.</summary>
internal sealed class LyricsView : View
{
    public IssLyrics Lyrics;
    public LyricState State;
    private readonly Paint paint = new Paint(PaintFlags.AntiAlias);

    public LyricsView(Context c) : base(c) { SetBackgroundColor(WideDeck.Panel); }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float d = Resources.DisplayMetrics.Density;
        var r = new RectF(6 * d, 6 * d, Width - 6 * d, Height - 6 * d);
        paint.Color = WideDeck.Inset;
        canvas.DrawRect(r, paint);
        paint.SetTypeface(Typeface.DefaultBold);
        float inner = r.Height();
        float big = Math.Min(26 * d, inner * 0.40f), next = Math.Min(17 * d, inner * 0.26f);
        if (Lyrics == null)
        {
            paint.TextAlign = Paint.Align.Center;
            paint.TextSize = next;
            paint.Color = WideDeck.Muted;
            canvas.DrawText("가사 파일(.ISS)이 없습니다", r.CenterX(), r.CenterY(), paint);
            paint.TextSize = 11 * d;
            paint.Color = Color.ParseColor("#596270");
            canvas.DrawText("NO LYRICS DATA LOADED", r.CenterX(), r.CenterY() + next, paint);
            paint.TextAlign = Paint.Align.Left;
            return;
        }
        LyricsPainter.DrawLyrics(canvas, paint, Lyrics, State, null, r, big, next, 0.36f, 0.79f, WideDeck.Text, WideDeck.Muted, WideDeck.Lcd, null);
    }
}

/// <summary>
/// Ocplay 풍 표시: 위 = 채널 색 막대가 흐르는 작은 피아노롤 (오른쪽 끝 = 지금, 약 12초), 아래 = 채널마다 세로 음량 막대 (맨 위 켜진 칸은 빨강).
/// </summary>
internal sealed class OcplayView : View
{
    private const int History = 240;   // 칸 수 (≈ 12초, 20번/초)
    private const int Segments = 10;
    private readonly List<int[]> notes = new List<int[]>();
    private List<MixerRow> rows = new List<MixerRow>();
    private readonly Paint paint = new Paint();

    public OcplayView(Context c) : base(c) { SetBackgroundColor(WideDeck.Panel); }

    public void Clear() { notes.Clear(); Invalidate(); }

    /// <summary>믹서 줄 (음 · 음량 · 색 · 끔)을 한 칸으로 받는다. 재생 중이 아니면 흐름은 멈춘다.</summary>
    public void Sample(List<MixerRow> source, bool playing)
    {
        rows = source;
        if (playing)
        {
            var col = new int[source.Count];
            for (int i = 0; i < col.Length; i++) col[i] = source[i].Muted ? -1 : source[i].Note;
            notes.Add(col);
            if (notes.Count > History) notes.RemoveRange(0, notes.Count - History);
        }
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float d = Resources.DisplayMetrics.Density;
        float pad = 6 * d;
        float meterH = Math.Min(80 * d, Math.Max(24 * d, (Height - pad * 2) / 3));
        var roll = new RectF(pad, pad, Width - pad, Height - pad - meterH - 4 * d);
        var meters = new RectF(pad, roll.Bottom + 4 * d, Width - pad, Height - pad);
        if (roll.Height() > 12) DrawRoll(canvas, roll, d);
        DrawMeters(canvas, meters, d);
    }

    private void DrawRoll(Canvas c, RectF r, float d)
    {
        paint.Color = Color.Black;
        c.DrawRect(r, paint);
        int lo = 127, hi = 0;
        foreach (var col in notes) foreach (int n in col) if (n >= 0) { lo = Math.Min(lo, n); hi = Math.Max(hi, n); }
        if (lo > hi) { lo = 48; hi = 72; }
        lo -= 2; hi += 2;
        if (hi - lo < 24) { int mid = (lo + hi) / 2; lo = mid - 12; hi = mid + 12; }
        float rowH = r.Height() / (hi - lo + 1);
        for (int n = lo; n <= hi; n++)
        {
            float y = r.Bottom - (n - lo + 1) * rowH;
            if (n % 12 == 0) { paint.Color = Color.Rgb(44, 50, 58); c.DrawLine(r.Left, y, r.Right, y, paint); }
            else if (rowH >= 4 * d && n % 12 == 5) { paint.Color = Color.Rgb(26, 30, 34); c.DrawLine(r.Left, y, r.Right, y, paint); }
        }
        if (notes.Count == 0) return;
        float colW = r.Width() / History;
        int start = History - notes.Count;
        float barH = Math.Max(2 * d, Math.Min(5 * d, rowH - 1));
        c.Save();
        c.ClipRect(r);
        for (int ch = 0; ch < rows.Count; ch++)
        {
            paint.Color = rows[ch].Color;
            int runStart = -1, runNote = -1;
            for (int k = 0; k <= notes.Count; k++)
            {
                int n = k < notes.Count && ch < notes[k].Length ? notes[k][ch] : -1;
                if (n == runNote && k < notes.Count) continue;
                if (runNote >= 0)
                {
                    float x0 = r.Left + (start + runStart) * colW, x1 = r.Left + (start + k) * colW;
                    float y = r.Bottom - (runNote - lo + 1) * rowH + Math.Max(0, (rowH - barH) / 2);
                    c.DrawRect(x0, y, Math.Max(x0 + 2, x1 - 1), y + barH, paint);
                }
                runStart = k; runNote = n;
            }
        }
        c.Restore();
    }

    private void DrawMeters(Canvas c, RectF r, float d)
    {
        paint.Color = WideDeck.Inset;
        c.DrawRect(r, paint);
        var inner = new RectF(r.Left + 4 * d, r.Top + 4 * d, r.Right - 4 * d, r.Bottom - 4 * d);
        int n = Math.Max(11, rows.Count);
        float colW = Math.Max(4 * d, Math.Min(44 * d, inner.Width() / n));
        int segments = Math.Max(4, Math.Min(Segments, (int)(inner.Height() / (4 * d))));
        float segH = (inner.Height() - (segments - 1) * d) / segments;
        for (int ch = 0; ch < rows.Count; ch++)
        {
            var row = rows[ch];
            float x = inner.Left + ch * colW;
            if (x + colW > inner.Right + 1) break;
            int on = row.Muted ? 0 : (int)Math.Round(Math.Max(0, Math.Min(1, row.Level)) * segments);
            for (int s = 0; s < segments; s++)
            {
                float y = inner.Bottom - (s + 1) * segH - s * d;
                paint.Color = s < on ? (s == on - 1 ? Color.Rgb(226, 52, 44) : row.Color) : Color.Rgb(70, 76, 84);
                c.DrawRect(x + d, y, x + colW - 2 * d, y + segH, paint);
            }
        }
    }
}

/// <summary>
/// 노래방: 곡 폴더(zip을 푼 폴더 포함)의 그림 (JPG · PNG · BMP · GIF)을 칸에 꽉 차게 보이고 10초마다 바꾼다. 곡 폴더에 그림이 없으면 설정의 그림 폴더.
/// 아래쪽 반투명 띠에 가사 (부른 글자 = 노랑), 가사가 없으면 곡 제목. 누르면 다음 그림.
/// </summary>
internal sealed class KaraokeView : View
{
    private static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif" };
    private const int MaxImageSide = 2048;
    private readonly Random rnd = new Random();
    private readonly Paint paint = new Paint(PaintFlags.AntiAlias | PaintFlags.FilterBitmap);
    private string folderKey;
    private List<string> images = new List<string>();
    private int current = -1;
    private Bitmap image;
    private long nextSlide;
    public IssLyrics Lyrics;
    public LyricState State;
    public string Title = "";

    public KaraokeView(Context c) : base(c)
    {
        SetBackgroundColor(WideDeck.Panel);
        Click += delegate { NextImage(); };
    }

    public bool HasImages { get { return images.Count > 0; } }

    /// <summary>곡이 바뀌면: 그 폴더의 그림 목록 (같은 폴더면 다시 읽지 않음)과 새 그림 하나.</summary>
    public void SetSong(string songPath)
    {
        string dir = null;
        try { dir = string.IsNullOrEmpty(songPath) ? null : System.IO.Path.GetDirectoryName(AdlibComposer.Audio.ChipSongs.FilePart(songPath)); } catch (Exception) { }
        string fallback = JukeboxPrefs.KaraokeFolder;
        string key = dir + "|" + fallback;
        if (key == folderKey) return;
        folderKey = key;
        images = ImagesIn(dir);
        if (images.Count == 0 && fallback != null) images = ImagesIn(fallback);
        current = -1;
        NextImage();
    }

    /// <summary>그림 폴더 설정이 바뀐 뒤.</summary>
    public void Reload(string songPath) { folderKey = null; SetSong(songPath); }

    private static List<string> ImagesIn(string dir)
    {
        try
        {
            if (dir != null && Directory.Exists(dir))
                return Directory.GetFiles(dir).Where(f => ImageExt.Any(x => string.Equals(System.IO.Path.GetExtension(f), x, StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (Exception) { }
        return new List<string>();
    }

    /// <summary>10초가 지났으면 다른 그림으로 (화면 갱신마다 부른다).</summary>
    public void Tick()
    {
        if (images.Count > 1 && Java.Lang.JavaSystem.CurrentTimeMillis() >= nextSlide) NextImage();
    }

    public void NextImage()
    {
        nextSlide = Java.Lang.JavaSystem.CurrentTimeMillis() + 10000;
        if (images.Count == 0) { SetImage(null); return; }
        int next = images.Count == 1 ? 0 : rnd.Next(images.Count - 1);
        if (images.Count > 1 && next >= current && current >= 0) next++;
        if (next == current && image != null) return;
        current = next;
        Bitmap bmp = null;
        try
        {
            // 크게 줄여 읽는다 (긴 변 2048 이하)
            var o = new BitmapFactory.Options { InJustDecodeBounds = true };
            BitmapFactory.DecodeFile(images[current], o);
            int sample = 1;
            while (Math.Max(o.OutWidth, o.OutHeight) / sample > MaxImageSide) sample *= 2;
            bmp = BitmapFactory.DecodeFile(images[current], new BitmapFactory.Options { InSampleSize = sample });
        }
        catch (Exception) { }
        SetImage(bmp);
    }

    private void SetImage(Bitmap bmp)
    {
        var old = image;
        image = bmp;
        old?.Recycle();
        Invalidate();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float d = Resources.DisplayMetrics.Density;
        var r = new RectF(6 * d, 6 * d, Width - 6 * d, Height - 6 * d);
        if (r.Width() <= 0 || r.Height() <= 0) return;
        canvas.Save();
        canvas.ClipRect(r);
        if (image != null)
        {
            // 칸에 꽉 차게 (가운데 기준으로 넘치는 쪽을 자름)
            float k = Math.Max(r.Width() / image.Width, r.Height() / image.Height);
            float w = image.Width * k, h = image.Height * k;
            canvas.DrawBitmap(image, null, new RectF(r.CenterX() - w / 2, r.CenterY() - h / 2, r.CenterX() + w / 2, r.CenterY() + h / 2), paint);
        }
        else
        {
            paint.SetShader(new LinearGradient(r.Left, r.Top, r.Right, r.Bottom, Color.Rgb(24, 40, 52), Color.Rgb(10, 96, 92), Shader.TileMode.Clamp));
            canvas.DrawRect(r, paint);
            paint.SetShader(null);
            paint.Color = Color.Rgb(170, 200, 200);
            paint.TextSize = 14 * d;
            paint.TextAlign = Paint.Align.Center;
            canvas.DrawText("그림이 없습니다 (JPG · PNG · BMP · GIF) — 곡 폴더에 넣거나 [설정]에서 그림 폴더를 고르세요", r.CenterX(), r.Top + 28 * d, paint);
            paint.TextAlign = Paint.Align.Left;
        }
        // 아래쪽 가사 띠
        float bandH = Math.Max(56 * d, Math.Min(r.Height() * 2 / 5, 170 * d));
        var band = new RectF(r.Left, r.Bottom - bandH, r.Right, r.Bottom);
        paint.Color = Color.Argb(150, 0, 0, 0);
        canvas.DrawRect(band, paint);
        paint.SetTypeface(Typeface.DefaultBold);
        float big = Math.Max(14 * d, Math.Min(40 * d, bandH * 0.30f));
        LyricsPainter.DrawLyrics(canvas, paint, Lyrics, State, Title, band, big, big * 0.62f, 0.38f, 0.78f,
            Color.Rgb(250, 250, 250), Color.Rgb(200, 210, 214), Color.Rgb(255, 214, 64), Color.Rgb(20, 20, 20));
        canvas.Restore();
    }
}

/// <summary>
/// 노래방(Cyberfunk): 반투명 검정으로 덮어 잔상을 남기는 화면에 [좌우로 흐르는 픽셀 별] [가운데서 퍼지는 워프 별] [잔상을 끌며 튕기는 삼각형 · 사각형],
/// 오른쪽 위 체크박스로 층을 켜고 끈다. 아래쪽 네온 가사 띠 (부른 글자 = 분홍), 왼쪽 위 HUD.
/// </summary>
internal sealed class CyberView : View
{
    private static readonly Color Pink = Color.Rgb(255, 0, 127), Cyan = Color.Rgb(0, 255, 255), Yellow = Color.Rgb(255, 234, 0), Bg = Color.Rgb(5, 5, 10);
    public static readonly string[] LayerNames = { "좌우로 흐르는 별", "우주 유영 (Starfield)", "튕기는 삼각형 (Mystify)", "튕기는 사각형 (Mystify)" };
    private readonly bool[] layers = { true, true, true, true };

    private sealed class SideStar { public float X, Y, Speed, Size; public Color Color; }
    private sealed class WarpStar { public float X, Y, Z, Speed, Size; }
    private sealed class Shape
    {
        public bool Triangle; public Color Color; public float Size, X, Y, Vx, Vy, Rot, VRot;
        public readonly List<(float x, float y, float rot)> History = new List<(float, float, float)>();
    }

    private readonly Random rnd = new Random();
    private readonly List<SideStar> side = new List<SideStar>();
    private readonly List<WarpStar> warp = new List<WarpStar>();
    private readonly List<Shape> shapes = new List<Shape>();
    private Bitmap board;
    private Canvas boardCanvas;
    private readonly Paint paint = new Paint(PaintFlags.AntiAlias);
    private readonly RectF[] checkRects = new RectF[4];
    private long lastStep;
    private readonly float d;
    public IssLyrics Lyrics;
    public LyricState State;
    public string Title = "";

    public CyberView(Context c) : base(c)
    {
        d = c.Resources.DisplayMetrics.Density;
        SetBackgroundColor(WideDeck.Panel);
        LayerText = JukeboxPrefs.CyberLayers;
    }

    public string LayerText
    {
        get { var ch = new char[4]; for (int i = 0; i < 4; i++) ch[i] = layers[i] ? '1' : '0'; return new string(ch); }
        set { for (int i = 0; i < 4; i++) layers[i] = value == null || i >= value.Length || value[i] != '0'; }
    }

    private RectF Inner { get { return new RectF(6 * d, 6 * d, Math.Max(7 * d, Width - 6 * d), Math.Max(7 * d, Height - 6 * d)); } }

    private void Reset(SideStar s, int w, int h)
    {
        s.Y = (float)(rnd.NextDouble() * h);
        s.Size = (rnd.NextDouble() < 0.8 ? 2 : 4) * d / 1.5f;
        s.Speed = (float)((rnd.NextDouble() * 2 + 0.5) * (rnd.NextDouble() < 0.5 ? 1 : -1)) * d;
        s.X = s.Speed > 0 ? -10 : w + 10;
        s.Color = rnd.NextDouble() < 0.1 ? Pink : Color.Argb(128, 255, 255, 255);
    }

    private void Reset(WarpStar s, int w, int h)
    {
        s.X = (float)((rnd.NextDouble() - 0.5) * w * 2);
        s.Y = (float)((rnd.NextDouble() - 0.5) * h * 2);
        s.Z = w;
        s.Speed = (float)(rnd.NextDouble() * 5 + 2);
        s.Size = (float)(rnd.NextDouble() * 2 + 1);
    }

    private Shape NewShape(bool triangle, Color color, int w, int h)
    {
        var s = new Shape { Triangle = triangle, Color = color, Size = (float)(rnd.NextDouble() * 30 + 30) * d };
        s.X = (float)(rnd.NextDouble() * Math.Max(1, w - s.Size * 2) + s.Size);
        s.Y = (float)(rnd.NextDouble() * Math.Max(1, h - s.Size * 2) + s.Size);
        s.Vx = (float)((rnd.NextDouble() * 6 + 3) * (rnd.NextDouble() < 0.5 ? 1 : -1)) * d / 1.5f;
        s.Vy = (float)((rnd.NextDouble() * 6 + 3) * (rnd.NextDouble() < 0.5 ? 1 : -1)) * d / 1.5f;
        s.VRot = (float)(rnd.NextDouble() * 0.1 - 0.05);
        return s;
    }

    /// <summary>한 장면 움직인다 (약 30번/초, 보이는 동안 OnDraw가 다음 그리기를 예약).</summary>
    private void Step()
    {
        var r = Inner;
        int w = (int)r.Width(), h = (int)r.Height();
        if (w < 8 || h < 8) return;
        if (board == null || board.Width != w || board.Height != h)
        {
            board?.Recycle();
            board = Bitmap.CreateBitmap(w, h, Bitmap.Config.Argb8888);
            boardCanvas = new Canvas(board);
            boardCanvas.DrawColor(Bg);
            side.Clear(); warp.Clear(); shapes.Clear();
            for (int i = 0; i < 80; i++) { var s = new SideStar(); Reset(s, w, h); s.X = (float)(rnd.NextDouble() * w); side.Add(s); }
            for (int i = 0; i < 300; i++) { var s = new WarpStar(); Reset(s, w, h); s.Z = (float)(rnd.NextDouble() * w + 1); warp.Add(s); }
            shapes.Add(NewShape(false, Pink, w, h));
            shapes.Add(NewShape(true, Yellow, w, h));
            shapes.Add(NewShape(false, Cyan, w, h));
        }
        var g = boardCanvas;
        paint.SetStyle(Paint.Style.Fill);
        paint.Color = Color.Argb(77, Bg.R, Bg.G, Bg.B);
        g.DrawRect(0, 0, w, h, paint);
        if (layers[0])
            foreach (var s in side)
            {
                s.X += s.Speed;
                if ((s.Speed > 0 && s.X > w) || (s.Speed < 0 && s.X < 0)) Reset(s, w, h);
                paint.Color = s.Color;
                g.DrawRect(s.X, s.Y, s.X + s.Size, s.Y + s.Size, paint);
            }
        if (layers[1])
        {
            paint.Color = Cyan;
            float zoom = 300 * d / 1.5f;
            foreach (var s in warp)
            {
                s.Z -= s.Speed;
                if (s.Z <= 1) { Reset(s, w, h); continue; }
                float px = w / 2f + s.X / s.Z * zoom, py = h / 2f + s.Y / s.Z * zoom;
                if (px < 0 || px > w || py < 0 || py > h) { Reset(s, w, h); continue; }
                float size = (1 - s.Z / w) * s.Size * 4;
                if (size >= 0.5f) g.DrawRect(px, py, px + Math.Max(1, size), py + Math.Max(1, size), paint);
            }
        }
        foreach (var s in shapes)
        {
            if (!(s.Triangle ? layers[2] : layers[3])) { s.History.Clear(); continue; }
            s.History.Insert(0, (s.X, s.Y, s.Rot));
            if (s.History.Count > 15) s.History.RemoveAt(s.History.Count - 1);
            s.X += s.Vx; s.Y += s.Vy; s.Rot += s.VRot;
            if (s.X - s.Size < 0 || s.X + s.Size > w) { s.Vx = -s.Vx; s.X = Math.Max(s.Size, Math.Min(s.X, w - s.Size)); }
            if (s.Y - s.Size < 0 || s.Y + s.Size > h) { s.Vy = -s.Vy; s.Y = Math.Max(s.Size, Math.Min(s.Y, h - s.Size)); }
            paint.SetStyle(Paint.Style.Stroke);
            paint.StrokeWidth = 2 * d / 1.5f;
            for (int i = 0; i < s.History.Count; i++)
            {
                int a = (int)(255 * (1 - i / (double)s.History.Count));
                paint.Color = Color.Argb(a, s.Color.R, s.Color.G, s.Color.B);
                DrawShape(g, s, s.History[i].x, s.History[i].y, s.History[i].rot, 1);
            }
            paint.SetStyle(Paint.Style.Fill);
            paint.Color = Color.Argb(60, s.Color.R, s.Color.G, s.Color.B);
            DrawShape(g, s, s.X, s.Y, s.Rot, 1.35f);
            paint.Color = s.Color;
            DrawShape(g, s, s.X, s.Y, s.Rot, 1);
        }
        paint.SetStyle(Paint.Style.Fill);
    }

    private void DrawShape(Canvas g, Shape s, float x, float y, float rot, float scale)
    {
        g.Save();
        g.Translate(x, y);
        g.Rotate(rot * 180 / (float)Math.PI);
        float h = s.Size * scale / 2;
        if (s.Triangle)
        {
            using (var path = new Android.Graphics.Path())
            {
                path.MoveTo(0, -h); path.LineTo(h, h); path.LineTo(-h, h); path.Close();
                g.DrawPath(path, paint);
            }
        }
        else g.DrawRect(-h, -h, h, h, paint);
        g.Restore();
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        // 움직임은 약 30번/초로만 (데크 갱신이 함께 다시 그려도 빨라지지 않게)
        long now = Java.Lang.JavaSystem.CurrentTimeMillis();
        if (now - lastStep >= 30) { lastStep = now; Step(); }
        var r = Inner;
        if (board != null) canvas.DrawBitmap(board, r.Left, r.Top, null);
        canvas.Save();
        canvas.ClipRect(r);

        // 왼쪽 위 HUD
        paint.SetTypeface(Typeface.Create(Typeface.Monospace, TypefaceStyle.Bold));
        paint.Color = Color.Argb(180, 0, 255, 255);
        paint.TextSize = 16 * d;
        canvas.DrawText("CYBER-SPACE // SECTOR 7G", r.Left + 14 * d, r.Top + 26 * d, paint);
        paint.TextSize = 11 * d;
        canvas.DrawText("SYSTEM: ONLINE", r.Left + 14 * d, r.Top + 44 * d, paint);
        canvas.DrawText("WARP ENGINE: " + (layers[1] ? "ACTIVE" : "IDLE"), r.Left + 14 * d, r.Top + 60 * d, paint);

        DrawChecks(canvas, r);

        // 아래쪽 네온 가사 띠
        float bandH = Math.Max(56 * d, Math.Min(r.Height() * 2 / 5, 170 * d));
        var band = new RectF(r.Left, r.Bottom - bandH, r.Right, r.Bottom);
        paint.SetShader(new LinearGradient(0, band.Top, 0, band.Bottom, Color.Argb(0, 0, 0, 0), Color.Argb(190, 0, 0, 0), Shader.TileMode.Clamp));
        canvas.DrawRect(band, paint);
        paint.SetShader(null);
        paint.Color = Color.Argb(120, 255, 0, 127);
        canvas.DrawLine(band.Left, band.Top + bandH / 6, band.Right, band.Top + bandH / 6, paint);
        paint.SetTypeface(Typeface.DefaultBold);
        float big = Math.Max(14 * d, Math.Min(40 * d, bandH * 0.30f));
        LyricsPainter.DrawLyrics(canvas, paint, Lyrics, State, Title, band, big, big * 0.62f, 0.45f, 0.80f, Cyan, Color.Rgb(150, 200, 210), Pink, Color.Rgb(0, 90, 110));
        canvas.Restore();

        // 보이는 동안 약 30번/초
        if (IsShown) PostInvalidateDelayed(33);
    }

    private void DrawChecks(Canvas c, RectF r)
    {
        paint.SetTypeface(Typeface.Create(Typeface.Monospace, TypefaceStyle.Bold));
        paint.TextSize = 12 * d;
        float rowH = 26 * d, boxW = 0;
        foreach (var n in LayerNames) boxW = Math.Max(boxW, paint.MeasureText(n));
        boxW += rowH + 18 * d;
        var panel = new RectF(r.Right - boxW - 12 * d, r.Top + 10 * d, r.Right - 12 * d, r.Top + 10 * d + rowH * 4 + 10 * d);
        paint.Color = Color.Argb(150, 0, 0, 0);
        c.DrawRect(panel, paint);
        paint.SetStyle(Paint.Style.Stroke);
        paint.StrokeWidth = d;
        paint.Color = Color.Argb(160, 0, 255, 255);
        c.DrawRect(panel, paint);
        paint.SetStyle(Paint.Style.Fill);
        for (int i = 0; i < 4; i++)
        {
            var row = new RectF(panel.Left + 6 * d, panel.Top + 5 * d + i * rowH, panel.Right - 6 * d, panel.Top + 5 * d + (i + 1) * rowH);
            checkRects[i] = row;
            float bs = 14 * d;
            var box = new RectF(row.Left, row.CenterY() - bs / 2, row.Left + bs, row.CenterY() + bs / 2);
            paint.SetStyle(Paint.Style.Stroke);
            paint.StrokeWidth = 1.5f * d;
            paint.Color = Cyan;
            c.DrawRect(box, paint);
            paint.SetStyle(Paint.Style.Fill);
            if (layers[i]) { paint.Color = Pink; c.DrawRect(box.Left + 3 * d, box.Top + 3 * d, box.Right - 3 * d, box.Bottom - 3 * d, paint); }
            paint.Color = layers[i] ? Color.White : Color.Rgb(130, 140, 150);
            c.DrawText(LayerNames[i], box.Right + 6 * d, row.CenterY() - (paint.Ascent() + paint.Descent()) / 2, paint);
        }
    }

    public override bool OnTouchEvent(MotionEvent e)
    {
        if (e.Action != MotionEventActions.Down) return true;
        for (int i = 0; i < 4; i++)
            if (checkRects[i] != null && checkRects[i].Contains(e.GetX(), e.GetY()))
            {
                layers[i] = !layers[i];
                JukeboxPrefs.SetCyberLayers(LayerText);
                Invalidate();
                return true;
            }
        return true;
    }
}
