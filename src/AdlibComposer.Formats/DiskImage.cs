using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// PC-88 · PC-98 플로피 디스크 이미지: D88 (.d88 · .d98 · .88d · .98d), FDI (Anex86), 날 이미지 (.hdm · .tfd · .xdf · .2hd).
/// 섹터를 (실린더, 헤드, 섹터 번호)로 읽고, 파일 시스템 (PC-98 MS-DOS FAT12 · PC-88 N88-BASIC)에서 파일 목록을 만들어 꺼낸다.
/// <para>디스크 안의 곡 파일 (PMD · FMP · mucom88 …)을 꺼내 보는 데 쓴다. 그 곡 형식의 재생은 아직 없다.</para>
/// </summary>
public sealed class DiskImage
{
    public static readonly string[] Extensions = { ".d88", ".d98", ".88d", ".98d", ".fdi", ".hdm", ".tfd", ".xdf", ".2hd", ".dup" };

    public static bool IsDiskImage(string path)
    {
        string ext = Path.GetExtension(path ?? "");
        return Extensions.Any(x => string.Equals(x, ext, StringComparison.OrdinalIgnoreCase));
    }

    public string Name = "";
    /// <summary>"D88" · "FDI" · "RAW".</summary>
    public string Format = "";
    /// <summary>"2D" · "2DD" · "2HD".</summary>
    public string Media = "";
    public int Cylinders, Heads, SectorsPerTrack, SectorSize;
    private readonly Dictionary<(int c, int h, int r), byte[]> sectors = new Dictionary<(int, int, int), byte[]>();
    /// <summary>트랙 순서대로 (논리 트랙 = 실린더 × 헤드 수 + 헤드)의 섹터 번호 목록.</summary>
    private readonly SortedDictionary<int, List<int>> trackSectors = new SortedDictionary<int, List<int>>();

    public int SectorCount { get { return sectors.Count; } }

    public byte[] Sector(int c, int h, int r) { return sectors.TryGetValue((c, h, r), out var d) ? d : null; }

    /// <summary>논리 섹터 번호 (0부터, 트랙 순서 · 섹터 번호 순서).</summary>
    public byte[] Lba(int lba)
    {
        if (SectorsPerTrack <= 0 || lba < 0) return null;
        int track = lba / SectorsPerTrack, s = lba % SectorsPerTrack;
        int c = track / Math.Max(1, Heads), h = track % Math.Max(1, Heads);
        if (trackSectors.TryGetValue(track, out var rs) && s < rs.Count) return Sector(c, h, rs[s]);
        return Sector(c, h, s + 1);
    }

    private void Add(int c, int h, int r, byte[] data)
    {
        sectors[(c, h, r)] = data;
        int t = c * Math.Max(1, Heads) + h;
        if (!trackSectors.TryGetValue(t, out var l)) trackSectors[t] = l = new List<int>();
        if (!l.Contains(r)) { l.Add(r); l.Sort(); }
    }

    // ================================================================ 읽기

