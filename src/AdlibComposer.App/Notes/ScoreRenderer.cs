using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using AdlibComposer.Core;
using AdlibComposer.Notation;

namespace AdlibComposer.App.Notes;

/// <summary>악보에서 고를 수 있는 음표 (클릭 → Composer 같은 위치로).</summary>
internal sealed class ScoreHit
{
    public RectangleF Bounds;
    public int Voice;
    public int Tick;
    public int Pitch;
    public string Label;
}

/// <summary>한 줄(시스템)에 들어가는 마디 범위와 위치.</summary>
internal sealed class ScoreSystem
{
    public int FirstMeasure, LastMeasure;   // 마디 인덱스 (0부터, 포함)
    public float Y, Height;
    public float ContentLeft;
    public readonly List<float> MeasureX = new List<float>();      // 각 마디 시작 x (+ 마지막 마디 끝)
    public readonly List<float[]> ColumnX = new List<float[]>();   // 마디별 [틱 → x] 열
    public readonly List<int[]> ColumnTicks = new List<int[]>();
}

/// <summary>
/// 오선 악보 그리기. Score(마디/음) → 줄(시스템) 배치 → Graphics에 그리기 (화면 · PNG · 인쇄 공용).
/// 보이스마다 오선 하나: 높은음자리표 / 낮은음자리표 (평균 음높이로), 드럼 보이스는 한 줄 타악기 보표.
/// 같은 박은 모든 보이스에서 같은 x에 오도록 마디 안을 "시간 열"로 나누고, 짧은 음일수록 좁게 둔다.
/// </summary>
internal sealed class ScoreRenderer
{
    private sealed class Glyph
    {
        public int Start, Duration, Pitch, AbsTick;
        public NoteValue Value;
        public int Dots;
        public bool IsRest, TieStart, TieStop, Exact, Triplet;
    }

    private readonly AdlibComposer.Notation.Score score;
    private readonly List<StaffClef> clefs = new List<StaffClef>();
    private readonly List<List<List<Glyph>>> glyphs = new List<List<List<Glyph>>>();   // [voice][measure][glyph]
    private readonly int measureCount;

    public float Zoom { get; set; } = 1f;
    public string Title { get; set; }
    public int HoverIndex { get; set; } = -1;
    public readonly List<ScoreHit> Hits = new List<ScoreHit>();

    private static readonly Font BaseFont = new Font("Segoe UI", 9f);
    private const string SymbolFont = "Segoe UI Symbol";

    public ScoreRenderer(AdlibComposer.Notation.Score score)
    {
        this.score = score;
        measureCount = 0;
        foreach (var v in score.Voices)
        {
            clefs.Add(StaffRules.ClefFor(v));
            var perMeasure = new List<List<Glyph>>();
            foreach (var m in v.Measures)
            {
                var list = new List<Glyph>();
                foreach (var n in m.Notes)
                {
                    var parts = NoteSpelling.Split(n.StartInMeasure, n.Duration, score.TickBeat);
                    for (int i = 0; i < parts.Count; i++)
                    {
                        var g = parts[i];
                        list.Add(new Glyph
                        {
                            Start = g.Start, Duration = g.Duration, Value = g.Value, Dots = g.Dots, Exact = g.Exact, Triplet = g.Triplet,
                            Pitch = n.Pitch, IsRest = n.IsRest, AbsTick = m.StartTick + g.Start,
                            TieStart = !n.IsRest && (i < parts.Count - 1 || n.TiedToNext),
                            TieStop = !n.IsRest && (i > 0 || n.TiedFromPrevious),
                        });
                    }
                }
                perMeasure.Add(list);
            }
            glyphs.Add(perMeasure);
            measureCount = Math.Max(measureCount, v.Measures.Count);
        }
    }

    public bool IsEmpty { get { return score.Voices.Count == 0; } }

    // ---------------------------------------------------------------- 치수 (zoom 반영)

    private float Sp { get { return 8f * Zoom; } }                      // 오선 한 칸
    private float LabelWidth { get { return 96f * Zoom; } }
    private float ClefWidth { get { return 4.2f * Sp; } }
    private float TimeWidth { get { return 2.8f * Sp; } }
    private float TitleHeight { get { return string.IsNullOrEmpty(Title) ? 0 : 7f * Sp; } }
    private float SystemTopPad { get { return 2.5f * Sp; } }
    private float SystemGap { get { return 2.5f * Sp; } }

