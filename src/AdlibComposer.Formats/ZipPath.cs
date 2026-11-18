using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;

namespace AdlibComposer.Formats;

/// <summary>
/// 풀지 않고 zip 안을 읽는 길: zip 안 파일 = "zip 경로\안 경로" (예: <c>C:\곡\모음.zip\가요\봄.rol</c>).
/// Path.GetFileName · GetExtension · GetDirectoryName이 보통 경로처럼 맞아, 곡 옆의 같은 이름 BNK · ISS도 같은 방법으로 찾는다.
/// <para>보통 경로는 그대로 File · Directory로 넘긴다 (이 클래스의 함수는 어느 경로든 받는다).
/// 안 경로 이름은 <see cref="ZipFolder"/>가 풀 때와 같다 (한글 이름 판별 · 쓸 수 없는 글자 → _).
/// 한 단계만: zip 안의 zip은 이 길로 들어가지 않는다 (그런 zip은 <see cref="ZipFolder.Extract"/>로 푼다).</para>
/// </summary>
public static class ZipPath
{
    /// <summary>풀지 않고 바로 읽는 곡 형식: AdLib ROL · IMS · SOP · MUS, MIDI (MID · MIDI · WRK · XMI · MDI).</summary>
    public static readonly string[] DirectSongExtensions = { ".rol", ".ims", ".sop", ".mus", ".mid", ".midi", ".wrk", ".xmi", ".mdi" };

    /// <summary>곡이 옆에서 찾는 파일 (뱅크 · 가사 · 음색): 곡과 같이 바로 읽는다.</summary>
    public static readonly string[] SideExtensions = { ".bnk", ".iss", ".snd", ".ad", ".opl", ".op2", ".ibk", ".ins", ".sbi" };

