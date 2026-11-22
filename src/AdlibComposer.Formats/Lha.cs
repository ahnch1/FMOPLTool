using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// LHA / LZH 압축 (LHarc): 머리말 단계 0 · 1 · 2, 방식 -lh0- · -lz4- (그대로) · -lh4- · -lh5- · -lh6- · -lh7- (LZ77 + 정적 허프만).
/// YM (아타리 ST) · VTX (ZX, 머리말 없는 -lh5- 흐름) 곡과 .lzh / .lha 압축 폴더에 쓴다. 외부 라이브러리 없이 직접 쓴 것 (방식은 LHA 명세 · STSound LzhLib과 같음).
/// <para>한 블록: 블록 크기 16비트 → 길이 부호 표 (NT = 19, 5비트, 셋째 뒤 0 건너뜀) → 문자·길이 부호 길이 (NC = 510, 9비트) → 위치 부호 표 (NP = 사전 비트 + 1, 4 · 5비트).
/// 문자·길이 부호 c &lt; 256 = 바이트, 아니면 길이 c − 253, 위치 = 위치 부호 j (j &gt; 1이면 2^(j−1) + j−1비트) + 1 만큼 뒤에서 베낀다. 허프만 부호는 길이 순 · 기호 순 정규 부호.</para>
/// </summary>
public static class Lha
{
    public static readonly string[] Extensions = { ".lzh", ".lha" };

    public sealed class Entry
    {
        public string Name = "";
        public string Method = "";
        public int DataOffset;
        public long PackedSize, OriginalSize;
        public DateTime Time;
        /// <summary>풀린 내용의 CRC-16 (머리말에 적힌 것, IBM 0xA001).</summary>
        public int Crc;
        public bool IsDirectory { get { return Method == "-lhd-"; } }
    }

