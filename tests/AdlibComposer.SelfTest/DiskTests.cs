using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>PC-88 · PC-98 디스크 이미지 (HDM · D88 · FDI) + 파일 시스템 (FAT12 · N88-BASIC).</summary>
public static class DiskTests
{
    public static void Run(Action<string, bool, string> check)
    {
        var raw = MakeFat12();
        Fat(check, "HDM (raw 2HD)", DiskImage.ReadRaw(raw));
        Fat(check, "D88 2HD", DiskImage.ReadD88(ToD88(raw, 77, 2, 8, 1024, 0x20, "PC98 DISK")));
        Fat(check, "FDI 2HD", DiskImage.ReadFdi(ToFdi(raw)));
        N88(check);
        Extract(check, raw);
    }

    private static byte[] Text(string s, int len) { var b = Enumerable.Repeat((byte)' ', len).ToArray(); Encoding.ASCII.GetBytes(s).CopyTo(b, 0); return b; }

    /// <summary>PC-98 2HD (1024바이트 × 8 × 2 × 77) FAT12: SONG.M (100바이트), MUSIC/A.OPI (5바이트).</summary>
    private static byte[] MakeFat12()
    {
        const int S = 1024;
        var d = new byte[1261568];
        // BPB
        d[0x0B] = 0x00; d[0x0C] = 0x04; d[0x0D] = 1; d[0x0E] = 1; d[0x10] = 2; d[0x11] = 192; d[0x13] = 0xD0; d[0x14] = 0x04;
        d[0x15] = 0xFE; d[0x16] = 2; d[0x18] = 8; d[0x1A] = 2;
        // FAT (LBA 1, 사본 LBA 3): 클러스터 2 = 끝, 3 = 끝 (폴더), 4 = 끝
        foreach (int at in new[] { 1 * S, 3 * S })
        {
            d[at] = 0xFE; d[at + 1] = 0xFF; d[at + 2] = 0xFF;
            // 12비트: cl2 = FFF, cl3 = FFF → 바이트 3 = FF, 4 = FF, 5 = FF; cl4 = FFF → 바이트 6 = FF, 7 = 0F
            d[at + 3] = 0xFF; d[at + 4] = 0xFF; d[at + 5] = 0xFF; d[at + 6] = 0xFF; d[at + 7] = 0x0F;
        }
        int root = 5 * S, data = 11 * S;
        void Entry(int at, string name, string ext, int attr, int cl, int size)
        {
            Text(name, 8).CopyTo(d, at); Text(ext, 3).CopyTo(d, at + 8);
            d[at + 11] = (byte)attr; d[at + 0x1A] = (byte)cl; d[at + 0x1B] = (byte)(cl >> 8);
            BitConverter.GetBytes(size).CopyTo(d, at + 0x1C);
        }
        Entry(root, "PC98DISK", "", 0x08, 0, 0);          // 볼륨 이름 (건너뜀)
        Entry(root + 32, "SONG", "M", 0x20, 2, 100);
        Entry(root + 64, "MUSIC", "", 0x10, 3, 0);
        d[root + 96] = 0xE5;                               // 지운 파일
        for (int i = 0; i < 100; i++) d[data + i] = (byte)i;
        int sub = data + 1 * S;
        Entry(sub, ".", "", 0x10, 3, 0); Entry(sub + 32, "..", "", 0x10, 0, 0);
        Entry(sub + 64, "A", "OPI", 0x21, 4, 5);
        Encoding.ASCII.GetBytes("HELLO").CopyTo(d, data + 2 * S);
        return d;
    }

    private static byte[] ToD88(byte[] raw, int cyls, int heads, int spt, int ss, byte media, string name)
    {
        var ms = new MemoryStream();
        var hdr = new byte[0x2B0];
        Encoding.ASCII.GetBytes(name).CopyTo(hdr, 0);
        hdr[0x1B] = media;
        ms.Write(hdr, 0, hdr.Length);
        int n = ss == 256 ? 1 : ss == 512 ? 2 : 3;
        var table = new int[164];
        int p = 0;
        for (int c = 0; c < cyls; c++)
            for (int h = 0; h < heads; h++)
            {
                table[c * heads + h] = (int)ms.Position;
                for (int r = 1; r <= spt; r++)
                {
                    var sh = new byte[16];
                    sh[0] = (byte)c; sh[1] = (byte)h; sh[2] = (byte)r; sh[3] = (byte)n;
                    sh[4] = (byte)spt; sh[0x0E] = (byte)ss; sh[0x0F] = (byte)(ss >> 8);
                    ms.Write(sh, 0, 16);
                    ms.Write(raw, p, ss);
                    p += ss;
                }
            }
        var b = ms.ToArray();
        for (int t = 0; t < 164; t++) BitConverter.GetBytes(table[t]).CopyTo(b, 0x20 + t * 4);
        BitConverter.GetBytes(b.Length).CopyTo(b, 0x1C);
        return b;
    }

