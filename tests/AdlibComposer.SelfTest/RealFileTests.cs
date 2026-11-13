using System;
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
