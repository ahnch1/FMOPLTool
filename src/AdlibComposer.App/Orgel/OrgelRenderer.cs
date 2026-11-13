using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using AdlibComposer.Notation;

namespace AdlibComposer.App.Orgel;

/// <summary>
/// 오르골 띠 그리기 (화면 · 인쇄 공용, 단위는 Graphics의 PageUnit 그대로: 화면 = 픽셀, 인쇄 = mm).
/// 시간은 왼쪽 → 오른쪽, 빗살 줄은 아래가 낮은 음(C) · 위가 높은 음(E₃). 띠에 인쇄된 음계 표시를 180도 돌린 방향.
/// 구멍은 음의 시작점에만, 줄(가로선) 위 시간 위치(세로선 기준)에 낸다.
/// </summary>
internal sealed class OrgelRenderer
{
    public static readonly Color Paper = Color.FromArgb(252, 249, 238);
    public static readonly Color PaperEdge = Color.FromArgb(120, 116, 100);
    public static readonly Color RowLine = Color.FromArgb(175, 175, 170);
    public static readonly Color RowBold = Color.FromArgb(40, 40, 40);
    public static readonly Color GridLine = Color.FromArgb(205, 205, 198);
    public static readonly Color BeatLine = Color.FromArgb(150, 150, 140);
    public static readonly Color MeasureLine = Color.FromArgb(60, 60, 60);
    public static readonly Color Hole = Color.FromArgb(20, 20, 20);
    public static readonly Color FoldedHole = Color.FromArgb(30, 90, 200);
    public static readonly Color CloseRing = Color.FromArgb(235, 130, 0);
    public static readonly Color MissingMark = Color.FromArgb(210, 30, 30);

    /// <summary>줄 간격 (화면 px 또는 mm).</summary>
    public float RowPitch = 14f;
    /// <summary>격자 한 칸(GridTicks)의 너비.</summary>
    public float GridWidth = 56f;
    public int GridTicks = 24;
    /// <summary>음계 표시 칸 너비.</summary>
    public float LabelWidth = 64f;
    public float LineScale = 1f;
    /// <summary>위 · 아래 띠 여백 (줄 간격 배수).</summary>
    public const float EdgeRows = 1.6f;

    public float UnitsPerTick { get { return GridWidth / Math.Max(1, GridTicks); } }
    /// <summary>띠 높이 (위 가장자리 ~ 아래 가장자리).</summary>
    public float PaperHeight { get { return (OrgelComb.Rows - 1 + 2 * EdgeRows) * RowPitch; } }

    public float RowY(float paperTop, int row) { return paperTop + (EdgeRows + OrgelComb.Rows - 1 - row) * RowPitch; }

    /// <summary>
    /// 띠 한 구간: [tickFrom, tickTo)를 x0(= tickFrom 위치)부터 그린다. 음계 표시는 따로 (DrawLabels).
    /// cursorTick ≥ 0이면 재생 위치 선, hover는 강조할 구멍.
    /// </summary>
    public void DrawStrip(Graphics g, OrgelStrip strip, float x0, float paperTop, double tickFrom, double tickTo,
                          double cursorTick = -1, OrgelPunch hover = null, bool showMeasureNumbers = true)
    {
        float upt = UnitsPerTick;
        float width = (float)((tickTo - tickFrom) * upt);
        float bottom = paperTop + PaperHeight;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var b = new SolidBrush(Paper)) g.FillRectangle(b, x0, paperTop, width, PaperHeight);

        int tb = Math.Max(1, strip.TickBeat), measure = tb * Math.Max(1, strip.BeatsPerMeasure);
        int grid = Math.Max(1, GridTicks);
        // 세로선: 격자 · 박 · 마디
        using (var pg = new Pen(GridLine, 0.6f * LineScale))
        using (var pb = new Pen(BeatLine, 0.8f * LineScale))
        using (var pm = new Pen(MeasureLine, 1.2f * LineScale))
        using (var font = new Font("Arial", RowPitch * 0.75f, FontStyle.Bold, GraphicsUnit.World))
        using (var numBrush = new SolidBrush(Color.FromArgb(90, 90, 90)))
        {
            long first = (long)Math.Ceiling(tickFrom / grid) * grid;
            for (long t = first; t < tickTo; t += grid)
            {
                float x = x0 + (float)((t - tickFrom) * upt);
                var pen = t % measure == 0 ? pm : t % tb == 0 ? pb : pg;
                g.DrawLine(pen, x, paperTop, x, bottom);
                if (showMeasureNumbers && t % measure == 0)
                    g.DrawString((t / measure + 1).ToString(), font, numBrush, x + RowPitch * 0.15f, paperTop - RowPitch * 1.05f);
            }
            // 마디선이 격자에 안 맞을 때도 (격자가 박보다 클 때)
            long mfirst = (long)Math.Ceiling(tickFrom / measure) * measure;
            for (long t = mfirst; t < tickTo; t += measure)
            {
                if (t % grid == 0) continue;
                float x = x0 + (float)((t - tickFrom) * upt);
                g.DrawLine(pm, x, paperTop, x, bottom);
                if (showMeasureNumbers) g.DrawString((t / measure + 1).ToString(), font, numBrush, x + RowPitch * 0.15f, paperTop - RowPitch * 1.05f);
            }
        }
        // 가로선: 빗살 줄 (띠처럼 6줄마다 굵게)
        using (var pr = new Pen(RowLine, 0.6f * LineScale))
        using (var pbold = new Pen(RowBold, 1.1f * LineScale))
        {
            for (int r = 0; r < OrgelComb.Rows; r++)
            {
                float y = RowY(paperTop, r);
                g.DrawLine(r % 6 == 5 ? pbold : pr, x0, y, x0 + width, y);
            }
        }
        using (var pe = new Pen(PaperEdge, 1f * LineScale)) { g.DrawLine(pe, x0, paperTop, x0 + width, paperTop); g.DrawLine(pe, x0, bottom, x0 + width, bottom); }