    private float BlockHeight(int voice) { return clefs[voice] == StaffClef.Percussion ? 6f * Sp : 12f * Sp; }
    /// <summary>블록 위에서 오선 맨 아래 줄까지의 거리.</summary>
    private float StaffBottomOffset(int voice) { return clefs[voice] == StaffClef.Percussion ? 3f * Sp : 8f * Sp; }

    private float Segment(int dur)
    {
        double beats = dur / (double)Math.Max(1, score.TickBeat);
        return (float)(1.7 * Sp + 2.6 * Sp * Math.Sqrt(Math.Max(0.0625, beats)));
    }

    // ---------------------------------------------------------------- 배치

    /// <summary>폭에 맞춰 마디를 줄로 나눈다. 반환: 줄 목록과 전체 높이.</summary>
    public List<ScoreSystem> Layout(float width, out float totalHeight)
    {
        var systems = new List<ScoreSystem>();
        float y = TitleHeight + Sp;
        float blocks = 0;
        for (int v = 0; v < clefs.Count; v++) blocks += BlockHeight(v);
        int m = 0;
        while (m < measureCount)
        {
            var sys = new ScoreSystem { FirstMeasure = m, Y = y };
            float left = Sp + LabelWidth + ClefWidth + (m == 0 ? TimeWidth : 0);
            sys.ContentLeft = left;
            float x = left;
            float avail = width - Sp;
            do
            {
                int[] ticks;
                float[] xs;
                float w = MeasureColumns(m, out ticks, out xs);
                if (sys.MeasureX.Count > 0 && x + w > avail) break;
                sys.MeasureX.Add(x);
                for (int i = 0; i < xs.Length; i++) xs[i] += x;
                sys.ColumnTicks.Add(ticks);
                sys.ColumnX.Add(xs);
                x += w;
                m++;
            } while (m < measureCount);
            sys.MeasureX.Add(x);
            sys.LastMeasure = m - 1;
            sys.Height = SystemTopPad + blocks;
            systems.Add(sys);
            y += sys.Height + SystemGap;
        }
        totalHeight = y + Sp;
        return systems;
    }

    /// <summary>마디 하나의 시간 열 (모든 보이스의 음표 시작 틱) 과 마디 폭.</summary>
    private float MeasureColumns(int m, out int[] ticks, out float[] xs)
    {
        int len = Math.Max(1, score.TickBeat * score.BeatsPerMeasure);
        var set = new SortedSet<int> { 0, len };
        var accidentalAt = new HashSet<int>();
        for (int v = 0; v < glyphs.Count; v++)
        {
            if (m >= glyphs[v].Count) continue;
            foreach (var g in glyphs[v][m])
            {
                set.Add(g.Start);
                if (!g.IsRest && clefs[v] != StaffClef.Percussion && NoteHelper.IsBlack(g.Pitch)) accidentalAt.Add(g.Start);
            }
        }
        ticks = new int[set.Count];
        set.CopyTo(ticks);
        xs = new float[ticks.Length];
        float x = 1.4f * Sp;
        for (int i = 0; i < ticks.Length; i++)
        {
            if (accidentalAt.Contains(ticks[i])) x += 1.1f * Sp;
            xs[i] = x;
            if (i < ticks.Length - 1) x += Segment(ticks[i + 1] - ticks[i]);
        }
        xs[ticks.Length - 1] = x;
        return x + 0.6f * Sp;
    }

    private int MeasureLength { get { return Math.Max(1, score.TickBeat * score.BeatsPerMeasure); } }

    /// <summary>곡 틱이 놓이는 줄과 x (마디 안 시간 열 사이를 직선으로 나눔). 악보 밖이면 false.</summary>
    public bool Locate(List<ScoreSystem> systems, double tick, out ScoreSystem sys, out float x)
    {
        sys = null; x = 0;
        if (systems == null || systems.Count == 0 || tick < 0) return false;
        int len = MeasureLength;
        int m = (int)(tick / len);
        foreach (var s in systems)
        {
            if (m < s.FirstMeasure || m > s.LastMeasure) continue;
            int mi = m - s.FirstMeasure;
            double t = tick - (double)m * len;
            var ticks = s.ColumnTicks[mi];
            var xs = s.ColumnX[mi];
            int i = Array.BinarySearch(ticks, (int)Math.Floor(t));
            if (i < 0) i = Math.Max(0, ~i - 1);
            if (i >= ticks.Length - 1) { x = xs[ticks.Length - 1]; }
            else
            {
                double f = (t - ticks[i]) / Math.Max(1, ticks[i + 1] - ticks[i]);
                x = (float)(xs[i] + (xs[i + 1] - xs[i]) * Math.Max(0, Math.Min(1, f)));
            }
            sys = s;
            return true;
        }
        return false;
    }

