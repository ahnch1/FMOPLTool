using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AdlibComposer.Audio.Dos;

/// <summary>
/// DOS 흉내가 보는 파일들 (메모리 안): 이름은 대문자 · 역슬래시 경로 ("MDAT\GAOT.FN1"), 드라이브 글자와 앞 '\'는 버린다.
/// 폴더(zip을 푼 곳)에서 채우거나 하나씩 더한다. 쓰기 · 만들기 · 지우기도 메모리 안에서만 (원래 파일은 바꾸지 않는다).
/// </summary>
public sealed class DosFileSystem
{
    private readonly Dictionary<string, byte[]> files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
    /// <summary>현재 폴더 ("" = 뿌리, "MDAT" …).</summary>
    public string CurrentDir = "";

    public IEnumerable<string> Names { get { return files.Keys; } }

    /// <summary>폴더 아래 모든 파일 (하위 폴더 포함).</summary>
    public static DosFileSystem FromDirectory(string dir)
    {
        var fs = new DosFileSystem();
        foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(dir, f);
            fs.Add(rel, File.ReadAllBytes(f));
        }
        return fs;
    }

    public void Add(string path, byte[] data) { files[Key(path, true)] = data; }

    /// <summary>DOS 경로 → 열쇠: 대문자, '/' → '\', 드라이브 · 앞 '\' 없앰, '.' · '..' 풀기. root가 아니면 현재 폴더 기준.</summary>
    public string Key(string path, bool fromRoot = false)
    {
        string p = (path ?? "").Replace('/', '\\').Trim().ToUpperInvariant();
        if (p.Length >= 2 && p[1] == ':') p = p.Substring(2);
        bool abs = p.StartsWith("\\") || fromRoot;
        var parts = new List<string>();
        if (!abs && CurrentDir.Length > 0) parts.AddRange(CurrentDir.Split('\\'));
        foreach (var seg in p.Split('\\'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(seg.TrimEnd('.', ' '));
        }
        return string.Join("\\", parts);
    }

    public bool TryGet(string path, out byte[] data)
    {
        if (files.TryGetValue(Key(path), out data)) return true;
        // 현재 폴더에 없으면 뿌리에서도 (hoot 목록은 대부분 뿌리에 둔다)
        return files.TryGetValue(Key(path, true), out data);
    }

    public bool Exists(string path) { return TryGet(path, out _); }
    public void Put(string path, byte[] data) { files[ResolveExisting(path) ?? Key(path)] = data; }
    public bool Delete(string path) { var k = ResolveExisting(path); return k != null && files.Remove(k); }

    public bool Rename(string from, string to)
    {
        var k = ResolveExisting(from);
        if (k == null || Exists(to)) return false;
        var d = files[k]; files.Remove(k); files[Key(to)] = d;
        return true;
    }

    /// <summary>있는 파일의 열쇠 (현재 폴더 → 뿌리), 없으면 null.</summary>
    public string ResolveExisting(string path)
    {
        string a = Key(path);
        if (files.ContainsKey(a)) return a;
        string b = Key(path, true);
        return files.ContainsKey(b) ? b : null;
    }

    /// <summary>폴더가 있는가 (그 아래 파일이 하나라도 있으면).</summary>
    public bool DirectoryExists(string path)
    {
        string k = Key(path);
        if (k.Length == 0) return true;
        return files.Keys.Any(f => f.StartsWith(k + "\\", StringComparison.Ordinal));
    }

    /// <summary>찾기 무늬 (예: "MDAT\*.FN1", "*.*"): 그 폴더 바로 아래의 맞는 파일 이름 (경로 빼고).</summary>
    public List<(string name, int size)> Find(string pattern)
    {
        string k = Key(pattern);
        int cut = k.LastIndexOf('\\');
        string dir = cut < 0 ? "" : k.Substring(0, cut), mask = cut < 0 ? k : k.Substring(cut + 1);
        if (mask.Length == 0) mask = "*.*";
        var l = new List<(string, int)>();
        foreach (var kv in files.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            int c = kv.Key.LastIndexOf('\\');
            string d = c < 0 ? "" : kv.Key.Substring(0, c), n = c < 0 ? kv.Key : kv.Key.Substring(c + 1);
            if (d == dir && Match83(mask, n)) l.Add((n, kv.Value.Length));
        }
        return l;
    }

    /// <summary>8.3 무늬 맞추기: '*'는 그 칸 끝까지 '?', '?'는 아무 글자 (또는 없음).</summary>
    public static bool Match83(string mask, string name)
    {
        string Pad(string s, int n) { return s.Length >= n ? s.Substring(0, n) : s.PadRight(n); }
        string Fill(string m, int n)
        {
            int star = m.IndexOf('*');
            if (star >= 0) m = m.Substring(0, star).PadRight(n, '?');
            return Pad(m, n);
        }
        Split(mask, out string mb, out string me);
        Split(name, out string nb, out string ne);
        string a = Fill(mb, 8) + Fill(me, 3), b = Pad(nb, 8) + Pad(ne, 3);
        for (int i = 0; i < 11; i++) if (a[i] != '?' && a[i] != b[i]) return false;
        return true;
    }

    private static void Split(string s, out string b, out string e)
    {
        int dot = s.LastIndexOf('.');
        if (dot < 0) { b = s; e = ""; } else { b = s.Substring(0, dot); e = s.Substring(dot + 1); }
    }
}