    private sealed class Index
    {
        public long Length;
        public DateTime Time;
        /// <summary>안 경로(대문자, '\' 구분) → zip 항목 이름(원래 그대로).</summary>
        public readonly Dictionary<string, string> Files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        /// <summary>안 경로 → 보이는 이름 (대소문자 그대로).</summary>
        public readonly Dictionary<string, string> Shown = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, DateTime> Times = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> Dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "" };
    }

    private static readonly object gate = new object();
    private static readonly Dictionary<string, Index> indexes = new Dictionary<string, Index>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 경로를 zip(실제 파일)과 그 안 경로로 나눈다. zip 자체면 inner = "". zip을 거치지 않는 경로면 false.
    /// </summary>
    public static bool TrySplit(string path, out string zip, out string inner)
    {
        zip = inner = null;
        if (string.IsNullOrEmpty(path) || path.IndexOf(".zip", StringComparison.OrdinalIgnoreCase) < 0) return false;
        string p = path.Replace('/', '\\').TrimEnd('\\');
        int at = 0;
        while (true)
        {
            int i = p.IndexOf(".zip", at, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            int end = i + 4;
            if (end == p.Length || p[end] == '\\')
            {
                string cand = p.Substring(0, end);
                if (File.Exists(cand))
                {
                    zip = cand;
                    inner = end == p.Length ? "" : p.Substring(end + 1);
                    return true;
                }
            }
            at = end;
        }
    }

    /// <summary>zip 안 파일 · 폴더 경로인가 (zip 파일 자체는 아님).</summary>
    public static bool IsInZip(string path)
    {
        return TrySplit(path, out _, out string inner) && inner.Length > 0;
    }

    /// <summary>zip을 폴더처럼 볼 때의 폴더인가 (zip 자체 또는 zip 안 폴더).</summary>
    public static bool IsZipDirectory(string path)
    {
        if (!TrySplit(path, out string zip, out string inner)) return false;
        var x = Get(zip);
        return x != null && x.Dirs.Contains(inner);
    }

    /// <summary>
    /// 이 zip을 풀지 않고 폴더처럼 볼 수 있는가: 곡 파일이 모두 바로 읽는 형식(<see cref="DirectSongExtensions"/>)이고 그런 곡이 하나 이상,
    /// zip 안의 zip이 없을 때. 다른 형식(VGM · 트래커 · hoot …)이 섞이면 false (그런 zip은 지금처럼 임시 폴더에 푼다).
    /// </summary>
    /// <param name="isSong">곡 파일인가 (파일 목록이 보이는 형식).</param>
    public static bool CanBrowse(string zipPath, Func<string, bool> isSong)
    {
        var x = Get(zipPath);
        if (x == null) return false;
        int direct = 0;
        foreach (var rel in x.Shown.Values)
        {
            string ext = Path.GetExtension(rel);
            if (ZipFolder.IsZip(rel)) return false;
            if (DirectSongExtensions.Any(e => string.Equals(e, ext, StringComparison.OrdinalIgnoreCase))) { direct++; continue; }
            if (isSong(rel)) return false;
        }
        return direct > 0;
    }

    public static bool FileExists(string path)
    {
        if (TrySplit(path, out string zip, out string inner))
        {
            if (inner.Length == 0) return true;   // zip 파일 자체
            var x = Get(zip);
            return x != null && x.Files.ContainsKey(inner);
        }
        return File.Exists(path);
    }

    public static bool DirectoryExists(string path)
    {
        if (TrySplit(path, out string zip, out string inner))
        {
            var x = Get(zip);
            return x != null && x.Dirs.Contains(inner);
        }
        return Directory.Exists(path);
    }

    public static DateTime GetLastWriteTimeUtc(string path)
    {
        if (TrySplit(path, out string zip, out string inner) && inner.Length > 0)
        {
            var x = Get(zip);
            if (x != null && x.Times.TryGetValue(inner, out var t)) return t;
            throw new FileNotFoundException("zip 안에 파일이 없습니다.", path);
        }
        return File.GetLastWriteTimeUtc(path);
    }

    public static byte[] ReadAllBytes(string path)
    {
        if (!TrySplit(path, out string zip, out string inner) || inner.Length == 0) return File.ReadAllBytes(path);
        var x = Get(zip);
        if (x == null || !x.Files.TryGetValue(inner, out var name)) throw new FileNotFoundException("zip 안에 파일이 없습니다: " + inner, path);
        using (var z = ZipFile.Open(zip, ZipArchiveMode.Read, Encoding.Latin1))
        {
            var e = z.GetEntry(name) ?? z.Entries.FirstOrDefault(en => en.FullName == name);
            if (e == null) throw new FileNotFoundException("zip 안에 파일이 없습니다: " + inner, path);
            if (e.Length > ZipFolder.MaxTotalBytes) throw new InvalidDataException("zip 안 파일이 너무 큽니다: " + inner);
            using (var s = e.Open())
            using (var m = new MemoryStream((int)Math.Max(0, e.Length)))
            {
                s.CopyTo(m);
                return m.ToArray();
            }
        }
    }

    /// <summary>읽기 스트림 (zip 안이면 메모리에 읽은 것).</summary>
    public static Stream OpenRead(string path)
    {
        if (!IsInZip(path)) return File.OpenRead(path);
        return new MemoryStream(ReadAllBytes(path), false);
    }

    /// <summary>폴더 안 파일 (전체 경로, 하위 폴더 빼고).</summary>
    public static string[] GetFiles(string dir)
    {
        if (!TrySplit(dir, out string zip, out string inner)) return Directory.GetFiles(dir);
        var x = Get(zip);
        if (x == null || !x.Dirs.Contains(inner)) throw new DirectoryNotFoundException(dir);
        return x.Shown.Where(kv => ParentOf(kv.Key) == inner).Select(kv => Path.Combine(zip, kv.Value)).ToArray();
    }

    /// <summary>폴더 안 파일 중 확장자가 맞는 것 (예: ".bnk").</summary>
    public static string[] GetFiles(string dir, string ext)
    {
        return GetFiles(dir).Where(f => string.Equals(Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>폴더 안 폴더 (전체 경로).</summary>
    public static string[] GetDirectories(string dir)
    {
        if (!TrySplit(dir, out string zip, out string inner)) return Directory.GetDirectories(dir);
        var x = Get(zip);
        if (x == null || !x.Dirs.Contains(inner)) throw new DirectoryNotFoundException(dir);
        return x.Dirs.Where(d => d.Length > 0 && ParentOf(d) == inner).Select(d => Path.Combine(zip, d)).ToArray();
    }

    private static string ParentOf(string rel)
    {
        int i = rel.LastIndexOf('\\');
        return i < 0 ? "" : rel.Substring(0, i);
    }

    /// <summary>zip 목차 (zip이 바뀌면 다시 읽는다). 읽을 수 없으면 null.</summary>
    private static Index Get(string zip)
    {
        FileInfo fi;
        try { fi = new FileInfo(zip); if (!fi.Exists) return null; }
        catch (Exception) { return null; }
        string key = fi.FullName;
        lock (gate)
        {
            if (indexes.TryGetValue(key, out var have) && have.Length == fi.Length && have.Time == fi.LastWriteTimeUtc) return have;
        }
        var x = new Index { Length = fi.Length, Time = fi.LastWriteTimeUtc };
        try
        {
            using (var z = ZipFile.Open(fi.FullName, ZipArchiveMode.Read, Encoding.Latin1))
            {
                foreach (var e in z.Entries)
                {
                    string rel = ZipFolder.SafeRelative(ZipFolder.EntryName(e));
                    if (rel.Length == 0) continue;
                    bool isDir = e.FullName.EndsWith("/") || e.FullName.EndsWith("\\");
                    // 폴더 (항목이 없어도 파일 경로에서)
                    string parent = isDir ? rel : ParentOf(rel);
                    while (parent.Length > 0 && x.Dirs.Add(parent)) parent = ParentOf(parent);
                    if (isDir || x.Files.ContainsKey(rel)) continue;   // 대소문자만 다른 같은 이름은 처음 것
                    x.Files[rel] = e.FullName;
                    x.Shown[rel] = rel;
                    try { x.Times[rel] = e.LastWriteTime.UtcDateTime; } catch (Exception) { x.Times[rel] = fi.LastWriteTimeUtc; }
                }
            }
        }
        catch (Exception) { return null; }
        lock (gate) indexes[key] = x;
        return x;
    }
}
