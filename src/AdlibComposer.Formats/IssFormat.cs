using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>ISS 강조 기록: tick이 되면 line 줄의 [StartX, StartX + Width) 칸을 칠한다.</summary>
public struct IssRecord
{
    /// <summary>원본 곡 틱 (IMS 240틱/박 기준, 파일 값 × 8 또는 옛 형식 × 10).</summary>
    public int Tick;
    public int Line;
    public int StartX;
    public int Width;
}

/// <summary>가사 한 줄 (64바이트 고정, 칸 단위 글자).</summary>
public sealed class IssLine
{
    public string Text = "";
    public List<TextCell> Cells = new List<TextCell>();
    /// <summary>줄의 칸 길이 (첫 NUL까지).</summary>
    public int Length;
}

/// <summary>
/// ISS (IMPlay / 이야기 가사 파일). 같은 이름의 IMS와 짝을 이룬다.
/// 구조: 헤더 154바이트 (headStr[20] reserved[10] writer[30] composer[30] singer[30] editor[30] recCount u16 lineCount u16)
///       + 기록 recCount × 5바이트 (tick u16, line u8, startX u8, widthX u8)
///       + 가사 줄 lineCount × 64바이트.
/// headStr에 "IMPlay Song V"가 있으면 tick = 곡 틱 / 8, 없으면 옛 형식으로 곡 틱 / 10.
/// 글자는 조합형 한글 (완성형으로 바꾼 파일도 자동 판별).
/// </summary>
public sealed class IssLyrics
{
    public const int HeaderSize = 154;
    public const int RecordSize = 5;
    public const int LineSize = 64;
    /// <summary>ISS 틱의 기준 박당 틱 (IMS 헤더 tickBeat, 사실상 항상 240).</summary>
    public const int BaseTickBeat = 240;

    public string Version = "";
    public bool IsV2;
    public string Writer = "", Composer = "", Singer = "", Editor = "";
    public KoreanText.Kind Encoding;
    public List<IssRecord> Records { get; } = new List<IssRecord>();
    public List<IssLine> Lines { get; } = new List<IssLine>();

    public static IssLyrics Load(string path)
    {
        return Read(File.ReadAllBytes(path));
    }

    public static IssLyrics Read(byte[] b)
    {
        if (b.Length < HeaderSize) throw new InvalidDataException("ISS 헤더가 잘렸습니다.");
        var iss = new IssLyrics();
        string head = System.Text.Encoding.ASCII.GetString(b, 0, 20);
        iss.IsV2 = head.Contains("IMPlay Song V");
        iss.Version = iss.IsV2 ? head.TrimEnd('\0', ' ') : "";
        int recCount = BitConverter.ToUInt16(b, 150);
        int lineCount = BitConverter.ToUInt16(b, 152);
        int recBase = HeaderSize, lineBase = HeaderSize + recCount * RecordSize;
        // 잘린 파일은 들어 있는 만큼만
        recCount = Math.Max(0, Math.Min(recCount, (b.Length - recBase) / RecordSize));
        lineCount = Math.Max(0, Math.Min(lineCount, (b.Length - lineBase) / LineSize));

        // 인코딩은 가사 전체를 보고 한 번에 고른다
        var all = new List<byte>();
        for (int l = 0; l < lineCount; l++)
            for (int i = 0; i < LineSize; i++)
            {
                byte c = b[lineBase + l * LineSize + i];
                if (c == 0) break;
                all.Add(c);
            }
        for (int f = 0; f < 4; f++) for (int i = 0; i < 30 && b[30 + f * 30 + i] != 0; i++) all.Add(b[30 + f * 30 + i]);
        var arr = all.ToArray();
        iss.Encoding = KoreanText.Detect(arr, 0, arr.Length);
        if (iss.Encoding == KoreanText.Kind.Ascii) iss.Encoding = KoreanText.Kind.Johab;

        iss.Writer = KoreanText.DecodeField(b, 30, 30, iss.Encoding);
        iss.Composer = KoreanText.DecodeField(b, 60, 30, iss.Encoding);
        iss.Singer = KoreanText.DecodeField(b, 90, 30, iss.Encoding);
        iss.Editor = KoreanText.DecodeField(b, 120, 30, iss.Encoding);

        for (int r = 0; r < recCount; r++)
        {
            int o = recBase + r * RecordSize;
            int t = BitConverter.ToUInt16(b, o);
            if (!iss.IsV2) t = t * 10 / 8;   // 옛 형식: 1/10 단위 → IMPLAY와 같은 정수 변환
            iss.Records.Add(new IssRecord { Tick = t * 8, Line = b[o + 2], StartX = b[o + 3], Width = b[o + 4] });
        }
        for (int l = 0; l < lineCount; l++)
        {
            int o = lineBase + l * LineSize;
            int n = 0;
            while (n < LineSize && b[o + n] != 0) n++;
            var line = new IssLine { Length = n, Cells = KoreanText.DecodeCells(b, o, n, iss.Encoding) };
            var sb = new StringBuilder();
            foreach (var c in line.Cells) sb.Append(c.Text);
            line.Text = sb.ToString();
            iss.Lines.Add(line);
        }
        return iss;
    }

