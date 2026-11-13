using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

/// <summary>곡 파일 하나를 살펴본 결과 (음악 파일 정리).</summary>
public sealed class MusicFileInfo
{
    public string Path = "";
    public string Format = "";
    public long Size;
    public DateTime Modified;
    /// <summary>곡 안의 제목 (IMS · SOP). ROL은 없다.</summary>
    public string Title = "";
    public string ContentHash = "";
    /// <summary>음표 지문 (보이스와 상관없이 모든 음의 시작 · 길이 · 음높이, 박 단위). 음이 없으면 null.</summary>
    public string NoteHash;
    /// <summary>음 목록 (정렬, 비슷한 정도 계산용).</summary>
    public List<string> NoteKeys = new List<string>();
    /// <summary>보이스별 악기 순서 ("piano1,piano2|bdrum|…", 소문자).</summary>
    public string InstrumentKey = "";
    public List<string> Instruments = new List<string>();
    public int NoteCount;
    public int VoicesUsed;
    public int TickBeat;
    public double Tempo;
    public double Seconds;
    public string BankPath;
    public string LyricsPath;
    public bool NumericName;
    public string Error;

    public int Score;
    public List<string> ScoreNotes = new List<string>();
    /// <summary>같은 곡 그룹 (없으면 null).</summary>
    public DuplicateGroup Group;
    /// <summary>그룹에서 남길 것으로 추천된 파일.</summary>
    public bool Keep;

    public string FileName { get { return System.IO.Path.GetFileName(Path); } }
    public string Folder { get { return System.IO.Path.GetDirectoryName(Path) ?? ""; } }
    public bool HasBank { get { return BankPath != null; } }
    public bool HasLyrics { get { return LyricsPath != null; } }
}

/// <summary>같은 곡으로 판정한 파일 묶음.</summary>
public sealed class DuplicateGroup
{
    public int Id;
    public List<MusicFileInfo> Files = new List<MusicFileInfo>();
    /// <summary>판정 이유 (강한 것부터).</summary>
    public SortedSet<MatchKind> Reasons = new SortedSet<MatchKind>();
    public MusicFileInfo Keeper;

    public string ReasonText { get { return string.Join(" · ", Reasons.Select(MusicDuplicateFinder.ReasonName)); } }
}

/// <summary>같은 곡 판정 이유 (작을수록 강함).</summary>
public enum MatchKind { SameBytes = 0, SameNotes = 1, TitleAndInstruments = 2, SizeAndInstruments = 3 }

/// <summary>
/// 폴더의 ROL / IMS / SOP에서 같은 곡을 찾는다.
/// <list type="bullet">
/// <item>같은 바이트 (크기 + SHA-1)</item>
/// <item>같은 음표: 형식 · 박당 틱 · 보이스 배치와 상관없이 모든 음 (시작 · 길이 · 음높이, 1/48박 단위)이 같다 (같은 곡의 ROL과 IMS 등)</item>
/// <item>제목 · 악기 순서가 같고 음 개수가 5% 안</item>
/// <item>형식 · 크기 · 악기 순서가 같고 음표가 90% 이상 같다 (조금 고친 판)</item>
/// </list>
/// 그룹 안에서 남길 파일 점수: 같은 이름 BNK +30, 같은 이름 ISS +30, 뜻 있는 파일 이름 +20, 숫자만 나열한 이름(예: SONG001, 0001) −20, 곡 안 제목 +5.
/// </summary>
public static class MusicDuplicateFinder
{
    public static readonly string[] Extensions = { ".rol", ".ims", ".sop" };

    public static string ReasonName(MatchKind k)
    {
        switch (k)
        {
            case MatchKind.SameBytes: return "같은 파일(바이트)";
            case MatchKind.SameNotes: return "같은 음표";
            case MatchKind.TitleAndInstruments: return "제목 · 악기 순서 같음";
            default: return "크기 · 악기 순서 같음, 음표 90% 이상 같음";
        }
    }

    public static bool IsMusicFile(string path)
    {
        return Array.IndexOf(Extensions, System.IO.Path.GetExtension(path).ToLowerInvariant()) >= 0;
    }

