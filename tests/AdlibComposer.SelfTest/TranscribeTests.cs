using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>채보: 오디오 읽기 (WAV · Media Foundation), 주 멜로디 뽑기, 템포, 곡으로 바꾸기.</summary>
public static class TranscribeTests
{
    private const int R = MelodyAnalysis.Rate;

    public static void Run(Action<string, bool, string> report)
    {
        void check(string name, bool ok, string detail = "") { report(name, ok, detail); }

        // ---- 합성 곡: 100 BPM, 8분음표 멜로디(배음 많은 소리) + 낮은 화음 반주(작게) + 잡음
        double bpm = 100, beat = 60 / bpm;
        int[] mel = { 72, 74, 76, 77, 79, 79, 77, 76, 74, 72, 74, 76, 72, 67, 72, 72, 76, 79, 81, 79, 77, 76, 74, 72 };
        var reference = new List<(double s, double e, int midi)>();
        double t0 = 0.5;
        for (int i = 0; i < mel.Length; i++) reference.Add((t0 + i * beat / 2, t0 + (i + 1) * beat / 2 - 0.03, mel[i]));
        double total = t0 + mel.Length * beat / 2 + 0.5;
        var x = new float[(int)(total * R)];
        var rnd = new Random(1);
        foreach (var (s, e, m) in reference) AddTone(x, s, e, m, 0.30, 8);
        // 반주: 마디(4박)마다 C · F · G · C 화음, 낮은 음역(48~55), 멜로디보다 작게
        int[][] chords = { new[] { 48, 52, 55 }, new[] { 41, 45, 48 }, new[] { 43, 47, 50 }, new[] { 48, 52, 55 } };
        for (int bar = 0; bar * 4 * beat + t0 < total - 0.5; bar++)
            foreach (var m in chords[bar % 4]) AddTone(x, t0 + bar * 4 * beat, t0 + (bar + 1) * 4 * beat - 0.02, m, 0.10, 4);
        for (int i = 0; i < x.Length; i++) x[i] += (float)((rnd.NextDouble() - 0.5) * 0.01);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var a = MelodyAnalysis.Analyze(x);
        var r = a.Track(new TranscribeOptions());
        double ms = sw.Elapsed.TotalMilliseconds;
        int hit = reference.Count(rf => r.Notes.Any(n => n.Midi == rf.midi && Math.Abs(n.Start - rf.s) < 0.08));
        int extra = r.Notes.Count(n => !reference.Any(rf => rf.midi == n.Midi && Math.Abs(n.Start - rf.s) < 0.08));
        check("Melody from mix: notes found (pitch + onset ±80 ms)", hit >= reference.Count * 0.85 && extra <= reference.Count * 0.2,
            hit + "/" + reference.Count + " found, " + extra + " extra, " + ms.ToString("0") + " ms for " + total.ToString("0.0") + " s");
        check("Tempo estimate ≈ 100 BPM (or half/double)", Near(a.Bpm, 100, 0.04) || Near(a.Bpm, 50, 0.04) || Near(a.Bpm, 200, 0.04), a.Bpm.ToString("0.0"));

        // 음역 밖(반주 음역)으로 좁히면 멜로디가 없어야 (반주만 남음): 반주 음이 아닌 멜로디 음은 안 나온다
        var low = a.Track(new TranscribeOptions { MinMidi = 36, MaxMidi = 58 });
        check("Range limits pitch tracking", low.Notes.All(n => n.Midi >= 35 && n.Midi <= 59), string.Join(" ", low.Notes.Take(8).Select(n => n.Midi)));

        // ---- 곡으로: 100 BPM, 첫 박 = 0.5초, 8분음표 격자
        var song = new Song();
        MelodyToSong.Build(song, r.Notes, 100, t0, 8);
        var notes = song.Voices[0].Notes;
        int eighth = song.TickBeat / 2;
        int onGrid = notes.Count(n => n.StartTick % eighth == 0);
        int correctPos = notes.Count(n => reference.Any(rf => rf.midi == n.Pitch && Math.Abs((rf.s - t0) / beat * song.TickBeat - n.StartTick) < 1));
        check("Song: notes on 8th grid at the right beats", notes.Count > 0 && onGrid == notes.Count && correctPos >= reference.Count * 0.8 && song.BasicTempo == 100,
            correctPos + "/" + reference.Count + " at exact ticks, " + notes.Count + " notes");

        // ---- WAV 읽기 (16bit 스테레오 44.1kHz → 모노 22050) · Media Foundation
        string wav = Path.Combine(Path.GetTempPath(), "fmopl_transcribe_test.wav");
        WriteWav(wav, x, 44100, 2);
        var back = AudioFileReader.ReadMono(wav, R, out int sr);
        check("WAV reader: rate and length", sr == 44100 && Math.Abs(back.Length - x.Length) < 50, sr + " Hz, " + back.Length + " / " + x.Length + " samples");
        try
        {
            var mf = AudioFileReader.ReadMono(wav, R, out int sr2, true);
            double corr = Correlation(back, mf);
            check("Media Foundation decoder reads the same audio", sr2 == 44100 && Math.Abs(mf.Length - back.Length) < 2000 && corr > 0.95, sr2 + " Hz, " + mf.Length + " samples, corr " + corr.ToString("0.000"));
        }
        catch (Exception ex) { check("Media Foundation decoder reads the same audio", false, ex.Message); }
        File.Delete(wav);

        // ---- 실제 MP3 (_incoming/sample.mp3, Media Foundation): 정답은 없으니 그럴듯한지만 (음 수, 옥타브 튐, 템포)
        string inc = SopTests.FindDir("_incoming");
        string mp3 = inc != null ? Path.Combine(inc, "sample.mp3") : null;
        if (mp3 == null || !File.Exists(mp3)) Console.WriteLine("SKIP  real MP3 transcription (_incoming/sample.mp3 not found)");
        else
        {
            var real = AudioFileReader.ReadMono(mp3, R, out int mr);
            var ra = MelodyAnalysis.Analyze(real);
            var rr = ra.Track(new TranscribeOptions());
            int jumps = 0;
            for (int i = 1; i < rr.Notes.Count; i++) if (Math.Abs(rr.Notes[i].Midi - rr.Notes[i - 1].Midi) >= 12) jumps++;
            check("sample.mp3: decoded, melody notes, few octave jumps, tempo",
                mr > 0 && real.Length > R * 10 && rr.Notes.Count > 100 && jumps < rr.Notes.Count * 0.05 && ra.Bpm >= 50 && ra.Bpm <= 220,
                (real.Length / (double)R).ToString("0") + " s, " + rr.Notes.Count + " notes, octave jumps " + jumps + ", " + ra.Bpm + " BPM");
        }

        // ---- 실제 녹음 (samples/demo.wav = demo.rol을 렌더링): 뽑은 음이 곡의 어느 보이스 음과 맞는지
        string dir = SopTests.FindDir("samples");
        if (dir != null && File.Exists(Path.Combine(dir, "demo.wav")) && File.Exists(Path.Combine(dir, "demo.rol")))
        {
            var pcm = AudioFileReader.ReadMono(Path.Combine(dir, "demo.wav"), R, out _);
            Song demo;
            using (var fs = File.OpenRead(Path.Combine(dir, "demo.rol"))) demo = new RolFormat().Read(fs);
            var tm = new TempoMap(demo);
            var dr = MelodyAnalysis.Analyze(pcm).Track(new TranscribeOptions { MinMidi = 36, MaxMidi = 96 });
            int match = dr.Notes.Count(n => Enumerable.Range(0, demo.VoiceCount).Any(v => demo.Voices[v].Notes.Any(sn =>
                (sn.Pitch == n.Midi || Math.Abs(sn.Pitch - n.Midi) == 12) && Math.Abs(tm.SecondsAt(sn.StartTick) - n.Start) < 0.12)));
            check("demo.wav: transcribed notes exist in demo.rol (pitch ± octave, onset ±120 ms)", dr.Notes.Count > 5 && match >= dr.Notes.Count * 0.7,
                match + "/" + dr.Notes.Count + " notes match");
        }
    }