    /// <summary>가사 틱(240/박) → 곡 틱.</summary>
    public static double ToSongTick(int issTick, int songTickBeat, int fileTickBeat)
    {
        int src = fileTickBeat > 0 ? fileTickBeat : BaseTickBeat;
        return (double)issTick * songTickBeat / src;
    }

    /// <summary>
    /// 곡 위치에서 보여 줄 상태를 IMPLAY 규칙대로 계산한다: 기록은 파일 순서대로 칠하고,
    /// 다른 줄이거나 앞 기록이 끝난 곳보다 왼쪽에서 시작하면 줄을 지우고 다시 칠한다.
    /// 칠한 칸은 lit[칸] = true. 아직 첫 기록 전이면 첫 줄을 미리 (칠하지 않고) 보여 준다.
    /// </summary>
    public LyricState StateAt(double songTick, int songTickBeat, int fileTickBeat)
    {
        var st = new LyricState { Line = -1, NextLine = -1, Lit = new bool[LineSize] };
        int lastEnd = 0, idx = 0;
        for (; idx < Records.Count; idx++)
        {
            var r = Records[idx];
            if (ToSongTick(r.Tick, songTickBeat, fileTickBeat) > songTick) break;
            if (r.Line >= Lines.Count) continue;
            if (r.Line != st.Line || r.StartX < lastEnd)
            {
                st.Line = r.Line;
                Array.Clear(st.Lit, 0, st.Lit.Length);
                lastEnd = 0;
            }
            lastEnd = Paint(Lines[r.Line], r, st.Lit, lastEnd);
        }
        // 다음에 나올 다른 줄
        for (int k = idx; k < Records.Count; k++)
        {
            int l = Records[k].Line;
            if (l < Lines.Count && l != st.Line) { st.NextLine = l; break; }
        }
        if (st.Line < 0 && st.NextLine >= 0) { st.Line = st.NextLine; st.NextLine = NextAfter(idx, st.Line); }
        return st;
    }

    private int NextAfter(int from, int line)
    {
        for (int k = from; k < Records.Count; k++)
        {
            int l = Records[k].Line;
            if (l < Lines.Count && l != line) return l;
        }
        return -1;
    }

    private static int Paint(IssLine line, IssRecord r, bool[] lit, int lastEnd)
    {
        int start = r.StartX, end = Math.Min(line.Length, r.StartX + r.Width);
        if (start >= end) return Math.Max(lastEnd, Math.Min(end, LineSize));
        // 2바이트 글자의 둘째 칸에서 시작하면 한 칸 뒤로
        foreach (var c in line.Cells)
            if (c.Width == 2 && start == c.Column + 1) { start++; break; }
        // 앞쪽 공백은 칠하지 않는다
        foreach (var c in line.Cells)
            if (c.Column >= start && c.Column < end)
            {
                if (c.Text == " ") { start = c.Column + 1; continue; }
                break;
            }
        for (int x = start; x < end && x < LineSize; x++) lit[x] = true;
        return end;
    }
}

/// <summary>가사 표시 상태: 지금 줄, 칠한 칸, 다음 줄.</summary>
public sealed class LyricState
{
    public int Line;
    public int NextLine;
    public bool[] Lit;
}