    public static List<string> ListFiles(string dir, bool recursive)
    {
        var l = new List<string>();
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true }))
                if (IsMusicFile(f)) l.Add(f);
        }
        catch (Exception) { }
        l.Sort(StringComparer.OrdinalIgnoreCase);
        return l;
    }

    private static readonly Regex NumericRe = new Regex(@"^([A-Za-z_\-~ ]{0,5}\d{3,}|[\d_\-\. ]+)$", RegexOptions.Compiled);

    /// <summary>숫자만 나열된 이름인지 (예: 0001, SONG001, MUS00012, ????001). 이런 이름은 남길 순위를 낮춘다.</summary>
    public static bool IsNumericName(string stem)
    {
        stem = (stem ?? "").Trim();
        return stem.Length > 0 && NumericRe.IsMatch(stem);
    }

    /// <summary>파일 하나를 읽어 비교에 쓸 값을 만든다. 읽지 못하면 Error에 이유 (크기 · 해시로만 비교).</summary>
    public static MusicFileInfo Analyze(string path)
    {
        var info = new MusicFileInfo { Path = path, Format = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant() };
        byte[] bytes;
        try
        {
            var fi = new FileInfo(path);
            info.Size = fi.Length;
            info.Modified = fi.LastWriteTime;
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) { info.Error = ex.Message; return info; }
        using (var sha = SHA1.Create()) info.ContentHash = Hex(sha.ComputeHash(bytes));
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        info.NumericName = IsNumericName(stem);
        info.BankPath = Companion(path, ".bnk");
        info.LyricsPath = Companion(path, ".iss");

        try
        {
            Song song;
            string ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".sop")
            {
                var sop = SopFormat.Read(new MemoryStream(bytes));
                List<AdlibInstrument> insts;
                song = SopConvert.ToSong(sop, out insts);
                info.Title = sop.Title ?? "";
            }
            else if (ext == ".ims")
            {
                song = new ImsFormat().Read(new MemoryStream(bytes));
                info.Title = song.Title ?? "";
            }
            else song = new RolFormat().Read(new MemoryStream(bytes));
            FillFromSong(info, song);
        }
        catch (Exception ex) { info.Error = ex.Message; }
        return info;
    }

    /// <summary>같은 폴더 · 같은 이름의 짝 파일 (대소문자 무시). 없으면 null.</summary>
    public static string Companion(string path, string ext)
    {
        string dir = System.IO.Path.GetDirectoryName(path);
        string stem = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrEmpty(dir)) dir = ".";
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, stem + ".*"))
                if (string.Equals(System.IO.Path.GetFileNameWithoutExtension(f), stem, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(System.IO.Path.GetExtension(f), ext, StringComparison.OrdinalIgnoreCase)) return f;
        }
        catch (Exception) { }
        return null;
    }

    private static void FillFromSong(MusicFileInfo info, Song song)
    {
        info.TickBeat = song.TickBeat;
        info.Tempo = song.BasicTempo;
        int tb = Math.Max(1, song.TickBeat);
        var notes = new List<string>();
        var voiceKeys = new List<string>();
        for (int v = 0; v < song.VoiceCount; v++)
        {
            var vt = song.Voices[v];
            if (vt.Notes.Count > 0) info.VoicesUsed++;
            var names = new List<string>();
            foreach (var t in vt.Timbres)
            {
                string n = (t.InstrumentName ?? "").Trim().ToLowerInvariant();
                if (names.Count == 0 || names[names.Count - 1] != n) names.Add(n);
                if (n.Length > 0 && !info.Instruments.Contains(n)) info.Instruments.Add(n);
            }
            if (vt.Notes.Count > 0 || names.Count > 0) voiceKeys.Add(string.Join(",", names));
            foreach (var n in vt.Notes)
            {
                long s = (long)Math.Round(n.StartTick * 48.0 / tb), d = (long)Math.Round(n.Duration * 48.0 / tb);
                notes.Add(s.ToString("D9") + ":" + n.Pitch + ":" + d);
            }
        }
        // 뒤쪽의 빈 보이스는 빼고 비교 (ROL 11보이스 / IMS · SOP 보이스 수 차이)
        while (voiceKeys.Count > 0 && voiceKeys[voiceKeys.Count - 1].Length == 0) voiceKeys.RemoveAt(voiceKeys.Count - 1);
        info.InstrumentKey = string.Join("|", voiceKeys);
        info.NoteCount = notes.Count;
        if (notes.Count > 0)
        {
            notes.Sort(StringComparer.Ordinal);
            info.NoteKeys = notes;
            using (var sha = SHA1.Create()) info.NoteHash = Hex(sha.ComputeHash(Encoding.ASCII.GetBytes(string.Join(";", notes))));
        }
        try { info.Seconds = new TempoMap(song).SecondsAt(song.LengthTicks); } catch (Exception) { }
    }

    private static string Hex(byte[] h)
    {
        var sb = new StringBuilder(h.Length * 2);
        foreach (var b in h) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static string NormTitle(string t)
    {
        t = Regex.Replace((t ?? "").Trim().ToLowerInvariant(), @"\s+", " ");
        return t.Length >= 2 ? t : null;
    }

    /// <summary>남길 파일 점수와 그 이유.</summary>
    public static void ScoreFile(MusicFileInfo f)
    {
        f.ScoreNotes.Clear();
        int s = 0;
        if (f.HasBank) { s += 30; f.ScoreNotes.Add("같은 이름 BNK +30"); }
        if (f.HasLyrics) { s += 30; f.ScoreNotes.Add("같은 이름 ISS +30"); }
        if (f.NumericName) { s -= 20; f.ScoreNotes.Add("숫자만 나열된 이름 −20"); }
        else { s += 20; f.ScoreNotes.Add("뜻 있는 파일 이름 +20"); }
        if (NormTitle(f.Title) != null) { s += 5; f.ScoreNotes.Add("곡 안 제목 +5"); }
        if (f.Error != null) { s -= 10; f.ScoreNotes.Add("읽기 오류 −10"); }
        f.Score = s;
    }

    /// <summary>같은 곡 그룹을 만든다 (파일 2개 이상인 것만). 각 파일의 Score · Group · Keep을 채운다.</summary>
    public static List<DuplicateGroup> FindGroups(IList<MusicFileInfo> files)
    {
        int n = files.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        Func<int, int> find = null;
        find = x => { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };
        var links = new List<Tuple<int, int, MatchKind>>();
        Action<int, int, MatchKind> union = (a, b, k) =>
        {
            links.Add(Tuple.Create(a, b, k));
            int ra = find(a), rb = find(b);
            if (ra != rb) parent[rb] = ra;
        };

        Action<Func<MusicFileInfo, string>, MatchKind> byKey = (key, kind) =>
        {
            var first = new Dictionary<string, int>();
            for (int i = 0; i < n; i++)
            {
                string k = key(files[i]);
                if (k == null) continue;
                int j;
                if (first.TryGetValue(k, out j)) union(j, i, kind); else first[k] = i;
            }
        };
        byKey(f => f.ContentHash.Length > 0 ? f.Size + ":" + f.ContentHash : null, MatchKind.SameBytes);
        byKey(f => f.NoteHash, MatchKind.SameNotes);
        // 형식 · 크기 · 악기 순서가 같은 묶음 안에서 음표가 90% 이상 같으면
        var sizeBuckets = new Dictionary<string, List<int>>();
        for (int i = 0; i < n; i++)
        {
            var f = files[i];
            if (f.Error != null || f.InstrumentKey.Length == 0) continue;
            string k = f.Format + ":" + f.Size + ":" + f.InstrumentKey;
            List<int> l;
            if (!sizeBuckets.TryGetValue(k, out l)) sizeBuckets[k] = l = new List<int>();
            l.Add(i);
        }
        foreach (var l in sizeBuckets.Values)
            for (int a = 0; a < l.Count; a++)
                for (int b = a + 1; b < l.Count; b++)
                    if (NoteSimilarity(files[l[a]], files[l[b]]) >= 0.9) union(l[a], l[b], MatchKind.SizeAndInstruments);
        // 제목 + 악기 순서: 같은 묶음 안에서 음 개수가 5% 안이면
        var buckets = new Dictionary<string, List<int>>();
        for (int i = 0; i < n; i++)
        {
            var f = files[i];
            string t = NormTitle(f.Title);
            if (t == null || f.Error != null || f.InstrumentKey.Length == 0) continue;
            string k = t + "\n" + f.InstrumentKey;
            List<int> l;
            if (!buckets.TryGetValue(k, out l)) buckets[k] = l = new List<int>();
            l.Add(i);
        }
        foreach (var l in buckets.Values)
            for (int a = 0; a < l.Count; a++)
                for (int b = a + 1; b < l.Count; b++)
                {
                    int na = files[l[a]].NoteCount, nb = files[l[b]].NoteCount;
                    if (Math.Abs(na - nb) <= Math.Max(1, Math.Max(na, nb) / 20)) union(l[a], l[b], MatchKind.TitleAndInstruments);
                }

        foreach (var f in files) { ScoreFile(f); f.Group = null; f.Keep = false; }
        var groups = new Dictionary<int, DuplicateGroup>();
        for (int i = 0; i < n; i++)
        {
            int r = find(i);
            DuplicateGroup g;
            if (!groups.TryGetValue(r, out g)) groups[r] = g = new DuplicateGroup();
            g.Files.Add(files[i]);
        }
        foreach (var lk in links)
        {
            DuplicateGroup g;
            if (groups.TryGetValue(find(lk.Item1), out g)) g.Reasons.Add(lk.Item3);
        }
        var result = new List<DuplicateGroup>();
        foreach (var g in groups.Values)
        {
            if (g.Files.Count < 2) continue;
            g.Files.Sort(CompareKeep);
            g.Keeper = g.Files[0];
            foreach (var f in g.Files) { f.Group = g; f.Keep = ReferenceEquals(f, g.Keeper); }
            result.Add(g);
        }
        result.Sort((a, b) => string.Compare(a.Keeper.Path, b.Keeper.Path, StringComparison.OrdinalIgnoreCase));
        for (int i = 0; i < result.Count; i++) result[i].Id = i + 1;
        return result;
    }

    /// <summary>음표가 같은 정도 (0~1): 같은 음 개수 / 더 많은 쪽 음 개수. 음이 없으면 0.</summary>
    public static double NoteSimilarity(MusicFileInfo a, MusicFileInfo b)
    {
        int na = a.NoteKeys.Count, nb = b.NoteKeys.Count;
        if (na == 0 || nb == 0) return 0;
        int i = 0, j = 0, same = 0;
        while (i < na && j < nb)
        {
            int c = string.CompareOrdinal(a.NoteKeys[i], b.NoteKeys[j]);
            if (c == 0) { same++; i++; j++; }
            else if (c < 0) i++;
            else j++;
        }
        return (double)same / Math.Max(na, nb);
    }

    /// <summary>남길 순서: 점수 → 음 개수 → 짧은 파일 이름 → 경로.</summary>
    private static int CompareKeep(MusicFileInfo a, MusicFileInfo b)
    {
        int c = b.Score.CompareTo(a.Score);
        if (c != 0) return c;
        c = b.NoteCount.CompareTo(a.NoteCount);
        if (c != 0) return c;
        c = a.FileName.Length.CompareTo(b.FileName.Length);
        if (c != 0) return c;
        return string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 정리할 때 같이 옮기거나 지울 짝 파일 (같은 이름 BNK · ISS). 같은 폴더에 같은 이름의 다른 곡 파일이 남아 있으면 그 곡이 쓰므로 뺀다.
    /// </summary>
    public static List<string> CompanionsToTake(MusicFileInfo f, ICollection<string> remainingMusicPaths)
    {
        var l = new List<string>();
        string stem = System.IO.Path.GetFileNameWithoutExtension(f.Path);
        foreach (var other in remainingMusicPaths)
            if (!string.Equals(other, f.Path, StringComparison.OrdinalIgnoreCase)
                && string.Equals(System.IO.Path.GetDirectoryName(other), f.Folder, StringComparison.OrdinalIgnoreCase)
                && string.Equals(System.IO.Path.GetFileNameWithoutExtension(other), stem, StringComparison.OrdinalIgnoreCase))
                return l;
        if (f.BankPath != null && File.Exists(f.BankPath)) l.Add(f.BankPath);
        if (f.LyricsPath != null && File.Exists(f.LyricsPath)) l.Add(f.LyricsPath);
        return l;
    }
}