    public static DiskImage Load(string path)
    {
        var d = File.ReadAllBytes(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        DiskImage img = ext == ".fdi" ? ReadFdi(d) : (ext == ".hdm" || ext == ".tfd" || ext == ".xdf" || ext == ".2hd" || ext == ".dup") ? ReadRaw(d) : ReadD88(d);
        if (img.Name.Length == 0) img.Name = Path.GetFileNameWithoutExtension(path);
        return img;
    }

    /// <summary>D88: 머리말 0x2B0 (이름 17, 쓰기 금지 0x1A, 미디어 0x1B, 크기 0x1C, 트랙 위치 164개 0x20~), 섹터마다 16바이트 머리 + 데이터.</summary>
    public static DiskImage ReadD88(byte[] d)
    {
        if (d.Length < 0x2B0) throw new InvalidDataException("D88 이미지가 너무 짧습니다.");
        var img = new DiskImage { Format = "D88" };
        int nameEnd = Array.IndexOf(d, (byte)0, 0, 17);
        img.Name = KoreanText.ShiftJis.GetString(d, 0, nameEnd < 0 ? 17 : nameEnd).Trim();
        int media = d[0x1B];
        img.Media = media == 0x20 ? "2HD" : media == 0x10 ? "2DD" : "2D";
        img.Heads = 2;
        int size = (int)Math.Min(d.Length, (long)(d[0x1C] | (d[0x1D] << 8) | (d[0x1E] << 16) | (d[0x1F] << 24)));
        if (size <= 0x2B0) size = d.Length;
        // 첫 트랙 위치가 머리말 크기를 알려준다 (트랙 표가 164칸보다 짧은 이미지)
        int first = U32(d, 0x20);
        int tableEnd = first >= 0x20 + 4 && first <= 0x2B0 ? first : 0x2B0;
        int maxCyl = 0;
        for (int t = 0; 0x20 + t * 4 < tableEnd; t++)
        {
            int at = U32(d, 0x20 + t * 4);
            if (at <= 0 || at >= size) continue;
            int p = at;
            int count = -1;
            for (int k = 0; p + 16 <= size && (count < 0 || k < count); k++)
            {
                int c = d[p], h = d[p + 1], r = d[p + 2], n = d[p + 3];
                if (count < 0) count = d[p + 4] | (d[p + 5] << 8);
                int len = d[p + 0x0E] | (d[p + 0x0F] << 8);
                if (len <= 0 || p + 16 + len > d.Length) break;
                var data = new byte[len];
                Array.Copy(d, p + 16, data, 0, len);
                img.Add(c, h, r, data);
                img.SectorSize = Math.Max(img.SectorSize, len);
                maxCyl = Math.Max(maxCyl, c);
                p += 16 + len;
            }
            if (count > 0) img.SectorsPerTrack = Math.Max(img.SectorsPerTrack, count);
        }
        img.Cylinders = maxCyl + 1;
        if (img.SectorCount == 0) throw new InvalidDataException("D88 이미지에 섹터가 없습니다.");
        return img;
    }

    /// <summary>FDI (Anex86): 머리말 (0, 종류, 머리말 크기, 데이터 크기, 섹터 크기, 트랙당 섹터, 면, 실린더) 뒤 날 섹터.</summary>
    public static DiskImage ReadFdi(byte[] d)
    {
        if (d.Length < 32) throw new InvalidDataException("FDI 이미지가 너무 짧습니다.");
        int hdr = U32(d, 8), secSize = U32(d, 16), spt = U32(d, 20), heads = U32(d, 24), cyls = U32(d, 28);
        if (hdr <= 0 || secSize <= 0 || spt <= 0 || heads <= 0 || cyls <= 0 || hdr >= d.Length) throw new InvalidDataException("FDI 머리말이 잘못되었습니다.");
        var img = new DiskImage { Format = "FDI", SectorSize = secSize, SectorsPerTrack = spt, Heads = heads, Cylinders = cyls };
        img.Media = secSize >= 1024 || spt >= 15 ? "2HD" : cyls > 50 ? "2DD" : "2D";
        img.FillRaw(d, hdr);
        return img;
    }

    /// <summary>날 이미지: 크기로 모양을 정한다 (PC-98 2HD 1232KB · 1.44MB · 720KB · 640KB · PC-88 2D 320KB).</summary>
    public static DiskImage ReadRaw(byte[] d)
    {
        var shapes = new (int size, int ss, int spt, int heads, int cyls, string media)[]
        {
            (1261568, 1024, 8, 2, 77, "2HD"), (1474560, 512, 18, 2, 80, "2HD"), (1228800, 512, 15, 2, 80, "2HD"),
            (737280, 512, 9, 2, 80, "2DD"), (655360, 512, 8, 2, 80, "2DD"), (327680, 256, 16, 2, 40, "2D"), (348160, 256, 16, 2, 42, "2D"),
        };
        foreach (var s in shapes)
            if (d.Length == s.size)
            {
                var img = new DiskImage { Format = "RAW", SectorSize = s.ss, SectorsPerTrack = s.spt, Heads = s.heads, Cylinders = s.cyls, Media = s.media };
                img.FillRaw(d, 0);
                return img;
            }
        throw new InvalidDataException("크기로 디스크 모양을 알 수 없습니다 (" + d.Length + "바이트).");
    }

    private void FillRaw(byte[] d, int at)
    {
        int p = at;
        for (int c = 0; c < Cylinders; c++)
            for (int h = 0; h < Heads; h++)
                for (int r = 1; r <= SectorsPerTrack; r++)
                {
                    if (p + SectorSize > d.Length) return;
                    var s = new byte[SectorSize];
                    Array.Copy(d, p, s, 0, SectorSize);
                    Add(c, h, r, s);
                    p += SectorSize;
                }
    }

    // ================================================================ 파일 시스템

    public sealed class FileEntry
    {
        /// <summary>폴더를 포함한 이름 (예: "MUSIC/STAGE1.M").</summary>
        public string Path = "";
        public int Size;
        public bool Directory;
        public string Attributes = "";
        internal Func<byte[]> Read;
        public byte[] Data() { return Read != null ? Read() : new byte[0]; }
    }

    /// <summary>찾은 파일 시스템 이름 ("FAT12" · "N88-BASIC" · "")과 파일 목록.</summary>
    public List<FileEntry> Files(out string fileSystem)
    {
        var fat = ReadFat12();
        if (fat != null) { fileSystem = "FAT12 (MS-DOS)"; return fat; }
        var n88 = ReadN88();
        if (n88 != null) { fileSystem = "N88-BASIC"; return n88; }
        fileSystem = "";
        return new List<FileEntry>();
    }

    /// <summary>모든 파일을 folder 아래에 꺼낸다 (폴더 구조 유지). 반환 = 꺼낸 파일 경로.</summary>
    public List<string> ExtractAll(string folder, out string fileSystem)
    {
        var done = new List<string>();
        Directory.CreateDirectory(folder);
        foreach (var f in Files(out fileSystem))
        {
            string rel = string.Join(System.IO.Path.DirectorySeparatorChar.ToString(), f.Path.Split('/').Select(SafeName));
            string target = System.IO.Path.Combine(folder, rel);
            if (f.Directory) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target));
            File.WriteAllBytes(target, f.Data());
            done.Add(target);
        }
        return done;
    }

    private static string SafeName(string n)
    {
        var bad = System.IO.Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (char ch in n) sb.Append(Array.IndexOf(bad, ch) >= 0 ? '_' : ch);
        string s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 ? "_" : s;
    }

    // ------------------------------------------------------------ FAT12

    private List<FileEntry> ReadFat12()
    {
        var boot = Lba(0);
        if (boot == null) return null;
        int bps = U16(boot, 0x0B), spc = boot.Length > 0x0D ? boot[0x0D] : 0, reserved = U16(boot, 0x0E), fats = boot.Length > 0x10 ? boot[0x10] : 0;
        int rootEntries = U16(boot, 0x11), total = U16(boot, 0x13), spf = U16(boot, 0x16);
        bool bpbOk = bps == SectorSize && spc > 0 && (spc & (spc - 1)) == 0 && reserved > 0 && fats >= 1 && fats <= 2 && rootEntries > 0 && spf > 0 && spf < 64;
        if (!bpbOk)
        {
            // BPB가 없는 PC-98 디스크: 미디어에 따른 기본값
            if (SectorSize == 1024 && SectorsPerTrack == 8) { spc = 1; reserved = 1; fats = 2; rootEntries = 192; spf = 2; total = Cylinders * Heads * 8; }
            else if (SectorSize == 512 && SectorsPerTrack == 18) { spc = 1; reserved = 1; fats = 2; rootEntries = 224; spf = 9; total = 2880; }
            else if (SectorSize == 512 && SectorsPerTrack == 9) { spc = 2; reserved = 1; fats = 2; rootEntries = 112; spf = 3; total = 1440; }
            else if (SectorSize == 512 && SectorsPerTrack == 8) { spc = 2; reserved = 1; fats = 2; rootEntries = 112; spf = 2; total = 1280; }
            else return null;
            bps = SectorSize;
        }
        var fat = new List<byte>();
        for (int i = 0; i < spf; i++) { var s = Lba(reserved + i); if (s == null) return null; fat.AddRange(s); }
        var fatB = fat.ToArray();
        // FAT 첫 바이트 = 미디어 (0xF0 이상), 다음 두 바이트 0xFF
        if (fatB.Length < 3 || fatB[0] < 0xF0 || fatB[1] != 0xFF || fatB[2] != 0xFF) return null;
        int rootAt = reserved + fats * spf;
        int rootSectors = (rootEntries * 32 + bps - 1) / bps;
        int dataAt = rootAt + rootSectors;
        int maxCluster = (Math.Max(total, 1) - dataAt) / Math.Max(1, spc) + 1;

        int Next(int cl)
        {
            int o = cl + cl / 2;
            if (o + 1 >= fatB.Length) return 0xFFF;
            int v = fatB[o] | (fatB[o + 1] << 8);
            return (cl & 1) == 0 ? v & 0xFFF : v >> 4;
        }
        byte[] Chain(int start, int size)
        {
            var ms = new MemoryStream();
            int cl = start, guard = 0;
            while (cl >= 2 && cl <= maxCluster && cl < 0xFF8 && guard++ < 4096)
            {
                for (int k = 0; k < spc; k++) { var s = Lba(dataAt + (cl - 2) * spc + k); if (s != null) ms.Write(s, 0, s.Length); }
                if (size >= 0 && ms.Length >= size) break;
                cl = Next(cl);
            }
            var b = ms.ToArray();
            if (size >= 0 && b.Length > size) Array.Resize(ref b, size);
            return b;
        }

        var list = new List<FileEntry>();
        void Walk(byte[] dir, string prefix, int depth)
        {
            for (int o = 0; o + 32 <= dir.Length; o += 32)
            {
                int first = dir[o];
                if (first == 0x00) break;
                if (first == 0xE5 || first == 0x2E) continue;
                int attr = dir[o + 11];
                if ((attr & 0x08) != 0) continue;   // 볼륨 이름 · 긴 이름
                string name = KoreanText.ShiftJis.GetString(dir, o, 8).TrimEnd();
                string ext = KoreanText.ShiftJis.GetString(dir, o + 8, 3).TrimEnd();
                if (first == 0x05) name = "å" + name.Substring(1);
                string full = prefix + name + (ext.Length > 0 ? "." + ext : "");
                int start = U16(dir, o + 0x1A), size = U32(dir, o + 0x1C);
                if ((attr & 0x10) != 0)
                {
                    list.Add(new FileEntry { Path = full, Directory = true, Attributes = "D" });
                    if (depth < 8 && start >= 2) Walk(Chain(start, -1), full + "/", depth + 1);
                    continue;
                }
                int st = start, sz = size;
                list.Add(new FileEntry { Path = full, Size = size, Attributes = ((attr & 1) != 0 ? "R" : "") + ((attr & 2) != 0 ? "H" : "") + ((attr & 4) != 0 ? "S" : ""), Read = () => Chain(st, sz) });
            }
        }
        var root = new MemoryStream();
        for (int i = 0; i < rootSectors; i++) { var s = Lba(rootAt + i); if (s != null) root.Write(s, 0, s.Length); }
        Walk(root.ToArray(), "", 0);
        return list;
    }

    // ------------------------------------------------------------ N88-BASIC (PC-88 2D)

    /// <summary>
    /// 2D 디스크: 디렉터리 = 논리 트랙 37 (실린더 18 · 헤드 1) 섹터 1~12 (16바이트 칸: 이름 6 · 확장자 3 · 속성 · 첫 클러스터),
    /// FAT = 같은 트랙 섹터 14 (클러스터 = 반 트랙 8섹터, 값 0xC1~0xC8 = 마지막 · 쓴 섹터 수).
    /// </summary>
    private List<FileEntry> ReadN88()
    {
        if (SectorSize != 256 || SectorsPerTrack < 16) return null;
        const int DirTrack = 37;
        int dc = DirTrack / Heads, dh = DirTrack % Heads;
        var fat = Sector(dc, dh, 14);
        if (fat == null) return null;
        byte[] Read(int cl, out bool ok)
        {
            ok = true;
            var ms = new MemoryStream();
            int guard = 0;
            while (guard++ < 200)
            {
                int track = cl / 2, first = (cl & 1) * 8 + 1;
                int v = cl < fat.Length ? fat[cl] : 0xFF;
                int count = v >= 0xC1 && v <= 0xC8 ? v - 0xC0 : 8;
                for (int k = 0; k < count; k++) { var s = Sector(track / Heads, track % Heads, first + k); if (s != null) ms.Write(s, 0, s.Length); }
                if (v >= 0xC1 && v <= 0xC8) break;
                if (v >= 0xA0) { ok = false; break; }
                cl = v;
            }
            return ms.ToArray();
        }
        var list = new List<FileEntry>();
        for (int sec = 1; sec <= 12; sec++)
        {
            var s = Sector(dc, dh, sec);
            if (s == null) continue;
            for (int o = 0; o + 16 <= s.Length; o += 16)
            {
                int b0 = s[o];
                if (b0 == 0x00 || b0 == 0xFF) continue;
                bool printable = true;
                for (int i = 0; i < 9; i++) if (s[o + i] < 0x20 || s[o + i] == 0x7F) printable = false;
                if (!printable) return null;
                string name = Encoding.ASCII.GetString(s, o, 6).TrimEnd();
                string ext = Encoding.ASCII.GetString(s, o + 6, 3).TrimEnd();
                int attr = s[o + 9], cl = s[o + 10];
                if (cl >= 0xA0) continue;
                var data = Read(cl, out bool ok);
                list.Add(new FileEntry
                {
                    Path = name + (ext.Length > 0 ? "." + ext : ""),
                    Size = data.Length,
                    Attributes = ((attr & 0x01) != 0 ? "B" : "") + ((attr & 0x10) != 0 ? "W" : "") + ((attr & 0x80) != 0 ? "A" : "") + (ok ? "" : "!"),
                    Read = () => data,
                });
            }
        }
        return list.Count > 0 ? list : null;
    }

    private static int U16(byte[] d, int o) { return o + 1 < d.Length ? d[o] | (d[o + 1] << 8) : 0; }
    private static int U32(byte[] d, int o) { return o + 3 < d.Length ? d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24) : 0; }
}
