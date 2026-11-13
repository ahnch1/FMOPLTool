using System;
using System.Collections.Generic;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>디코딩한 글자 하나와 원래 바이트 수(화면 칸 수: 1바이트 = 1칸, 2바이트 = 2칸).</summary>
public struct TextCell
{
    public string Text;
    public int Column;   // 줄 안의 시작 칸(바이트 위치)
    public int Width;    // 1 또는 2
}

/// <summary>
/// 옛 DOS 한글 문자열 → 유니코드. IMS 제목 / ISS 가사는 KS C 5601-1992 조합형이 원칙이지만
/// 완성형(EUC-KR / CP949)으로 바꿔 둔 파일도 있어 자동으로 구별한다.
/// 조합형 한글 영역은 초·중·종성 비트 계산으로 직접 풀고, 기호·한자 영역은 Windows 조합형 코드페이지(1361)를 쓴다.
/// 이야기(Iyagi) 사용자 글꼴 영역(0xD480–0xD4FF)은 CP437 그림 글자로 바꾼다.
/// </summary>
public static class KoreanText
{
    public enum Kind { Ascii, Johab, Wansung }

    private const string Initials = "ㄱㄲㄴㄷㄸㄹㅁㅂㅃㅅㅆㅇㅈㅉㅊㅋㅌㅍㅎ";
    private const string Medials = "ㅏㅐㅑㅒㅓㅔㅕㅖㅗㅘㅙㅚㅛㅜㅝㅞㅟㅠㅡㅢㅣ";
    private const string Finals = "ㄱㄲㄳㄴㄵㄶㄷㄹㄺㄻㄼㄽㄾㄿㅀㅁㅂㅄㅅㅆㅇㅈㅊㅋㅌㅍㅎ";

    // CP437 0x00–0x1F 그림 글자 (0x00은 이야기의 소 그림 대신 ☺와 비슷한 자리표시)
    private const string Cp437Low = "☺☺☻♥♦♣♠•◘○◙♂♀♪♫☼►◄↕‼¶§▬↨↑↓→←∟↔▲▼";

    private static readonly Encoding johabCodec = TryGet(1361);
    private static readonly Encoding wansungCodec = TryGet(949);
    private static readonly Encoding cp437 = TryGet(437);

