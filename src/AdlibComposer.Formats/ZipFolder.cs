using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// ZIP을 폴더처럼: 처음 열 때 임시 폴더(<see cref="CacheBase"/> 아래 zip마다 하나)에 한 번 풀고, 그 폴더를 보통 폴더로 쓴다.
/// 곡을 여는 쪽(ROL · IMS · SOP · VGM …)은 경로로 읽고 같은 폴더의 BNK(뱅크) · ISS(가사) · m3u(곡 목록)를 찾으므로 그대로 동작한다.
/// <para>파일 이름: UTF-8 깃발이 있으면 UTF-8, 없으면 바이트를 보고 UTF-8 / 한글(완성형 CP949 · 조합형) / CP437로.
/// 안전: 절대 경로 · ".." · 쓸 수 없는 글자는 걸러 풀 폴더 밖으로 나가지 않게 한다 (zip slip). 다 합쳐 <see cref="MaxTotalBytes"/>를 넘으면 열지 않는다.</para>
/// <para>캐시 이름 = zip 전체 경로 · 크기 · 고친 시각의 해시: zip이 바뀌면 새로 푼다. 사흘 넘은 캐시는 새로 풀 때 지운다.</para>
/// </summary>
public static class ZipFolder
{
    public const long MaxTotalBytes = 512L * 1024 * 1024;
    private const string DoneMark = ".fmopl_unzipped";

    public static string CacheBase { get; set; } = Path.Combine(Path.GetTempPath(), "FMOPLTool", "zip");

    /// <summary>zip을 푼 폴더 → 원래 zip 경로 (푼 표시 파일에 적어 둔 것). 아니면 null.</summary>
    public static string SourceZip(string dir)
    {
        try
        {
            string mark = Path.Combine(dir, DoneMark);
            if (!File.Exists(mark)) return null;
            string zip = File.ReadAllText(mark).Trim();
            return File.Exists(zip) ? zip : null;
        }
        catch (Exception) { return null; }
    }