    public static bool IsLha(string path)
    {
        string ext = Path.GetExtension(path ?? "");
        foreach (var x in Extensions) if (string.Equals(x, ext, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>바이트가 LHA 머리말로 시작하는가 (2 · 3 자리의 "-l?? -").</summary>
    public static bool LooksLikeLha(byte[] d, int at = 0)
    {
        return d.Length >= at + 22 && d[at + 2] == '-' && d[at + 3] == 'l' && d[at + 6] == '-';
    }

    // ------------------------------------------------------------ 압축 파일 머리말

    public static List<Entry> ReadEntries(byte[] d)
    {
        var list = new List<Entry>();
        int p = 0;
        while (p + 22 <= d.Length && d[p] != 0)
        {
            var e = ReadHeader(d, p, out int next);
            if (e == null) break;
            list.Add(e);
            if (next <= p) break;
            p = next;
        }
        return list;
    }

    private static Entry ReadHeader(byte[] d, int p, out int next)
    {
        next = -1;
        if (!LooksLikeLha(d, p)) return null;
        int level = d[p + 20];
        var e = new Entry { Method = Encoding.ASCII.GetString(d, p + 2, 5), PackedSize = U32(d, p + 7), OriginalSize = U32(d, p + 11) };
        string dir = "";
        if (level == 0 || level == 1)
        {
            int hsize = d[p];
            int nameLen = d[p + 21];
            if (p + 22 + nameLen > d.Length) return null;
            e.Name = Decode(d, p + 22, nameLen);
            e.Time = DosTime(U32(d, p + 15));
            e.Crc = U16(d, p + 22 + nameLen);
            int at = p + 2 + hsize;   // 기본 머리말 끝
            if (level == 1)
            {
                // 확장 머리말: 마지막 2바이트 (기본 머리말 안)가 첫 확장 크기, 확장 크기들은 압축 크기에 들어 있다
                int extSize = U16(d, at - 2);
                long extTotal = 0;
                while (extSize > 0 && at + extSize <= d.Length)
                {
                    ReadExt(d, at, extSize, ref e.Name, ref dir);
                    extTotal += extSize;
                    at += extSize;
                    extSize = U16(d, at - 2);
                }
                e.PackedSize -= extTotal;
            }
            e.DataOffset = at;
        }
        else if (level == 2)
        {
            int total = U16(d, p);
            e.Time = DateTimeOffset.FromUnixTimeSeconds(U32(d, p + 15)).LocalDateTime;
            e.Crc = U16(d, p + 21);
            int at = p + 24;
            int extSize = U16(d, at);
            at += 2;
            while (extSize > 0 && at + extSize - 2 <= d.Length && at < p + total)
            {
                ReadExt(d, at - 2, extSize, ref e.Name, ref dir, true);
                at += extSize;
                extSize = U16(d, at - 2);
            }
            e.DataOffset = p + total;
        }
        else return null;
        if (dir.Length > 0) e.Name = dir + (e.Name.Length > 0 ? "/" + e.Name : "");
        e.Name = e.Name.Replace('\\', '/');
        next = e.DataOffset + (int)Math.Max(0, e.PackedSize);
        return e;
    }

    /// <summary>확장 머리말 하나: [크기 2] [종류 1] [자료] (단계 1은 크기가 앞 머리말 끝에, 여기 at = 종류 앞 2바이트 자리).</summary>
    private static void ReadExt(byte[] d, int at, int size, ref string name, ref string dir, bool level2 = false)
    {
        int type = d[at + 2 - (level2 ? 0 : 2)];
        int dataAt = at + 3 - (level2 ? 0 : 2);
        int len = size - 3;
        if (len < 0 || dataAt + len > d.Length) return;
        if (type == 0x01) name = Decode(d, dataAt, len);
        else if (type == 0x02) dir = Decode(d, dataAt, len).TrimEnd('/');
    }

    /// <summary>이름 바이트 → 글 (FFh = 폴더 나눔 '/', UTF-8이 아니면 Shift-JIS).</summary>
    private static string Decode(byte[] d, int at, int n)
    {
        var b = new byte[Math.Max(0, Math.Min(n, d.Length - at))];
        Array.Copy(d, at, b, 0, b.Length);
        for (int i = 0; i < b.Length; i++) if (b[i] == 0xFF) b[i] = (byte)'/';
        try { return new UTF8Encoding(false, true).GetString(b); }
        catch (DecoderFallbackException) { return KoreanText.ShiftJis.GetString(b); }
    }

    private static DateTime DosTime(long v)
    {
        try
        {
            int t = (int)(v & 0xFFFF), dt = (int)(v >> 16);
            return new DateTime(1980 + (dt >> 9), Math.Max(1, (dt >> 5) & 15), Math.Max(1, dt & 31), (t >> 11) % 24, ((t >> 5) & 63) % 60, ((t & 31) * 2) % 60);
        }
        catch (Exception) { return DateTime.MinValue; }
    }

    public static byte[] Extract(byte[] archive, Entry e)
    {
        int packed = (int)Math.Min(e.PackedSize, archive.Length - e.DataOffset);
        return Decompress(archive, e.DataOffset, packed, (int)e.OriginalSize, e.Method);
    }

    /// <summary>머리말 하나짜리 LHA (YM 파일)면 첫 항목을 풀어 주고, 아니면 그대로.</summary>
    public static byte[] UnpackSingle(byte[] d)
    {
        if (!LooksLikeLha(d)) return d;
        var e = ReadHeader(d, 0, out _);
        return e == null ? d : Extract(d, e);
    }

    // ------------------------------------------------------------ 풀기

    public static byte[] Decompress(byte[] src, int at, int packedSize, int originalSize, string method)
    {
        switch (method)
        {
            case "-lh0-":
            case "-lz4-":
            {
                var o = new byte[Math.Min(originalSize, packedSize)];
                Array.Copy(src, at, o, 0, o.Length);
                return o;
            }
            case "-lh4-": return new Decoder(src, at, packedSize, 12, 4).Run(originalSize);
            case "-lh5-": return new Decoder(src, at, packedSize, 13, 4).Run(originalSize);
            case "-lh6-": return new Decoder(src, at, packedSize, 15, 5).Run(originalSize);
            case "-lh7-": return new Decoder(src, at, packedSize, 16, 5).Run(originalSize);
            default: throw new InvalidDataException("LHA 방식 " + method + "은 풀지 못합니다 (-lh0- · -lh4- ~ -lh7-만).");
        }
    }

    private sealed class Huffman
    {
        private readonly int[] count = new int[17], first = new int[17], offset = new int[17];
        private readonly int[] symbols;
        private readonly int single = -1;

        public Huffman(byte[] lengths, int n)
        {
            symbols = new int[n];
            for (int i = 0; i < n; i++) count[lengths[i]]++;
            count[0] = 0;
            int code = 0, k = 0;
            for (int len = 1; len <= 16; len++)
            {
                first[len] = code;
                offset[len] = k;
                for (int s = 0; s < n; s++) if (lengths[s] == len) symbols[k++] = s;
                code = (code + count[len]) << 1;
            }
            if (k == 0) single = 0;
        }

        public Huffman(int onlySymbol) { symbols = new int[0]; single = onlySymbol; }

        public int Decode(Decoder r)
        {
            if (single >= 0) return single;
            int code = 0;
            for (int len = 1; len <= 16; len++)
            {
                code = (code << 1) | r.Bit();
                int idx = code - first[len];
                if (idx >= 0 && idx < count[len]) return symbols[offset[len] + idx];
            }
            throw new InvalidDataException("LHA 허프만 부호가 깨졌습니다.");
        }
    }

    private sealed class Decoder
    {
        private const int NC = 510, NT = 19, TBIT = 5, CBIT = 9;
        private readonly byte[] src;
        private int pos;
        private readonly int end;
        private readonly int dicBits, pBits, np;
        private int bitBuf, bitCount;

        public Decoder(byte[] src, int at, int len, int dicBits, int pBits)
        {
            this.src = src; pos = at; end = Math.Min(src.Length, at + len);
            this.dicBits = dicBits; this.pBits = pBits; np = dicBits + 1;
        }

        public int Bit()
        {
            if (bitCount == 0) { bitBuf = pos < end ? src[pos++] : 0; bitCount = 8; }
            bitCount--;
            return (bitBuf >> bitCount) & 1;
        }

        private int Bits(int n)
        {
            int v = 0;
            for (int i = 0; i < n; i++) v = (v << 1) | Bit();
            return v;
        }

        private Huffman ReadPtLen(int nn, int nbit, int special)
        {
            int n = Bits(nbit);
            if (n == 0) return new Huffman(Bits(nbit));
            var len = new byte[Math.Max(nn, n)];
            int i = 0;
            while (i < n && i < len.Length)
            {
                int c = Bits(3);
                if (c == 7) while (Bit() == 1) c++;
                len[i++] = (byte)Math.Min(c, 16);
                if (i == special)
                {
                    int z = Bits(2);
                    while (z-- > 0 && i < len.Length) len[i++] = 0;
                }
            }
            return new Huffman(len, nn);
        }

        private Huffman ReadCLen(Huffman pt)
        {
            int n = Bits(CBIT);
            if (n == 0) return new Huffman(Bits(CBIT));
            var len = new byte[NC];
            int i = 0;
            while (i < n && i < NC)
            {
                int c = pt.Decode(this);
                if (c <= 2)
                {
                    int z = c == 0 ? 1 : c == 1 ? Bits(4) + 3 : Bits(CBIT) + 20;
                    while (z-- > 0 && i < NC) len[i++] = 0;
                }
                else len[i++] = (byte)Math.Min(c - 2, 16);
            }
            return new Huffman(len, NC);
        }

        public byte[] Run(int size)
        {
            var o = new byte[size];
            int op = 0, blockLeft = 0;
            Huffman c = null, p = null;
            while (op < size)
            {
                if (blockLeft == 0)
                {
                    if (pos >= end && bitCount == 0) throw new InvalidDataException("LHA 자료가 모자랍니다.");
                    blockLeft = Bits(16);
                    var pt = ReadPtLen(NT, TBIT, 3);
                    c = ReadCLen(pt);
                    p = ReadPtLen(np, pBits, -1);
                }
                blockLeft--;
                int code = c.Decode(this);
                if (code < 256) { o[op++] = (byte)code; continue; }
                int length = code - 256 + 3;
                int j = p.Decode(this);
                int dist = j <= 1 ? j : (1 << (j - 1)) + Bits(j - 1);
                int from = op - dist - 1;
                if (from < 0) throw new InvalidDataException("LHA 자료가 깨졌습니다 (사전 밖을 가리킴).");
                for (int k = 0; k < length && op < size; k++) o[op++] = o[from + k];
            }
            return o;
        }
    }

    /// <summary>LHA CRC-16 (IBM, 다항식 0xA001, 처음 0).</summary>
    public static int Crc16(byte[] d)
    {
        int crc = 0;
        foreach (byte b in d)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xA001 : crc >> 1;
        }
        return crc;
    }

    private static int U16(byte[] d, int o) { return o + 1 < d.Length ? d[o] | (d[o + 1] << 8) : 0; }
    private static long U32(byte[] d, int o) { return o + 3 < d.Length ? (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24)) : 0; }
}