    private static Encoding TryGet(int codePage)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception) { return null; }
    }

    /// <summary>문자열 → 완성형(CP949) 바이트. 바꿀 수 없는 글자는 '?'. 코드페이지가 없으면 ASCII.</summary>
    public static byte[] Encode(string s)
    {
        s = s ?? "";
        if (wansungCodec != null)
        {
            try { return Encoding.GetEncoding(949, new EncoderReplacementFallback("?"), DecoderFallback.ReplacementFallback).GetBytes(s); }
            catch (Exception) { }
        }
        return Encoding.ASCII.GetBytes(s);
    }

    /// <summary>바이트 → 문자열 (조합형/완성형 자동 판별).</summary>
    public static string Decode(byte[] b, int offset, int count)
    {
        var sb = new StringBuilder();
        foreach (var c in DecodeCells(b, offset, count, Detect(b, offset, count))) sb.Append(c.Text);
        return sb.ToString();
    }

    /// <summary>NUL에서 끝나는 고정 길이 필드.</summary>
    public static string DecodeField(byte[] b, int offset, int length, Kind kind)
    {
        int n = 0;
        while (n < length && offset + n < b.Length && b[offset + n] != 0) n++;
        var sb = new StringBuilder();
        foreach (var c in DecodeCells(b, offset, n, kind)) sb.Append(c.Text);
        return sb.ToString().TrimEnd();
    }

    /// <summary>여러 조각을 한꺼번에 보고 인코딩을 고른다 (가사 파일 전체 등).</summary>
    public static Kind Detect(byte[] b, int offset, int count)
    {
        bool high = false;
        for (int i = offset; i < offset + count && i < b.Length; i++) if (b[i] >= 0x80) { high = true; break; }
        if (!high) return Kind.Ascii;

        // 완성형 후보: 두 바이트가 모두 0xA1–0xFE (KS X 1001)
        bool wansungOk = true;
        for (int i = offset; i < offset + count && i < b.Length; i++)
        {
            if (b[i] == 0) continue;
            if (b[i] < 0x80) continue;
            if (i + 1 >= offset + count || b[i] < 0xA1 || b[i] > 0xFE || b[i + 1] < 0xA1 || b[i + 1] > 0xFE) { wansungOk = false; break; }
            i++;
        }
        if (!wansungOk) return Kind.Johab;

        // 둘 다 가능: 조합형으로 풀었을 때 흔한 글자(KS X 1001의 2350자)가 대부분이면 조합형
        int hangul = 0, common = 0, bad = 0;
        foreach (var c in DecodeCells(b, offset, count, Kind.Johab))
        {
            if (c.Width != 2) continue;
            char ch = c.Text.Length == 1 ? c.Text[0] : '\0';
            if (ch >= 0xAC00 && ch <= 0xD7A3)
            {
                hangul++;
                if (IsCommonSyllable(ch)) common++;
            }
            else if (ch == '�') bad++;
        }
        if (hangul == 0) return bad > 0 ? Kind.Wansung : Kind.Johab;
        return common * 10 >= hangul * 9 && bad == 0 ? Kind.Johab : Kind.Wansung;
    }

    private static bool IsCommonSyllable(char ch)
    {
        if (wansungCodec == null) return true;
        try
        {
            var bytes = wansungCodec.GetBytes(ch.ToString());
            return bytes.Length == 2 && bytes[0] >= 0xB0 && bytes[0] <= 0xC8 && bytes[1] >= 0xA1;
        }
        catch (Exception) { return false; }
    }

    /// <summary>글자마다 칸 위치를 함께 돌려준다 (ISS 강조 기록은 칸 단위).</summary>
    public static List<TextCell> DecodeCells(byte[] b, int offset, int count, Kind kind)
    {
        var list = new List<TextCell>();
        int end = Math.Min(b.Length, offset + count);
        for (int i = offset; i < end; i++)
        {
            int col = i - offset;
            byte c = b[i];
            if (c < 0x80 || kind == Kind.Ascii)
            {
                list.Add(new TextCell { Text = c < 0x20 ? " " : c < 0x80 ? ((char)c).ToString() : "�", Column = col, Width = 1 });
                continue;
            }
            if (i + 1 >= end)
            {
                list.Add(new TextCell { Text = "�", Column = col, Width = 1 });
                continue;
            }
            string s = kind == Kind.Wansung ? DecodeWansung(c, b[i + 1]) : DecodeJohab(c, b[i + 1]);
            list.Add(new TextCell { Text = s, Column = col, Width = 2 });
            i++;
        }
        return list;
    }

    private static string DecodeWansung(byte lead, byte trail)
    {
        if (wansungCodec != null)
            try { return wansungCodec.GetString(new[] { lead, trail }); } catch (Exception) { }
        return "�";
    }

    /// <summary>조합형 2바이트 한 글자.</summary>
    public static string DecodeJohab(byte lead, byte trail)
    {
        int code = (lead << 8) | trail;
        if (lead >= 0x84 && lead <= 0xD3)
        {
            int ci = (code >> 10) & 0x1F, mi = (code >> 5) & 0x1F, fi = code & 0x1F;
            int cho = ci >= 2 && ci <= 20 ? ci - 2 : ci == 1 ? -1 : -2;
            int jung = MedialIndex(mi);
            int jong = FinalIndex(fi);
            if (cho == -2 || jung == -2 || jong == -2) return "�";
            if (cho >= 0 && jung >= 0) return ((char)(0xAC00 + cho * 588 + jung * 28 + Math.Max(0, jong))).ToString();
            if (cho >= 0 && jung == -1 && jong <= 0) return Initials[cho].ToString();
            if (cho == -1 && jung >= 0 && jong <= 0) return Medials[jung].ToString();
            if (cho == -1 && jung == -1 && jong > 0) return Finals[jong - 1].ToString();
            if (cho == -1 && jung == -1 && jong <= 0) return "　";
            return "�";
        }
        if (lead == 0xD4 && trail >= 0x80)
        {
            int glyph = trail - 0x80;
            if (glyph < 0x20) return Cp437Low[glyph].ToString();
            if (glyph == 0x7F) return "⌂";
            return ((char)glyph).ToString();
        }
        if (johabCodec != null)
            try { return johabCodec.GetString(new[] { lead, trail }); } catch (Exception) { }
        return "�";
    }

    // 중성 칸: 2 = 채움, 3–7, 10–15, 18–23, 26–29 = ㅏ…ㅣ
    private static int MedialIndex(int slot)
    {
        if (slot == 2) return -1;
        if (slot >= 3 && slot <= 7) return slot - 3;
        if (slot >= 10 && slot <= 15) return slot - 10 + 5;
        if (slot >= 18 && slot <= 23) return slot - 18 + 11;
        if (slot >= 26 && slot <= 29) return slot - 26 + 17;
        return -2;
    }

    // 종성 칸: 1 = 없음(0), 2–17 = 1–16, 19–29 = 17–27
    private static int FinalIndex(int slot)
    {
        if (slot == 1) return 0;
        if (slot >= 2 && slot <= 17) return slot - 1;
        if (slot >= 19 && slot <= 29) return slot - 2;
        return -2;
    }
}