    /// <summary>화면 점 → 곡 틱 (그 줄의 마디 · 시간 열 사이를 직선으로). 줄 밖이면 -1.</summary>
    public double TickAt(List<ScoreSystem> systems, PointF p)
    {
        if (systems == null) return -1;
        int len = MeasureLength;
        foreach (var s in systems)
        {
            if (p.Y < s.Y || p.Y > s.Y + s.Height) continue;
            int last = s.MeasureX.Count - 1;
            if (p.X < s.MeasureX[0]) return (double)s.FirstMeasure * len;
            if (p.X >= s.MeasureX[last]) return (double)(s.LastMeasure + 1) * len - 1;
            for (int mi = 0; mi < last; mi++)
            {
                if (p.X >= s.MeasureX[mi + 1]) continue;
                var ticks = s.ColumnTicks[mi];
                var xs = s.ColumnX[mi];
                double t = 0;
                if (p.X >= xs[ticks.Length - 1]) t = ticks[ticks.Length - 1];
                else if (p.X > xs[0])
                    for (int i = 0; i < ticks.Length - 1; i++)
                        if (p.X < xs[i + 1]) { t = ticks[i] + (ticks[i + 1] - ticks[i]) * (p.X - xs[i]) / Math.Max(0.01f, xs[i + 1] - xs[i]); break; }
                return (double)(s.FirstMeasure + mi) * len + Math.Max(0, Math.Min(len - 1, t));
            }
        }
        return -1;
    }

    /// <summary>줄 위에 세로선 (재생 위치 · 현재 위치).</summary>
    public void DrawMarker(Graphics g, ScoreSystem s, float x, Color color, float width)
    {
        using (var pen = new Pen(color, Math.Max(1f, width * Zoom)))
            g.DrawLine(pen, x, s.Y + SystemTopPad * 0.5f, x, s.Y + s.Height);
    }

    private static float XAt(ScoreSystem s, int measureInSystem, int tick)
    {
        var ticks = s.ColumnTicks[measureInSystem];
        int i = Array.BinarySearch(ticks, tick);
        if (i < 0) i = Math.Max(0, ~i - 1);
        return s.ColumnX[measureInSystem][i];
    }

    // ---------------------------------------------------------------- 그리기