    public static bool IsZip(string path)
    {
        return path != null && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>경로가 zip을 푼 임시 폴더 안인가 (저장할 때 다른 이름으로 묻기 등).</summary>
    public static bool IsCachePath(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (ZipPath.IsInZip(path)) return true;   // 풀지 않고 읽은 zip 안 파일도 그 자리에 저장할 수 없다
        try
        {
            string full = Path.GetFullPath(path);
            string bas = Path.GetFullPath(CacheBase).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return full.StartsWith(bas, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; }
    }

    /// <summary>zip을 (아직이면) 풀고 그 폴더를 돌려준다. 실패하면 예외.</summary>
    public static string Extract(string zipPath)
    {
        var fi = new FileInfo(zipPath);
        if (!fi.Exists) throw new FileNotFoundException("zip 파일이 없습니다.", zipPath);
        string key;
        using (var sha = SHA1.Create())
            key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(fi.FullName.ToUpperInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks))).Replace("-", "").Substring(0, 16);
        string dir = Path.Combine(CacheBase, Path.GetFileNameWithoutExtension(fi.Name).Trim() + "_" + key);
        if (File.Exists(Path.Combine(dir, DoneMark))) { AddHootEntries(fi.FullName, dir); return dir; }

        CleanOld();
        if (Directory.Exists(dir)) Directory.Delete(dir, true);   // 지난번에 풀다 만 것
        string tmp = dir + ".part";
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        string root = Path.GetFullPath(tmp).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        try
        {
            // Latin-1로 열면 깃발 없는 이름의 바이트가 그대로 남는다 (UTF-8 깃발이 있는 이름은 .NET이 UTF-8로 읽는다)
            using (var zip = ZipFile.Open(fi.FullName, ZipArchiveMode.Read, Encoding.Latin1))
            {
                long total = zip.Entries.Sum(e => e.Length);
                if (total > MaxTotalBytes) throw new InvalidDataException("zip이 너무 큽니다 (풀면 " + (total >> 20) + "MB, 한도 " + (MaxTotalBytes >> 20) + "MB).");
                var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in zip.Entries)
                {
                    string rel = SafeRelative(EntryName(e));
                    if (rel.Length == 0) continue;
                    string target = Path.GetFullPath(Path.Combine(tmp, rel));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // 밖으로 나가는 이름
                    bool isDir = e.FullName.EndsWith("/") || e.FullName.EndsWith("\\");
                    if (isDir) { Directory.CreateDirectory(target); continue; }
                    // 대소문자만 다른 같은 이름: 뒤에 번호
                    string unique = target;
                    for (int n = 2; !used.Add(unique); n++)
                        unique = Path.Combine(Path.GetDirectoryName(target), Path.GetFileNameWithoutExtension(target) + " (" + n + ")" + Path.GetExtension(target));
                    Directory.CreateDirectory(Path.GetDirectoryName(unique));
                    using (var src = e.Open())
                    using (var dst = File.Create(unique))
                        src.CopyTo(dst);
                    try { File.SetLastWriteTime(unique, e.LastWriteTime.DateTime); } catch (Exception) { }
                }
            }
            File.WriteAllText(Path.Combine(tmp, DoneMark), fi.FullName);
            File.SetAttributes(Path.Combine(tmp, DoneMark), FileAttributes.Hidden);
            Directory.Move(tmp, dir);
        }
        catch
        {
            try { Directory.Delete(tmp, true); } catch (Exception) { }
            throw;
        }
        AddHootEntries(fi.FullName, dir);
        return dir;
    }

    /// <summary>hoot 목록(zip 옆 xml 폴더)에 이 zip을 쓰는 흉내 항목이 있는가: 그런 zip은 풀어서 .hoot 항목을 만들어야 한다.</summary>
    public static bool IsHootArchive(string zipPath)
    {
        try
        {
            string listDir = HootArchive.FindListDir(zipPath);
            return listDir != null && HootArchive.Index(listDir).ContainsKey(Path.GetFileNameWithoutExtension(zipPath).Trim());
        }
        catch (Exception) { return false; }
    }

    /// <summary>hoot 아카이브 zip이면 그 zip의 기종 흉내 항목을 .hoot 파일로 (<see cref="HootArchive"/>). 실패해도 그만.</summary>
    private static void AddHootEntries(string zipPath, string dir)
    {
        try { HootArchive.WriteEntries(zipPath, dir); }
        catch (Exception) { }
    }

    /// <summary>항목 이름: Latin-1로 읽은 글자 = 원래 바이트. UTF-8로 맞으면 UTF-8, 아니면 한글 자동 판별 (완성형 / 조합형), 둘 다 아니면 CP437.</summary>
    public static string EntryName(ZipArchiveEntry e)
    {
        string s = e.FullName;
        if (s.Any(c => c > 0xFF)) return s;                                // 이미 UTF-8로 읽힘 (깃발)
        var raw = s.Select(c => (byte)c).ToArray();
        if (raw.All(b => b < 0x80)) return s;
        // 예외 없이 먼저 검사 (CP949 이름마다 DecoderFallbackException이 디버거 출력에 찍히지 않게)
        if (System.Text.Unicode.Utf8.IsValid(raw)) return Encoding.UTF8.GetString(raw);
        string ko = KoreanText.Decode(raw, 0, raw.Length);
        if (ko.Length > 0 && !ko.Contains('�')) return ko;
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding(437).GetString(raw); }
        catch (Exception) { return s; }
    }

    /// <summary>zip 안 경로 → 안전한 상대 경로 (쓸 수 없는 글자 → _, ".." · 절대 경로 · 드라이브 제거). 쓸 수 없으면 빈 문자열.</summary>
    public static string SafeRelative(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var parts = new List<string>();
        foreach (var raw in name.Replace('\\', '/').Split('/'))
        {
            string p = raw.Trim();
            if (p.Length == 0 || p == "." || p == "..") continue;
            p = new string(p.Select(c => Array.IndexOf(bad, c) >= 0 ? '_' : c).ToArray()).TrimEnd('.', ' ');
            if (p.Length == 0) continue;
            parts.Add(p);
        }
        return string.Join(Path.DirectorySeparatorChar.ToString(), parts);
    }

    /// <summary>사흘 넘게 안 쓴 캐시를 지운다 (실패해도 그만).</summary>
    private static void CleanOld()
    {
        try
        {
            if (!Directory.Exists(CacheBase)) return;
            foreach (var d in Directory.GetDirectories(CacheBase))
            {
                try { if (Directory.GetLastWriteTimeUtc(d) < DateTime.UtcNow.AddDays(-3)) Directory.Delete(d, true); }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }
}
