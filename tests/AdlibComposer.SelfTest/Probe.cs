using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>실제 파일 점검용: --probe file... (구조 덤프), --render song.rol bank.bnk out.wav [seconds]</summary>
public static class Probe
{
    public static int Run(string[] args)
    {
        if (args[0] == "--drum")
        {
            InstrumentBank bank;
            using (var fs = File.OpenRead(args[1])) bank = new BnkFormat().Read(fs);
            foreach (var name in new[] { "bdrum", "bdrum1", "snare1", "tom1", "cymbal1", "hihat1", "piano1" })
            {
                var ins = bank.Find(name);
                foreach (int note in new[] { 36, 48, 60 })
                {
                    var synth = new AdlibComposer.Audio.OplSynth();
                    int voice = name.StartsWith("bdrum") ? 6 : name == "snare1" ? 7 : name == "tom1" ? 8 : name == "cymbal1" ? 9 : name == "hihat1" ? 10 : 0;
                    synth.SetRhythmMode(voice >= 6);
                    synth.SetInstrument(voice, ins);
                    synth.NoteOn(voice, note);
                    int peak = 0;
                    for (int i = 0; i < 49716 / 4; i++) peak = Math.Max(peak, Math.Abs((int)synth.Generate()));
                    Console.WriteLine($"{name,-8} voice {voice,2} note {note}: peak {peak}");
                }
            }
            return 0;
        }
        if (args[0] == "--drumshape")
        {
            InstrumentBank bank;
            using (var fs = File.OpenRead(args[1])) bank = new BnkFormat().Read(fs);
            foreach (var c in new[] { ("bdrum", 6, true, 36), ("bdrum", 6, true, 60), ("bdrum", 6, false, 60), ("bdrum", 0, false, 60), ("bdrum", 0, true, 60), ("piano1", 0, false, 60) })
            {
                var synth = new AdlibComposer.Audio.OplSynth();
                synth.SetRhythmMode(c.Item3);
                synth.SetInstrument(c.Item2, bank.Find(c.Item1));
                synth.NoteOn(c.Item2, c.Item4);
                int rate = synth.NativeRate, n = rate * 2, crossings = 0, peak = 0, lastLoud = 0;
                var buf = new short[n];
                for (int i = 0; i < n; i++) { buf[i] = synth.Generate(); peak = Math.Max(peak, Math.Abs((int)buf[i])); }
                for (int i = 1; i < n; i++)
                {
                    if (Math.Abs((int)buf[i]) > peak / 100) lastLoud = i;     // -40dB
                    if (i < rate / 10 && buf[i - 1] < 0 && buf[i] >= 0) crossings++;
                }
                Console.WriteLine($"{c.Item1,-7} voice {c.Item2} rhythm={c.Item3,-5} note {c.Item4}: peak {peak,5}  audible {lastLoud * 1000 / rate,5} ms  ~{crossings * 10} Hz");
            }
            return 0;
        }
        if (args[0] == "--latency")
        {
            Song song; InstrumentBank bank;
            using (var fs = File.OpenRead(args[1])) song = FormatRegistry.CreateDefault().FindSong(args[1]).Read(fs);
            using (var fs = File.OpenRead(args[2])) bank = new BnkFormat().Read(fs);
            var eng = new AdlibComposer.Audio.AudioEngine();
            eng.MasterVolume = 0.05;
            eng.OpenDevice();
            System.Threading.Thread.Sleep(300);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            eng.Play(song, bank, 0);
            double tps = song.BasicTempo / 60.0 * song.TickBeat;
            double firstMove = -1;
            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(10);
                double heard = eng.PositionTick;
                if (heard > 0 && firstMove < 0) firstMove = sw.Elapsed.TotalMilliseconds;
                if (i % 8 == 7) Console.WriteLine($"t={sw.Elapsed.TotalMilliseconds,6:0}ms heard={heard,7:0.00} ticks ({heard / tps * 1000,6:0}ms of song)");
            }
            Console.WriteLine($"cursor starts moving {firstMove:0}ms after Play (= audible start)");
            eng.Stop(); eng.Dispose();
            return 0;
        }
        if (args[0] == "--render")
        {
            Song song; InstrumentBank bank;
            using (var fs = File.OpenRead(args[1])) song = FormatRegistry.CreateDefault().FindSong(args[1]).Read(fs);
            using (var fs = File.OpenRead(args[2])) bank = new BnkFormat().Read(fs);
            double sec = args.Length > 4 ? double.Parse(args[4]) : 20;
            var missing = new HashSet<string>();
            foreach (var v in song.Voices) foreach (var t in v.Timbres) if (bank.Find(t.InstrumentName) == null) missing.Add(t.InstrumentName);
            Console.WriteLine("missing instruments: " + string.Join(",", missing));
            var pcm = DemoRender.Render(song, bank, sec);
            DemoRender.WriteWav(args[3], pcm, AdlibComposer.Audio.AudioEngine.OutputRate);
            int peak = 0; double sum = 0;
            foreach (short s in pcm) { peak = Math.Max(peak, Math.Abs((int)s)); sum += (double)s * s; }
            Console.WriteLine("rendered peak=" + peak + " rms=" + Math.Sqrt(sum / pcm.Length).ToString("0"));
            return 0;
        }
        for (int i = 1; i < args.Length; i++)
        {
            string p = args[i];
            Console.WriteLine("==== " + p);
            try
            {
                string ext = Path.GetExtension(p).ToLowerInvariant();
                if (ext == ".rol" || ext == ".ims") DumpRol(p);
                else if (ext == ".bnk") DumpBnk(p);
                else if (ext == ".ins") DumpIns(p);
            }
            catch (Exception ex) { Console.WriteLine("ERROR " + ex); }
        }
        return 0;
    }

    private static string Op(OplOperator o)
    {
        return $"ksl={o.Ksl} mul={o.Multiple} fb={o.Feedback} ar={o.Attack} sl={o.Sustain} eg={o.Eg} dr={o.Decay} rr={o.ReleaseRate} tl={o.TotalLevel} am={o.Am} vib={o.Vib} ksr={o.Ksr} con={o.Con} ws={o.WaveSelect}";
    }

    private static void DumpIns(string p)
    {
        AdlibInstrument ins;
        using (var fs = File.OpenRead(p)) ins = new InsFormat().Read(fs, Path.GetFileNameWithoutExtension(p));
        Console.WriteLine(" mod " + Op(ins.Modulator));
        Console.WriteLine(" car " + Op(ins.Carrier));
        var ms = new MemoryStream();
        new InsFormat().Write(ins, ms);
        Console.WriteLine(" rewrite identical: " + ms.ToArray().SequenceEqual(File.ReadAllBytes(p)));
    }

    private static void DumpBnk(string p)
    {
        InstrumentBank bank;
        using (var fs = File.OpenRead(p)) bank = new BnkFormat().Read(fs);
        Console.WriteLine($" ver={bank.VersionMajor}.{bank.VersionMinor} count={bank.Instruments.Count} used={bank.UsedCount}");
        var maxes = new Dictionary<string, int>();
        foreach (var ins in bank.Instruments)
            foreach (var pair in new[] { ("m", ins.Modulator), ("c", ins.Carrier) })
                foreach (OperatorParameter op in Enum.GetValues(typeof(OperatorParameter)))
                {
                    string k = pair.Item1 + op; int v = pair.Item2.Get(op);
                    if (!maxes.ContainsKey(k) || maxes[k] < v) maxes[k] = v;
                }
        Console.WriteLine(" max: " + string.Join(" ", maxes.Select(kv => kv.Key + "=" + kv.Value)));
        int conMod0 = bank.Instruments.Count(i => i.Modulator.Con == 0), conCar0 = bank.Instruments.Count(i => i.Carrier.Con == 0);
        Console.WriteLine($" mod.con==0: {conMod0}  car.con==0: {conCar0}  perc: {bank.Instruments.Count(i => i.Percussive)}");
        foreach (var ins in bank.Instruments.Take(12))
        {
            Console.WriteLine($" [{ins.Name}] used={ins.IsUsed} perc={ins.Percussive} vn={ins.VoiceNum}");
            Console.WriteLine("   mod " + Op(ins.Modulator));
            Console.WriteLine("   car " + Op(ins.Carrier));
        }
        var ms = new MemoryStream();
        new BnkFormat().Write(bank, ms);
        Console.WriteLine(" rewrite identical: " + ms.ToArray().SequenceEqual(File.ReadAllBytes(p)) + " (orig " + new FileInfo(p).Length + ", new " + ms.Length + ")");
    }

    private static void DumpRol(string p)
    {
        Song song;
        var fmt0 = FormatRegistry.CreateDefault().FindSong(p);
        using (var fs = File.OpenRead(p)) { song = fmt0.Read(fs); Console.WriteLine($" consumed {fs.Position}/{fs.Length}"); }
        Console.WriteLine($" ver={song.VersionMajor}.{song.VersionMinor} sig='{song.Signature}' tickBeat={song.TickBeat} bpm={song.BeatsPerMeasure} scale={song.ScaleX}x{song.ScaleY} melodic={song.IsMelodic} tempo={song.BasicTempo} tempoEv={song.TempoEvents.Count} len={song.LengthTicks}");
        foreach (var t in song.TempoEvents.Take(5)) Console.WriteLine($"   tempo @{t.Tick} x{t.Multiplier}");
        for (int v = 0; v < Song.MaxVoices; v++)
        {
            var vt = song.Voices[v];
            string range = vt.Notes.Count > 0 ? vt.Notes.Min(n => n.Pitch) + ".." + vt.Notes.Max(n => n.Pitch) : "-";
            Console.WriteLine($" v{v} '{vt.Name}' ticks={vt.DeclaredTicks} notes={vt.Notes.Count} range={range} timbres={vt.Timbres.Count} vols={vt.Volumes.Count} pitches={vt.Pitches.Count}");
            foreach (var t in vt.Timbres.Take(3)) Console.WriteLine($"    timbre @{t.Tick} '{t.InstrumentName}' unk={t.Unknown}");
            foreach (var e in vt.Volumes.Take(3)) Console.WriteLine($"    vol @{e.Tick} {e.Volume}");
            foreach (var e in vt.Pitches.Take(3)) Console.WriteLine($"    pitch @{e.Tick} {e.Pitch}");
        }
        var ms = new MemoryStream();
        fmt0.Write(song, ms);
        var orig = File.ReadAllBytes(p); var neu = ms.ToArray();
        int diff = -1;
        for (int i = 0; i < Math.Min(orig.Length, neu.Length); i++) if (orig[i] != neu[i]) { diff = i; break; }
        Console.WriteLine($" rewrite: orig {orig.Length} new {neu.Length} firstDiff={diff}");
    }
}
