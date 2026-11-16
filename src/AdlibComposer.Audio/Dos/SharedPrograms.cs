using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace AdlibComposer.Audio.Dos;

/// <summary>
/// 여러 게임이 같이 쓰는 드라이버 · 재생기 (NAX.COM · DOFMDX98.COM · FMUDRV2 …): hoot 항목의 셸 프로그램이 그 zip에 없으면
/// 같은 폴더(예: pc98\)의 다른 zip에서 같은 이름의 .COM / .EXE를 찾는다 (X68000: zmusic.x · cos68snd.bin 같은 파일은 이름 그대로). 판이 여럿이면 가장 많은 zip에 든 같은 내용을 고른다.
/// <para>폴더마다 처음 한 번 zip 목록(중앙 디렉터리)만 훑어 이름 → zip 색인을 만든다 (1,700개에 1~2초).</para>
/// </summary>
public static class SharedPrograms
{
    private static readonly object gate = new object();
    private static readonly Dictionary<string, Dictionary<string, List<(string zip, string entry)>>> indexes =
        new Dictionary<string, Dictionary<string, List<(string, string)>>>(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, byte[]> found = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, List<(string zip, string entry)>> Index(string folder)
    {
        lock (gate)
        {
            if (indexes.TryGetValue(folder, out var have)) return have;
            var idx = new Dictionary<string, List<(string, string)>>(StringComparer.OrdinalIgnoreCase);
            foreach (var z in Directory.EnumerateFiles(folder, "*.zip"))
            {
                try
                {
                    using (var zip = ZipFile.OpenRead(z))
                        foreach (var e in zip.Entries)
                        {
                            if (e.Name.Length == 0) continue;
                            string key = e.Name.ToUpperInvariant();
                            if (!idx.TryGetValue(key, out var l)) idx[key] = l = new List<(string, string)>();
                            l.Add((z, e.FullName));
                        }
                }
                catch (Exception) { }
            }
            indexes[folder] = idx;
            return idx;
        }
    }

    /// <summary>프로그램 이름("NAX", "fmudrv2.com" …)의 파일 (이름 · 내용). 확장자가 있으면 그 이름 그대로 찾는다. 없으면 null.</summary>
    public static (string name, byte[] data)? Find(string folder, string program, string exceptZip = null)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || string.IsNullOrWhiteSpace(program)) return null;
        string p = Path.GetFileName(program.Trim()).ToUpperInvariant();
        var names = Path.HasExtension(p) ? new[] { p } : new[] { p + ".COM", p + ".EXE" };
        var idx = Index(folder);
        foreach (var n in names)
        {
            string cacheKey = folder + "|" + n;
            lock (gate) if (found.TryGetValue(cacheKey, out var hit)) return hit == null ? ((string, byte[])?)null : (n, hit);
            if (!idx.TryGetValue(n, out var list)) continue;
            // 같은 내용끼리 세어 가장 많은 것
            var groups = new Dictionary<string, (int count, byte[] data)>();
            using (var sha = SHA1.Create())
                foreach (var (zip, entry) in list.Where(x => !string.Equals(x.zip, exceptZip, StringComparison.OrdinalIgnoreCase)).Take(200))
                {
                    try
                    {
                        using (var z = ZipFile.OpenRead(zip))
                        {
                            var e = z.GetEntry(entry);
                            if (e == null) continue;
                            byte[] d;
                            using (var s = e.Open()) using (var ms = new MemoryStream()) { s.CopyTo(ms); d = ms.ToArray(); }
                            string h = Convert.ToBase64String(sha.ComputeHash(d));
                            groups[h] = groups.TryGetValue(h, out var g) ? (g.count + 1, g.data) : (1, d);
                        }
                    }
                    catch (Exception) { }
                }
            var best = groups.Values.OrderByDescending(g => g.count).Select(g => g.data).FirstOrDefault();
            lock (gate) found[cacheKey] = best;
            if (best != null) return (n, best);
        }
        return null;
    }
}
