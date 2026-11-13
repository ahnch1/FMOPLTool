using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>_incoming 폴더(실제 ROL/BNK/INS 샘플)가 있으면 읽기·의미 보존 재저장을 검사한다. 없으면 건너뛴다.</summary>
public static class RealFileTests
{
    public static void Run(Action<string, bool, string> check)
    {
        string dir = FindDir("_incoming");
        if (dir == null) { Console.WriteLine("SKIP  real sample files (_incoming not found)"); return; }

        foreach (var p in Directory.GetFiles(dir, "*.*").Where(f => f.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)))
        {
            var fmt = new BnkFormat();
            InstrumentBank a;
            using (var fs = File.OpenRead(p)) a = fmt.Read(fs);
            var ms = new MemoryStream(); fmt.Write(a, ms); ms.Position = 0;
            var b = fmt.Read(ms);
            bool same = a.Instruments.Count(i => i.IsUsed) == b.Instruments.Count(i => i.IsUsed);
            foreach (var i in a.Instruments.Where(x => x.IsUsed))
            {
                var j = b.Find(i.Name);
                same &= j != null && SameRaw(i, j);
            }
            int carriers = a.Instruments.Count(i => i.IsUsed && i.Carrier.TotalLevel == 0);
            check("Real BNK " + Path.GetFileName(p) + " re-save keeps instruments", same, "used=" + a.UsedCount);
            check("Real BNK " + Path.GetFileName(p) + " carrier TL mostly 0 (TL = OPL register)", carriers * 2 > a.UsedCount, carriers + "/" + a.UsedCount);
        }
        foreach (var p in Directory.GetFiles(dir, "*.*").Where(f => f.EndsWith(".ins", StringComparison.OrdinalIgnoreCase)))
        {
            var fmt = new InsFormat();
            var bytes = File.ReadAllBytes(p);
            var ins = fmt.Read(new MemoryStream(bytes), Path.GetFileNameWithoutExtension(p));
            var ms = new MemoryStream(); fmt.Write(ins, ms);
            check("Real INS " + Path.GetFileName(p) + " byte-identical re-save", bytes.Length != 80 || ms.ToArray().SequenceEqual(bytes), "");
        }
        RunIms(dir, check);
        foreach (var p in Directory.GetFiles(dir, "*.*").Where(f => f.EndsWith(".rol", StringComparison.OrdinalIgnoreCase)))
        {
            var fmt = new RolFormat();
            Song a; long consumed, len;
            using (var fs = File.OpenRead(p)) { a = fmt.Read(fs); consumed = fs.Position; len = fs.Length; }
            check("Real ROL " + Path.GetFileName(p) + " parsed to end of file", consumed == len, consumed + "/" + len);
            int lo = 999, hi = 0;
            foreach (var v in a.Voices) foreach (var n in v.Notes) { lo = Math.Min(lo, n.Pitch); hi = Math.Max(hi, n.Pitch); }
            check("Real ROL " + Path.GetFileName(p) + " notes in 12..107", lo >= Song.MinNote && hi <= Song.MaxNote, lo + ".." + hi);
            var ms = new MemoryStream(); fmt.Write(a, ms);
            check("Real ROL " + Path.GetFileName(p) + " re-save same size", ms.Length == len, ms.Length + " vs " + len);
            ms.Position = 0;
            var b = fmt.Read(ms);
            check("Real ROL " + Path.GetFileName(p) + " re-save keeps events", SameSong(a, b), "");
        }
    }

    /// <summary>IMS: 읽기, 재저장 후 같은 곡, 같은 이름의 ROL이 있으면 내용 비교.</summary>
    public static void RunIms(string dir, Action<string, bool, string> check)
    {
        foreach (var p in Directory.GetFiles(dir, "*.*").Where(f => f.EndsWith(".ims", StringComparison.OrdinalIgnoreCase)))
        {
            string n = Path.GetFileName(p);
            var fmt = new ImsFormat();
            Song a;
            using (var fs = File.OpenRead(p)) a = fmt.Read(fs);
            int notes = a.Voices.Sum(v => v.Notes.Count);
            check("Real IMS " + n + " has notes and instruments", notes > 0 && a.Voices.Any(v => v.Timbres.Count > 0), "notes=" + notes + " tickBeat=" + a.TickBeat);
            var ms = new MemoryStream(); fmt.Write(a, ms); ms.Position = 0;
            var b = fmt.Read(ms);
            string why = Diff(a, b, 0.006f);
            check("Real IMS " + n + " re-save keeps song", why == null, why ?? "");

            string rolPath = Path.ChangeExtension(p, ".ROL");
            if (!File.Exists(rolPath)) continue;
            Song rol;
            using (var fs = File.OpenRead(rolPath)) rol = new RolFormat().Read(fs);
            why = Diff(rol, a, 0.006f);
            check("Real IMS " + n + " == " + Path.GetFileName(rolPath) + " (notes/timbres/pitch/volume/tempo)", why == null, why ?? "");
        }
    }

    /// <summary>두 곡이 같은 음악인지 비교 (틱 해상도가 다르면 배율을 맞춤, 중복/무의미 이벤트는 정규화). 다르면 이유를 반환.</summary>
    public static string Diff(Song a, Song b, float tol)
    {
        if (a.IsMelodic != b.IsMelodic) return "mode";
        if (Math.Abs(a.BasicTempo - b.BasicTempo) > 0.5f) return "tempo " + a.BasicTempo + "/" + b.BasicTempo;
        int l = Lcm(a.TickBeat, b.TickBeat), fa = l / a.TickBeat, fb = l / b.TickBeat;
        var ta = Norm(a.TempoEvents.Select(t => (t.Tick * fa, t.Multiplier)), 1f);
        var tb = Norm(b.TempoEvents.Select(t => (t.Tick * fb, t.Multiplier)), 1f);
        if (!SameF(ta, tb, 0.01f)) return "tempo events";
        for (int v = 0; v < a.VoiceCount; v++)
        {
            var x = a.Voices[v]; var y = b.Voices[v];
            var nx = x.Notes.Select(q => (q.StartTick * fa, q.Duration * fa, q.Pitch)).ToList();
            var ny = y.Notes.Select(q => (q.StartTick * fb, q.Duration * fb, q.Pitch)).ToList();
            if (!nx.SequenceEqual(ny))
            {
                int i = 0; while (i < Math.Min(nx.Count, ny.Count) && nx[i] == ny[i]) i++;
                return "v" + v + " notes " + nx.Count + "/" + ny.Count + " first diff #" + i + " " + (i < nx.Count ? nx[i].ToString() : "-") + " vs " + (i < ny.Count ? ny[i].ToString() : "-");
            }
            if (nx.Count == 0) continue;   // 소리가 없는 보이스의 악기/볼륨은 비교하지 않음
            var ix = Timbres(x, fa); var iy = Timbres(y, fb);
            if (!ix.SequenceEqual(iy)) return "v" + v + " timbres " + string.Join(",", ix) + " | " + string.Join(",", iy);
            if (!SameF(Norm(x.Pitches.Select(e => (e.Tick * fa, e.Pitch)), 1f), Norm(y.Pitches.Select(e => (e.Tick * fb, e.Pitch)), 1f), tol)) return "v" + v + " pitch";
            // 볼륨은 노트가 시작될 때의 값만 들린다 → 각 노트 시작 시점의 볼륨 비교
            foreach (var q in x.Notes)
            {
                float va = VolAt(x, q.StartTick), vb = VolAt(y, q.StartTick * fa / fb);
                if (Math.Abs(va - vb) > tol) return "v" + v + " volume at " + q.StartTick + ": " + va + "/" + vb;
            }
        }
        return null;
    }

    private static float VolAt(VoiceTrack v, int tick)
    {
        float r = 1f; int best = -1;
        foreach (var e in v.Volumes) if (e.Tick <= tick && e.Tick >= best) { best = e.Tick; r = e.Volume; }
        return r;
    }

    private static List<string> Timbres(VoiceTrack v, int f)
    {
        var r = new List<string>(); string cur = null;
        foreach (var e in v.Timbres.OrderBy(e => e.Tick))
        {
            string n = e.InstrumentName.ToUpperInvariant();
            if (n == cur) continue;
            if (r.Count > 0 && r[r.Count - 1].StartsWith((e.Tick * f) + ":")) r.RemoveAt(r.Count - 1);
            r.Add((e.Tick * f) + ":" + n); cur = n;
        }
        return r;
    }

    /// <summary>같은 틱은 마지막 값만, 직전 값과 같은 이벤트는 제거 (초기값 = def).</summary>
    private static List<(int, float)> Norm(IEnumerable<(int, float)> evs, float def)
    {
        var byTick = new SortedDictionary<int, float>();
        foreach (var e in evs) byTick[e.Item1] = e.Item2;
        var r = new List<(int, float)>(); float cur = def;
        foreach (var kv in byTick) { if (Math.Abs(kv.Value - cur) > 1e-4f) { r.Add((kv.Key, kv.Value)); cur = kv.Value; } }
        return r;
    }

    private static bool SameF(List<(int, float)> a, List<(int, float)> b, float tol)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i].Item1 != b[i].Item1 || Math.Abs(a[i].Item2 - b[i].Item2) > tol) return false;
        return true;
    }

    private static int Lcm(int a, int b) { int x = a, y = b; while (y != 0) { int t = x % y; x = y; y = t; } return a / x * b; }

    private static bool SameRaw(AdlibInstrument a, AdlibInstrument b)
    {
        foreach (var pair in new[] { (a.Modulator, b.Modulator), (a.Carrier, b.Carrier) })
        {
            var x = pair.Item1; var y = pair.Item2;
            if (x.Ksl != y.Ksl || x.Multiple != y.Multiple || x.Feedback != y.Feedback || x.Attack != y.Attack || x.Sustain != y.Sustain
                || x.Eg != y.Eg || x.Decay != y.Decay || x.ReleaseRate != y.ReleaseRate || x.TotalLevel != y.TotalLevel || x.Am != y.Am
                || x.Vib != y.Vib || x.Ksr != y.Ksr || x.Con != y.Con || x.WaveSelect != y.WaveSelect) return false;
        }
        return a.Percussive == b.Percussive && a.VoiceNum == b.VoiceNum;
    }

    private static bool SameSong(Song a, Song b)
    {
        if (a.TickBeat != b.TickBeat || a.BeatsPerMeasure != b.BeatsPerMeasure || a.IsMelodic != b.IsMelodic || a.BasicTempo != b.BasicTempo) return false;
        if (!a.TempoEvents.Select(t => (t.Tick, t.Multiplier)).SequenceEqual(b.TempoEvents.Select(t => (t.Tick, t.Multiplier)))) return false;
        for (int v = 0; v < Song.MaxVoices; v++)
        {
            var x = a.Voices[v]; var y = b.Voices[v];
            if (!x.Notes.Select(n => (n.StartTick, n.Duration, n.Pitch)).SequenceEqual(y.Notes.Select(n => (n.StartTick, n.Duration, n.Pitch)))) return false;
            if (!x.Timbres.Select(t => (t.Tick, t.InstrumentName.ToUpperInvariant(), t.Unknown)).SequenceEqual(y.Timbres.Select(t => (t.Tick, t.InstrumentName.ToUpperInvariant(), t.Unknown)))) return false;
            if (!x.Volumes.Select(t => (t.Tick, t.Volume)).SequenceEqual(y.Volumes.Select(t => (t.Tick, t.Volume)))) return false;
            if (!x.Pitches.Select(t => (t.Tick, t.Pitch)).SequenceEqual(y.Pitches.Select(t => (t.Tick, t.Pitch)))) return false;
        }
        return true;
    }

    private static string FindDir(string name)
    {
        var d = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (d != null)
        {
            string c = Path.Combine(d.FullName, name);
            if (Directory.Exists(c)) return c;
            d = d.Parent;
        }
        return null;
    }
}