    public void DrawTitle(Graphics g, float width, float yOffset)
    {
        if (string.IsNullOrEmpty(Title)) return;
        using (var f = new Font("Segoe UI Semibold", 15f * Zoom))
        using (var small = new Font("Segoe UI", 8.5f * Zoom))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center })
        {
            g.DrawString(Title, f, Brushes.Black, new RectangleF(0, yOffset + Sp, width, 3f * Sp), sf);
            string tempo = "♩ = " + Math.Round(score.Tempo) + "     " + score.BeatsPerMeasure + "/4";
            g.DrawString(tempo, small, Brushes.DimGray, Sp + LabelWidth, yOffset + 4.6f * Sp);
        }
    }

    /// <summary>줄 하나를 그린다. yOffset만큼 내려서 (인쇄 페이지 등).</summary>
    public void DrawSystem(Graphics g, ScoreSystem s, float yOffset, float width, bool collectHits)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        float top = s.Y + yOffset + SystemTopPad;
        float right = s.MeasureX[s.MeasureX.Count - 1];
        using (var pen = new Pen(Color.FromArgb(40, 40, 40), Math.Max(1f, 0.11f * Sp)))
        using (var thin = new Pen(Color.FromArgb(60, 60, 60), Math.Max(1f, 0.1f * Sp)))
        using (var label = new Font("Segoe UI", 8f * Zoom))
        using (var labelBold = new Font("Segoe UI Semibold", 8.5f * Zoom))
        using (var measureNo = new Font("Segoe UI", 7f * Zoom))
        {
            // 마디 번호
            g.DrawString((s.FirstMeasure + 1).ToString(), measureNo, Brushes.Gray, s.MeasureX[0], top - 2.3f * Sp);

            float blockTop = top;
            float firstStaffTop = -1, lastStaffBottom = 0;
            for (int v = 0; v < clefs.Count; v++)
            {
                float bottom = blockTop + StaffBottomOffset(v);
                bool perc = clefs[v] == StaffClef.Percussion;
                float staffTop = perc ? bottom : bottom - 4 * Sp;
                if (firstStaffTop < 0) firstStaffTop = staffTop;
                lastStaffBottom = bottom;

                // 보이스 이름
                var sv = score.Voices[v];
                string head = sv.Drum != DrumKind.None ? sv.Label : "Voice " + sv.Label;
                g.DrawString(head, labelBold, Brushes.Black, Sp, (staffTop + bottom) / 2 - 1.6f * Sp);
                g.DrawString(sv.InstrumentName ?? "", label, Brushes.DimGray, Sp, (staffTop + bottom) / 2 + 0.1f * Sp);

                // 오선
                float x0 = Sp + LabelWidth;
                if (perc) g.DrawLine(thin, x0, bottom, right, bottom);
                else for (int i = 0; i < 5; i++) g.DrawLine(thin, x0, bottom - i * Sp, right, bottom - i * Sp);

                DrawClef(g, v, x0 + 0.4f * Sp, bottom);
                if (s.FirstMeasure == 0) DrawTimeSignature(g, v, x0 + ClefWidth, bottom);

                // 마디 줄
                for (int i = 1; i < s.MeasureX.Count; i++)
                {
                    float bx = s.MeasureX[i];
                    bool final = s.LastMeasure == measureCount - 1 && i == s.MeasureX.Count - 1;
                    float t = perc ? bottom - 1.5f * Sp : staffTop, b = perc ? bottom + 1.5f * Sp : bottom;
                    if (final)
                    {
                        g.DrawLine(thin, bx - 0.6f * Sp, t, bx - 0.6f * Sp, b);
                        using (var heavy = new Pen(Color.Black, 0.45f * Sp)) g.DrawLine(heavy, bx - 0.2f * Sp, t, bx - 0.2f * Sp, b);
                    }
                    else g.DrawLine(thin, bx, t, bx, b);
                }

                DrawVoice(g, s, v, bottom, collectHits);
                blockTop += BlockHeight(v);
            }

            // 줄 앞 세로줄 (여러 오선을 묶음)
            if (clefs.Count > 1) g.DrawLine(pen, Sp + LabelWidth, firstStaffTop, Sp + LabelWidth, lastStaffBottom);
        }
    }

    private void DrawClef(Graphics g, int v, float x, float bottom)
    {
        switch (clefs[v])
        {
            case StaffClef.Percussion:
                using (var b = new SolidBrush(Color.Black))
                {
                    g.FillRectangle(b, x + 0.8f * Sp, bottom - 1.2f * Sp, 0.35f * Sp, 2.4f * Sp);
                    g.FillRectangle(b, x + 1.6f * Sp, bottom - 1.2f * Sp, 0.35f * Sp, 2.4f * Sp);
                }
                break;
            case StaffClef.Bass:
                using (var f = new Font(SymbolFont, 4.1f * Sp, GraphicsUnit.Pixel))
                    g.DrawString("\U0001D122", f, Brushes.Black, x - 0.2f * Sp, bottom - 4.75f * Sp);
                break;
            default:
                using (var f = new Font(SymbolFont, 6.4f * Sp, GraphicsUnit.Pixel))
                    g.DrawString("\U0001D11E", f, Brushes.Black, x - 0.4f * Sp, bottom - 6.75f * Sp);
                break;
        }
    }

    private void DrawTimeSignature(Graphics g, int v, float x, float bottom)
    {
        using (var f = new Font("Times New Roman", 2.5f * Sp, FontStyle.Bold, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            if (clefs[v] == StaffClef.Percussion)
            {
                g.DrawString(score.BeatsPerMeasure.ToString(), f, Brushes.Black, new RectangleF(x, bottom - 2.2f * Sp, 2f * Sp, 2f * Sp), sf);
                g.DrawString("4", f, Brushes.Black, new RectangleF(x, bottom + 0.2f * Sp, 2f * Sp, 2f * Sp), sf);
            }
            else
            {
                g.DrawString(score.BeatsPerMeasure.ToString(), f, Brushes.Black, new RectangleF(x, bottom - 4.1f * Sp, 2f * Sp, 2f * Sp), sf);
                g.DrawString("4", f, Brushes.Black, new RectangleF(x, bottom - 2.1f * Sp, 2f * Sp, 2f * Sp), sf);
            }
        }
    }

    /// <summary>오선 위 높이: 맨 아래 줄 = 0, 칸·줄마다 1 (높은음자리표 E4, 낮은음자리표 G2 기준).</summary>
    private int StaffStep(int v, int pitch)
    {
        if (clefs[v] == StaffClef.Percussion) return 0;
        int baseNote = clefs[v] == StaffClef.Bass ? 43 : 64;
        return NoteSpelling.Diatonic(pitch) - NoteSpelling.Diatonic(baseNote);
    }

    private void DrawVoice(Graphics g, ScoreSystem s, int v, float bottom, bool collectHits)
    {
        bool perc = clefs[v] == StaffClef.Percussion;
        var drum = score.Voices[v].Drum;
        float tieFromX = float.NaN, tieFromY = 0;
        bool tieAbove = false;

        for (int mi = 0; mi + s.FirstMeasure <= s.LastMeasure; mi++)
        {
            int m = s.FirstMeasure + mi;
            if (m >= glyphs[v].Count) continue;
            var accState = new Dictionary<int, int>();
            foreach (var gl in glyphs[v][m])
            {
                float x = XAt(s, mi, gl.Start);
                if (gl.IsRest)
                {
                    DrawRest(g, gl, x, bottom, perc);
                    continue;
                }
                int step = StaffStep(v, gl.Pitch);
                float y = bottom - step * Sp / 2f;
                bool up = perc || step < 4;

                // 덧줄
                using (var ledger = new Pen(Color.Black, Math.Max(1f, 0.1f * Sp)))
                {
                    if (!perc && step <= -2) for (int k = -2; k >= step; k -= 2) g.DrawLine(ledger, x - 0.4f * Sp, bottom - k * Sp / 2f, x + 1.7f * Sp, bottom - k * Sp / 2f);
                    if (!perc && step >= 10) for (int k = 10; k <= step; k += 2) g.DrawLine(ledger, x - 0.4f * Sp, bottom - k * Sp / 2f, x + 1.7f * Sp, bottom - k * Sp / 2f);
                }

                // 임시표 (마디 안에서 같은 자리의 올림/제자리)
                if (!perc)
                {
                    int st, alter, oct;
                    NoteSpelling.Spell(gl.Pitch, out st, out alter, out oct);
                    int d = NoteSpelling.Diatonic(gl.Pitch);
                    int prev;
                    bool had = accState.TryGetValue(d, out prev);
                    if (!gl.TieStop || mi == 0 && gl.Start == 0)
                    {
                        if (alter == 1 && (!had || prev != 1)) DrawAccidental(g, "♯", x, y);
                        else if (alter == 0 && had && prev == 1) DrawAccidental(g, "♮", x, y);
                    }
                    accState[d] = alter;
                }

                // 음표
                int hit = -1;
                if (collectHits)
                {
                    hit = Hits.Count;
                    var sv = score.Voices[v];
                    Hits.Add(new ScoreHit
                    {
                        Bounds = new RectangleF(x - 0.3f * Sp, y - 0.9f * Sp, 2f * Sp, 1.8f * Sp),
                        Voice = sv.VoiceIndex, Tick = gl.AbsTick, Pitch = gl.Pitch,
                        Label = (sv.Drum != DrumKind.None ? sv.Label : NoteHelper.Name(gl.Pitch)) + "  m" + (m + 1) + " beat " + (gl.Start / Math.Max(1, score.TickBeat) + 1) + "  " + NoteSpelling.TypeName(gl.Value) + (gl.Dots > 0 ? " dotted" : ""),
                    });
                }
                var color = hit >= 0 && hit == HoverIndex ? Color.FromArgb(0, 103, 192) : gl.Exact ? Color.Black : Color.FromArgb(170, 60, 40);
                DrawNote(g, gl, x, y, up, StaffRules.XHead(drum), color);

                // 붙임줄
                float headW = 1.25f * Sp;
                if (gl.TieStop)
                {
                    float fromX = float.IsNaN(tieFromX) ? s.ContentLeft - 0.6f * Sp : tieFromX;
                    float fromY = float.IsNaN(tieFromX) ? y : tieFromY;
                    DrawTie(g, fromX, x, (fromY + y) / 2f, tieAbove);
                }
                if (gl.TieStart) { tieFromX = x + headW; tieFromY = y; tieAbove = !up; }
                else tieFromX = float.NaN;
            }
        }
        DrawTripletMarks(s, g, v, bottom, perc);
        // 다음 줄로 이어지는 붙임줄
        if (!float.IsNaN(tieFromX)) DrawTie(g, tieFromX, s.MeasureX[s.MeasureX.Count - 1] - 0.3f * Sp, tieFromY, tieAbove);
    }

    /// <summary>셋잇단음표: 박마다 묶어 오선 위에 "3"을 쓴다.</summary>
    private void DrawTripletMarks(ScoreSystem s, Graphics g, int v, float bottom, bool perc)
    {
        float top = perc ? bottom - 3.6f * Sp : bottom - 6.4f * Sp;
        using (var f = new Font("Segoe UI", 1.5f * Sp, FontStyle.Italic | FontStyle.Bold, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center })
            for (int mi = 0; mi + s.FirstMeasure <= s.LastMeasure; mi++)
            {
                int m = s.FirstMeasure + mi;
                if (m >= glyphs[v].Count) continue;
                int groupBeat = -1;
                float x1 = 0, x2 = 0;
                foreach (var gl in glyphs[v][m])
                {
                    int beat = gl.Start / Math.Max(1, score.TickBeat);
                    if (gl.Triplet && beat == groupBeat) { x2 = XAt(s, mi, gl.Start) + 1.2f * Sp; continue; }
                    if (groupBeat >= 0) g.DrawString("3", f, Brushes.DimGray, (x1 + x2) / 2, top, sf);
                    groupBeat = gl.Triplet ? beat : -1;
                    x1 = XAt(s, mi, gl.Start);
                    x2 = x1 + 1.2f * Sp;
                }
                if (groupBeat >= 0) g.DrawString("3", f, Brushes.DimGray, (x1 + x2) / 2, top, sf);
            }
    }

    private void DrawAccidental(Graphics g, string symbol, float x, float y)
    {
        using (var f = new Font(SymbolFont, 2.2f * Sp, GraphicsUnit.Pixel))
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            g.DrawString(symbol, f, Brushes.Black, new RectangleF(x - 1.6f * Sp, y - 1.5f * Sp, 1.4f * Sp, 3f * Sp), sf);
    }

    private void DrawNote(Graphics g, Glyph gl, float x, float y, bool up, bool xHead, Color color)
    {
        float w = 1.25f * Sp, h = 0.95f * Sp;
        bool filled = gl.Value >= NoteValue.Quarter;
        using (var b = new SolidBrush(color))
        using (var p = new Pen(color, Math.Max(1f, 0.14f * Sp)))
        {
            if (xHead)
            {
                g.DrawLine(p, x, y - h / 2, x + w, y + h / 2);
                g.DrawLine(p, x, y + h / 2, x + w, y - h / 2);
            }
            else
            {
                var state = g.Save();
                g.TranslateTransform(x + w / 2, y);
                g.RotateTransform(-20);
                var r = new RectangleF(-w / 2, -h / 2, w, h);
                if (filled) g.FillEllipse(b, r);
                else
                {
                    using (var hp = new Pen(color, Math.Max(1.2f, 0.2f * Sp))) g.DrawEllipse(hp, r.X + 0.1f * Sp, r.Y + 0.08f * Sp, r.Width - 0.2f * Sp, r.Height - 0.16f * Sp);
                }
                g.Restore(state);
            }

            // 점
            for (int d = 0; d < gl.Dots; d++)
            {
                float dy = Math.Abs((y / (Sp / 2f)) % 2) < 0.01f ? -Sp / 2f : 0;
                g.FillEllipse(b, x + w + 0.35f * Sp + d * 0.55f * Sp, y + dy - 0.2f * Sp, 0.4f * Sp, 0.4f * Sp);
            }

            if (gl.Value == NoteValue.Whole) return;
            // 기둥
            float stemLen = 3.4f * Sp;
            float sx = up ? x + w - 0.08f * Sp : x + 0.08f * Sp;
            float sy0 = y, sy1 = up ? y - stemLen : y + stemLen;
            using (var sp = new Pen(color, Math.Max(1f, 0.12f * Sp))) g.DrawLine(sp, sx, sy0, sx, sy1);

            // 꼬리 (8분 이하)
            int flags = (int)gl.Value - (int)NoteValue.Quarter;
            using (var fp = new Pen(color, Math.Max(1.2f, 0.28f * Sp)))
                for (int i = 0; i < flags; i++)
                {
                    float fy = up ? sy1 + i * 0.75f * Sp : sy1 - i * 0.75f * Sp;
                    float dir = up ? 1 : -1;
                    g.DrawBezier(fp, sx, fy, sx + 0.3f * Sp, fy + dir * 0.9f * Sp, sx + 1.3f * Sp, fy + dir * 1.2f * Sp, sx + 1.0f * Sp, fy + dir * 2.4f * Sp);
                }
        }
    }

    private void DrawRest(Graphics g, Glyph gl, float x, float bottom, bool perc)
    {
        float mid = perc ? bottom : bottom - 2f * Sp;   // 가운데 줄
        using (var b = new SolidBrush(Color.FromArgb(30, 30, 30)))
        using (var p = new Pen(Color.FromArgb(30, 30, 30), Math.Max(1.2f, 0.22f * Sp)))
        {
            switch (gl.Value)
            {
                case NoteValue.Whole:
                    g.FillRectangle(b, x, mid - Sp, 1.2f * Sp, 0.5f * Sp);
                    break;
                case NoteValue.Half:
                    g.FillRectangle(b, x, mid - 0.5f * Sp, 1.2f * Sp, 0.5f * Sp);
                    break;
                case NoteValue.Quarter:
                    g.DrawLines(p, new[]
                    {
                        new PointF(x + 0.3f * Sp, mid - 1.5f * Sp), new PointF(x + 0.9f * Sp, mid - 0.8f * Sp),
                        new PointF(x + 0.35f * Sp, mid - 0.1f * Sp), new PointF(x + 0.95f * Sp, mid + 0.6f * Sp),
                    });
                    g.DrawBezier(p, x + 0.95f * Sp, mid + 0.6f * Sp, x + 0.2f * Sp, mid + 0.3f * Sp, x + 0.1f * Sp, mid + 1.0f * Sp, x + 0.6f * Sp, mid + 1.4f * Sp);
                    break;
                default:
                {
                    int flags = (int)gl.Value - (int)NoteValue.Quarter;
                    float top = mid - 0.9f * Sp;
                    g.DrawLine(p, x + 1.2f * Sp, top, x + 0.5f * Sp, mid + 1.4f * Sp + (flags - 1) * 0.5f * Sp);
                    for (int i = 0; i < flags; i++)
                    {
                        float fy = top + i * 0.85f * Sp;
                        g.FillEllipse(b, x + 0.1f * Sp, fy, 0.5f * Sp, 0.5f * Sp);
                        g.DrawLine(p, x + 0.4f * Sp, fy + 0.4f * Sp, x + 1.2f * Sp - i * 0.25f * Sp, fy + 0.05f * Sp);
                    }
                    break;
                }
            }
            for (int d = 0; d < gl.Dots; d++) g.FillEllipse(b, x + 1.6f * Sp + d * 0.55f * Sp, mid - 0.7f * Sp, 0.4f * Sp, 0.4f * Sp);
        }
    }

    private void DrawTie(Graphics g, float x1, float x2, float y, bool above)
    {
        if (x2 - x1 < 0.5f * Sp) return;
        float dir = above ? -1 : 1;
        float yy = y + dir * 0.75f * Sp;
        using (var p = new Pen(Color.FromArgb(40, 40, 40), Math.Max(1f, 0.14f * Sp)))
            g.DrawBezier(p, x1, yy, x1 + (x2 - x1) * 0.25f, yy + dir * 0.9f * Sp, x1 + (x2 - x1) * 0.75f, yy + dir * 0.9f * Sp, x2, yy);
    }
}