    /// <summary>
    /// <c>--transcribe 파일 [음역낮음 음역높음 감도]</c>: 실제 음악 파일을 채보해 통계와 앞부분 음을 찍는다 (조절값 맞추기용).
    /// </summary>
    public static int Probe(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("--transcribe file [lo hi voicing]"); return 1; }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var pcm = AudioFileReader.ReadMono(args[1], R, out int sr);
        float peak = pcm.Max(v => Math.Abs(v));
        if (peak > 0) for (int i = 0; i < pcm.Length; i++) pcm[i] *= 0.9f / peak;
        Console.WriteLine("decode: " + sw.ElapsedMilliseconds + " ms, " + sr + " Hz, " + (pcm.Length / (double)R).ToString("0.0") + " s, peak " + peak.ToString("0.00"));
        sw.Restart();
        var a = MelodyAnalysis.Analyze(pcm);
        Console.WriteLine("analyze: " + sw.ElapsedMilliseconds + " ms, tempo " + a.Bpm + " BPM, first beat " + a.BeatOffset.ToString("0.00") + " s");
        var opt = new TranscribeOptions();
        if (args.Length >= 5) { opt.MinMidi = int.Parse(args[2]); opt.MaxMidi = int.Parse(args[3]); opt.Voicing = double.Parse(args[4]); }
        sw.Restart();
        var r = a.Track(opt);
        int voiced = r.Pitch.Count(p => !float.IsNaN(p));
        Console.WriteLine("track: " + sw.ElapsedMilliseconds + " ms, range " + opt.MinMidi + "-" + opt.MaxMidi + ", voicing " + opt.Voicing + ", voiced " + (100.0 * voiced / r.Pitch.Length).ToString("0") + "%, notes " + r.Notes.Count);
        if (r.Notes.Count == 0) return 0;
        var hist = r.Notes.GroupBy(n => n.Midi).OrderBy(g => g.Key).Select(g => g.Key + ":" + g.Count());
        Console.WriteLine("pitch histogram: " + string.Join(" ", hist));
        var durs = r.Notes.Select(n => n.Duration).OrderBy(x => x).ToArray();
        Console.WriteLine("duration ms: p10 " + (durs[durs.Length / 10] * 1000).ToString("0") + ", median " + (durs[durs.Length / 2] * 1000).ToString("0") + ", p90 " + (durs[durs.Length * 9 / 10] * 1000).ToString("0"));
        var st = r.Notes.Select(n => n.Strength).OrderBy(x => x).ToArray();
        Console.WriteLine("strength p10 " + st[st.Length / 10].ToString("0.00") + ", p25 " + st[st.Length / 4].ToString("0.00") + ", median " + st[st.Length / 2].ToString("0.00") + ", p75 " + st[st.Length * 3 / 4].ToString("0.00"));
        Console.WriteLine("cents off: " + r.Notes.Average(n => Math.Abs(n.Pitch - n.Midi) * 100).ToString("0") + ", strength avg " + r.Notes.Average(n => n.Strength).ToString("0.00"));
        int jumps = 0;
        for (int i = 1; i < r.Notes.Count; i++) if (Math.Abs(r.Notes[i].Midi - r.Notes[i - 1].Midi) >= 12) jumps++;
        Console.WriteLine("octave+ jumps between neighbours: " + jumps + " (" + (100.0 * jumps / r.Notes.Count).ToString("0") + "%)");
        string[] nm = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        foreach (var n in r.Notes.Take(int.Parse(Environment.GetEnvironmentVariable("TRANSCRIBE_SHOW") ?? "60")))
            Console.WriteLine("  " + n.Start.ToString("0.00").PadLeft(7) + "  " + (n.Duration * 1000).ToString("0").PadLeft(5) + " ms  " + (nm[n.Midi % 12] + (n.Midi / 12 - 1)).PadRight(4) + " " + ((n.Pitch - n.Midi) * 100).ToString("+0;-0").PadLeft(4) + "c  s=" + n.Strength.ToString("0.00"));
        return 0;
    }

    private static bool Near(double v, double target, double tol) { return Math.Abs(v / target - 1) < tol; }

    /// <summary>배음 h개, 1/h 크기, 감쇠 + 짧은 어택 · 릴리스.</summary>
    private static void AddTone(float[] x, double s, double e, int midi, double amp, int harmonics)
    {
        double f = 440 * Math.Pow(2, (midi - 69) / 12.0);
        int a = (int)(s * R), b = Math.Min(x.Length, (int)(e * R));
        for (int i = a; i < b; i++)
        {
            double t = (i - a) / (double)R, rel = (b - i) / (double)R;
            double env = Math.Min(1, t / 0.01) * Math.Min(1, rel / 0.02) * (0.6 + 0.4 * Math.Exp(-t * 3));
            double v = 0;
            for (int h = 1; h <= harmonics; h++) { if (f * h > R / 2.2) break; v += Math.Sin(2 * Math.PI * f * h * t) / h; }
            x[i] += (float)(v * amp * env);
        }
    }

    private static void WriteWav(string path, float[] mono, int rate, int channels)
    {
        var src = AudioFileReader.Resample(mono, R, rate);
        using (var w = new BinaryWriter(File.Create(path)))
        {
            int bytes = src.Length * 2 * channels;
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
            w.Write(rate * 2 * channels); w.Write((short)(2 * channels)); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (var v in src) { short sv = (short)Math.Max(-32768, Math.Min(32767, v * 32767)); for (int c = 0; c < channels; c++) w.Write(sv); }
        }
    }

    private static double Correlation(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double sab = 0, saa = 0, sbb = 0;
        for (int i = 0; i < n; i++) { sab += a[i] * b[i]; saa += a[i] * a[i]; sbb += b[i] * b[i]; }
        return sab / Math.Sqrt(saa * sbb + 1e-12);
    }
}
