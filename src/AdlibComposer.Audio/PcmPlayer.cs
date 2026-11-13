using System;
using System.Collections.Generic;

namespace AdlibComposer.Audio;

/// <summary>
/// 채보 화면의 재생: 원곡(모노 PCM)과 채보한 멜로디(간단한 배음 소리)를 섞어 낸다. 둘 다 켜면 맞는지 귀로 견줄 수 있다.
/// <see cref="Step"/>은 <see cref="Rate"/>마다 한 번.
/// </summary>
public sealed class PcmPlayer
{
    private float[] data = new float[0];
    private int rate = 22050;
    private MelodyNote[] melody = new MelodyNote[0];
    private long pos;
    private bool playing;
    private int nextNote;
    private readonly List<(MelodyNote note, double phase)> active = new List<(MelodyNote, double)>();

    public bool Original = true, Melody = true;
    /// <summary>멜로디 소리 크기 (원곡 대비).</summary>
    public double MelodyGain = 0.35;

    public int Rate { get { return rate; } }
    public bool IsPlaying { get { return playing; } }
    /// <summary>지금 위치 (ms).</summary>
    public double PositionMs { get { return pos * 1000.0 / rate; } }

    public void Start(float[] samples, int sampleRate, IList<MelodyNote> notes, double startSeconds, bool original, bool melodyOn)
    {
        data = samples ?? new float[0];
        rate = sampleRate;
        melody = notes != null ? new List<MelodyNote>(notes).ToArray() : new MelodyNote[0];
        Array.Sort(melody, (a, b) => a.Start.CompareTo(b.Start));
        Original = original;
        Melody = melodyOn;
        pos = (long)Math.Max(0, startSeconds * rate);
        double t = pos / (double)rate;
        nextNote = 0;
        while (nextNote < melody.Length && melody[nextNote].End <= t) nextNote++;
        active.Clear();
        playing = pos < data.Length;
    }

    public void Stop() { playing = false; active.Clear(); }

    /// <summary>샘플 1개 (−1~1 안팎).</summary>
    public double Step()
    {
        if (!playing) return 0;
        double t = pos / (double)rate;
        double v = Original && pos < data.Length ? data[pos] : 0;
        if (Melody)
        {
            while (nextNote < melody.Length && melody[nextNote].Start <= t) { active.Add((melody[nextNote], 0)); nextNote++; }
            double m = 0;
            for (int i = active.Count - 1; i >= 0; i--)
            {
                var (note, phase) = active[i];
                double rel = t - note.End;
                if (rel > 0.08) { active.RemoveAt(i); continue; }
                double env = Math.Min(1, (t - note.Start) / 0.01) * (rel > 0 ? 1 - rel / 0.08 : 1);
                double f = 440.0 * Math.Pow(2, (note.Midi - 69) / 12.0);
                phase += 2 * Math.PI * f / rate;
                if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                active[i] = (note, phase);
                m += env * (Math.Sin(phase) + 0.4 * Math.Sin(2 * phase) + 0.2 * Math.Sin(3 * phase));
            }
            v += m * MelodyGain * 0.6;
        }
        pos++;
        if (pos >= data.Length && active.Count == 0) playing = false;
        return v;
    }
}