    private static byte[] ToFdi(byte[] raw)
    {
        var b = new byte[4096 + raw.Length];
        BitConverter.GetBytes(0x90).CopyTo(b, 4); BitConverter.GetBytes(4096).CopyTo(b, 8); BitConverter.GetBytes(raw.Length).CopyTo(b, 12);
        BitConverter.GetBytes(1024).CopyTo(b, 16); BitConverter.GetBytes(8).CopyTo(b, 20); BitConverter.GetBytes(2).CopyTo(b, 24); BitConverter.GetBytes(77).CopyTo(b, 28);
        raw.CopyTo(b, 4096);
        return b;
    }

    private static void Fat(Action<string, bool, string> check, string name, DiskImage img)
    {
        var files = img.Files(out string fs);
        var song = files.FirstOrDefault(f => f.Path == "SONG.M");
        var opi = files.FirstOrDefault(f => f.Path == "MUSIC/A.OPI");
        check("Disk " + name + ": " + img.Media + " geometry", img.Media == "2HD" && img.SectorSize == 1024 && img.SectorsPerTrack == 8 && img.Cylinders == 77, img.Cylinders + "x" + img.Heads + "x" + img.SectorsPerTrack);
        check("Disk " + name + ": FAT12 files (volume label + deleted skipped)", fs.StartsWith("FAT12") && files.Count(f => !f.Directory) == 2 && files.Any(f => f.Directory && f.Path == "MUSIC"), string.Join(",", files.Select(f => f.Path)));
        check("Disk " + name + ": file data", song != null && song.Size == 100 && song.Data().Length == 100 && song.Data()[99] == 99 && opi != null && Encoding.ASCII.GetString(opi.Data()) == "HELLO" && opi.Attributes.Contains("R"), "");
    }

    private static void N88(Action<string, bool, string> check)
    {
        const int S = 256, Spt = 16, Heads = 2, Cyls = 40;
        var raw = new byte[Cyls * Heads * Spt * S];
        int Off(int track, int sector) { return (track * Spt + (sector - 1)) * S; }
        // 디렉터리 (논리 트랙 37, 섹터 1): TITLE.BAS → 클러스터 0x10
        for (int sec = 1; sec <= 12; sec++) for (int i = 0; i < S; i++) raw[Off(37, sec) + i] = 0xFF;
        int dir = Off(37, 1);
        Text("TITLE", 6).CopyTo(raw, dir); Text("BAS", 3).CopyTo(raw, dir + 6); raw[dir + 9] = 0x80; raw[dir + 10] = 0x10;
        Text("MUSIC", 6).CopyTo(raw, dir + 16); Text("M", 3).CopyTo(raw, dir + 22); raw[dir + 25] = 0x01; raw[dir + 26] = 0x20;
        // FAT (섹터 14): 0x10 → 0x11 → 마지막 (2섹터), 0x20 → 마지막 (1섹터)
        int fat = Off(37, 14);
        for (int i = 0; i < S; i++) raw[fat + i] = 0xFF;
        raw[fat + 0x10] = 0x11; raw[fat + 0x11] = 0xC2; raw[fat + 0x20] = 0xC1;
        for (int i = 0; i < 8 * S; i++) raw[Off(8, 1) + i] = 0x41;   // 클러스터 0x10 = 트랙 8 섹터 1~8
        for (int i = 0; i < 2 * S; i++) raw[Off(8, 9) + i] = 0x42;   // 클러스터 0x11 = 트랙 8 섹터 9~10
        for (int i = 0; i < S; i++) raw[Off(16, 1) + i] = 0x43;      // 클러스터 0x20 = 트랙 16 섹터 1
        var img = DiskImage.ReadD88(ToD88(raw, Cyls, Heads, Spt, S, 0x00, "PC88"));
        var files = img.Files(out string fs);
        var t = files.FirstOrDefault(f => f.Path == "TITLE.BAS");
        var m = files.FirstOrDefault(f => f.Path == "MUSIC.M");
        check("Disk D88 2D: N88-BASIC directory", fs == "N88-BASIC" && files.Count == 2 && img.Media == "2D" && img.Name == "PC88", fs + " " + string.Join(",", files.Select(f => f.Path)));
        check("Disk D88 2D: N88 cluster chain (8 + 2 sectors) and last-cluster count", t != null && t.Size == 10 * S && t.Data()[0] == 0x41 && t.Data()[9 * S] == 0x42 && t.Attributes.Contains("A")
            && m != null && m.Size == S && m.Data()[0] == 0x43 && m.Attributes.Contains("B"), t?.Size + " / " + m?.Size);
    }

    private static void Extract(Action<string, bool, string> check, byte[] raw)
    {
        string dir = Path.Combine(Path.GetTempPath(), "fmopl_disk_test");
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            var files = DiskImage.ReadRaw(raw).ExtractAll(dir, out string fs);
            bool ok = files.Count == 2 && File.ReadAllBytes(Path.Combine(dir, "SONG.M")).Length == 100 && File.ReadAllText(Path.Combine(dir, "MUSIC", "A.OPI")) == "HELLO";
            check("Disk extract all keeps folders", ok, string.Join(",", files.Select(Path.GetFileName)));
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
        check("Disk image extensions recognized", DiskImage.IsDiskImage("a.d88") && DiskImage.IsDiskImage("b.FDI") && DiskImage.IsDiskImage("c.hdm") && !DiskImage.IsDiskImage("d.vgm"), "");
    }
}