        // 구멍
        float d = RowPitch * 0.78f;
        using (var hole = new SolidBrush(Hole))
        using (var folded = new SolidBrush(FoldedHole))
        using (var ring = new Pen(CloseRing, Math.Max(1.2f * LineScale, RowPitch * 0.14f)))
        using (var hov = new Pen(Color.FromArgb(0, 150, 255), Math.Max(1.5f * LineScale, RowPitch * 0.16f)))
        {
            foreach (var p in strip.Punches)
            {
                if (p.Tick < tickFrom || p.Tick >= tickTo) continue;   // 구간마다 정확히 한 번 (인쇄 구간이 겹치지 않게)
                float x = x0 + (float)((p.Tick - tickFrom) * upt), y = RowY(paperTop, p.Row);
                g.FillEllipse(p.Folded ? folded : hole, x - d / 2, y - d / 2, d, d);
                if (p.TooClose) g.DrawEllipse(ring, x - d * 0.8f, y - d * 0.8f, d * 1.6f, d * 1.6f);
                if (ReferenceEquals(p, hover)) g.DrawEllipse(hov, x - d * 0.85f, y - d * 0.85f, d * 1.7f, d * 1.7f);
            }
        }
        // 못 내는 음: 띠 아래에 빨간 ×
        using (var mp = new Pen(MissingMark, Math.Max(1.4f * LineScale, RowPitch * 0.14f)))
        {
            float s = RowPitch * 0.35f, my = bottom + RowPitch * 0.75f;
            foreach (var m in strip.Missing)
            {
                if (m.Tick < tickFrom || m.Tick >= tickTo) continue;
                float x = x0 + (float)((m.Tick - tickFrom) * upt);
                g.DrawLine(mp, x - s, my - s, x + s, my + s);
                g.DrawLine(mp, x - s, my + s, x + s, my - s);
            }
        }
        if (cursorTick >= 0 && cursorTick >= tickFrom && cursorTick < tickTo)   // 음수 = 재생 중 아님
        {
            float x = x0 + (float)((cursorTick - tickFrom) * upt);
            using (var pc = new Pen(Color.FromArgb(220, 30, 30), 2f * LineScale)) g.DrawLine(pc, x, paperTop - RowPitch * 0.4f, x, bottom + RowPitch * 0.4f);
        }
    }

    /// <summary>음계 표시: 띠처럼 두 칸에 엇갈려 (짝수 줄 왼쪽 칸, 홀수 줄 오른쪽 칸), ♯은 위, 옥타브 숫자는 아래 작게.</summary>
    public void DrawLabels(Graphics g, float x, float paperTop)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
        using (var b = new SolidBrush(Paper)) g.FillRectangle(b, x, paperTop, LabelWidth, PaperHeight);
        using (var pe = new Pen(PaperEdge, 1f * LineScale))
        {
            g.DrawLine(pe, x, paperTop, x + LabelWidth, paperTop);
            g.DrawLine(pe, x, paperTop + PaperHeight, x + LabelWidth, paperTop + PaperHeight);
            g.DrawLine(pe, x + LabelWidth - 0.5f, paperTop, x + LabelWidth - 0.5f, paperTop + PaperHeight);
        }
        using (var big = new Font("Arial", RowPitch * 1.05f, FontStyle.Bold, GraphicsUnit.World))
        using (var small = new Font("Arial", RowPitch * 0.62f, FontStyle.Bold, GraphicsUnit.World))
        using (var ink = new SolidBrush(Color.FromArgb(25, 25, 25)))
        using (var pr = new Pen(RowLine, 0.6f * LineScale))
        {
            float colA = x + LabelWidth * 0.12f, colB = x + LabelWidth * 0.52f;
            for (int r = 0; r < OrgelComb.Rows; r++)
            {
                float y = RowY(paperTop, r);
                string name, oct;
                OrgelComb.LabelParts(r, out name, out oct);
                bool sharp = name.Length > 1;
                string letter = name.Substring(0, 1);
                float cx = r % 2 == 0 ? colA : colB;
                g.DrawLine(pr, cx + RowPitch * 1.6f, y, x + LabelWidth, y);
                var sz = g.MeasureString(letter, big);
                float ty = y - sz.Height / 2;
                g.DrawString(letter, big, ink, cx, ty);
                float rx = cx + sz.Width * 0.78f;
                if (sharp) g.DrawString("#", small, ink, rx, ty - RowPitch * 0.12f);
                if (oct.Length > 0) g.DrawString(oct, small, ink, rx, ty + sz.Height * 0.42f);
            }
        }
    }
}
