using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace AdlibComposer.Audio;

/// <summary>채보 조절값 (분석 결과는 그대로 두고 바꿔 가며 다시 뽑을 수 있다).</summary>
public sealed class TranscribeOptions
{
    /// <summary>멜로디 음역 (MIDI 음 번호, 33 = A1 ~ 93 = A6 안).</summary>
    public int MinMidi = 48, MaxMidi = 84;
    /// <summary>멜로디가 '있다'고 볼 문턱 (0 = 거의 모두 음으로, 100 = 아주 뚜렷한 것만).</summary>
    public double Voicing = 50;
    /// <summary>이보다 짧은 음은 버린다 (ms).</summary>
    public double MinNoteMs = 90;
}

/// <summary>채보한 음 하나 (초, MIDI 음 번호).</summary>
public sealed class MelodyNote
{
    public double Start, End;
    public int Midi;
    /// <summary>음 안의 평균 음높이 (반올림 전, 음정이 얼마나 맞는지 보기용).</summary>
    public double Pitch;
    /// <summary>뚜렷함 (0~1).</summary>
    public double Strength;
    public double Duration { get { return End - Start; } }
}

/// <summary>채보 결과: 프레임별 음높이 곡선 + 음 목록.</summary>
public sealed class TranscriptionResult
{
    public double HopSeconds;
    /// <summary>프레임별 멜로디 음높이 (MIDI, 소수), 멜로디 없음 = NaN.</summary>
    public float[] Pitch;
    public List<MelodyNote> Notes = new List<MelodyNote>();
}

/// <summary>
/// 오디오 → 주 멜로디 (화음 · 반주는 무시). 로컬에서 신호 처리만으로 동작한다 (Melodia 방식을 단순하게 옮김):
/// <list type="number">
/// <item>22050Hz 모노, 2048 Hann 창 · 4096 FFT · 홉 256 (11.6ms)</item>
/// <item>스펙트럼 봉우리 (프레임 최대보다 40dB 위, 50Hz~5kHz, 포물선 보간) 최대 50개</item>
/// <item>배음 합 살리언스: 봉우리 f를 f/h (h = 1~20)의 후보 기본음에 0.8^(h−1)만큼 더한다. 20센트 칸, A1(55Hz)~A6, ±1반음에 cos² 가중</item>
/// <item>비터비 추적: 음역 안 칸 + '멜로디 없음' 상태. 가까운 칸으로는 거리만큼, 먼 도약 · 켜짐 · 꺼짐에는 고정 벌점</item>
/// <item>음 나누기: 반음 넘게 0.65 이상 벗어난 상태가 4프레임 이어지면 새 음, 같은 음 안의 강한 어택(스펙트럼 플럭스 봉우리)에서도 나눔, 짧은 음은 버림</item>
/// <item>템포: 플럭스의 자기상관 (50~220 BPM, 120 근처 선호) + 박 위상</item>
/// </list>
/// 1~3단계와 템포는 <see cref="Analyze"/>에서 한 번, 4~5단계는 <see cref="Track"/>에서 조절값을 바꿀 때마다.
/// </summary>
public sealed class MelodyAnalysis
{
    public const int Rate = 22050, FrameSize = 2048, FftSize = 4096, Hop = 256;
    public const int LowMidi = 33, BinsPerSemitone = 5, Bins = 60 * BinsPerSemitone;   // 33 (A1) ~ 93 (A6), 20센트 칸
    private const int MaxPeaks = 50, Harmonics = 20;
    private const double Alpha = 0.8;

    public double HopSeconds { get { return (double)Hop / Rate; } }
    public int Frames { get; private set; }
    public double Duration { get; private set; }
    /// <summary>[프레임 × Bins] 살리언스.</summary>
    private float[] salience;
    /// <summary>프레임별 스펙트럼 플럭스 (어택 세기).</summary>
    public float[] Onset { get; private set; }
    /// <summary>추정 템포 (BPM)와 첫 박 위치 (초).</summary>
    public double Bpm { get; private set; } = 120;
    public double BeatOffset { get; private set; }

    public float SalienceAt(int frame, int bin) { return salience[frame * Bins + bin]; }

    public static int MidiToBin(double midi) { return (int)Math.Round((midi - LowMidi) * BinsPerSemitone); }

    // ------------------------------------------------------------ 1~3단계

    /// <summary>모노 22050Hz 샘플을 분석한다 (몇 초 걸림, progress = 0~1).</summary>
    public static MelodyAnalysis Analyze(float[] mono, IProgress<double> progress = null, CancellationToken cancel = default)
    {
        var a = new MelodyAnalysis();
        int frames = Math.Max(1, (mono.Length - FrameSize) / Hop + 1);
        a.Frames = frames;
        a.Duration = mono.Length / (double)Rate;
        a.salience = new float[frames * Bins];
        a.Onset = new float[frames];

        var fft = new Fft(FftSize);
        var window = new double[FrameSize];
        for (int i = 0; i < FrameSize; i++) window[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (FrameSize - 1));
        var re = new double[FftSize];
        var im = new double[FftSize];
        int half = FftSize / 2;
        var mag = new double[half + 1];
        var logMag = new double[half + 1];
        var prevLog = new double[half + 1];
        int kMin = (int)(50.0 * FftSize / Rate), kMax = Math.Min(half - 1, (int)(5000.0 * FftSize / Rate));
        int kFlux = Math.Min(half, (int)(6000.0 * FftSize / Rate));
        var peakF = new double[256];
        var peakA = new double[256];
        var harmW = new double[Harmonics + 1];
        for (int h = 1; h <= Harmonics; h++) harmW[h] = Math.Pow(Alpha, h - 1);
        double binsPerOctave = 12.0 * BinsPerSemitone;

        for (int t = 0; t < frames; t++)
        {
            if ((t & 255) == 0)
            {
                cancel.ThrowIfCancellationRequested();
                progress?.Report((double)t / frames);
            }
            int at = t * Hop;
            for (int i = 0; i < FftSize; i++) { re[i] = i < FrameSize && at + i < mono.Length ? mono[at + i] * window[i] : 0; im[i] = 0; }
            fft.Transform(re, im);
            double maxMag = 0;
            for (int k = 0; k <= half; k++)
            {
                double m = Math.Sqrt(re[k] * re[k] + im[k] * im[k]);
                mag[k] = m;
                if (k >= kMin && k <= kMax && m > maxMag) maxMag = m;
            }
            // 플럭스 (어택)
            double flux = 0;
            for (int k = 1; k < kFlux; k++)
            {
                logMag[k] = Math.Log(1 + 1000 * mag[k]);
                double d = logMag[k] - prevLog[k];
                if (d > 0) flux += d;
            }
            a.Onset[t] = (float)flux;
            var tmp = prevLog; prevLog = logMag; logMag = tmp;
            if (maxMag <= 1e-7) continue;

            // 봉우리
            double floor = maxMag * 0.01;   // −40dB
            int n = 0;
            for (int k = kMin + 1; k < kMax; k++)
            {
                double m = mag[k];
                if (m < floor || m <= mag[k - 1] || m < mag[k + 1]) continue;
                double l0 = Math.Log(mag[k - 1] + 1e-12), l1 = Math.Log(m + 1e-12), l2 = Math.Log(mag[k + 1] + 1e-12);
                double den = l0 - 2 * l1 + l2;
                double p = den != 0 ? 0.5 * (l0 - l2) / den : 0;
                if (p > 0.5 || p < -0.5) p = 0;
                if (n < peakF.Length)
                {
                    peakF[n] = (k + p) * Rate / FftSize;
                    peakA[n] = Math.Exp(l1 - 0.25 * (l0 - l2) * p);
                    n++;
                }
            }
            if (n > MaxPeaks)
            {
                // 큰 것 MaxPeaks개만
                var idx = Enumerable.Range(0, n).OrderByDescending(i => peakA[i]).Take(MaxPeaks).ToArray();
                var f2 = idx.Select(i => peakF[i]).ToArray();
                var a2 = idx.Select(i => peakA[i]).ToArray();
                n = MaxPeaks;
                Array.Copy(f2, peakF, n);
                Array.Copy(a2, peakA, n);
            }
            // 배음 합 살리언스
            int baseIdx = t * Bins;
            for (int i = 0; i < n; i++)
            {
                double f = peakF[i], amp = peakA[i] / maxMag;
                for (int h = 1; h <= Harmonics; h++)
                {
                    double fh = f / h;
                    if (fh < 52) break;
                    double c = binsPerOctave * Math.Log(fh / 55.0, 2);
                    int b0 = (int)Math.Ceiling(c - BinsPerSemitone), b1 = (int)Math.Floor(c + BinsPerSemitone);
                    if (b1 < 0) break;
                    double w0 = harmW[h] * amp;
                    for (int b = Math.Max(0, b0); b <= b1 && b < Bins; b++)
                    {
                        double d = Math.Abs(b - c) / BinsPerSemitone;
                        double cs = Math.Cos(d * Math.PI / 2);
                        a.salience[baseIdx + b] += (float)(w0 * cs * cs);
                    }
                }
            }
            // 프레임 크기(소리 세기)를 살리언스에 반영: 조용한 프레임은 낮게
            float level = (float)Math.Sqrt(maxMag);
            for (int b = 0; b < Bins; b++) a.salience[baseIdx + b] *= level;
        }
        progress?.Report(1);
        a.EstimateTempo();
        return a;
    }

    // ------------------------------------------------------------ 템포

    private void EstimateTempo()
    {
        int n = Onset.Length;
        if (n < 200) return;
        var o = new double[n];
        double mean = Onset.Average();
        for (int i = 0; i < n; i++) o[i] = Math.Max(0, Onset[i] - mean);
        double hop = HopSeconds;
        int lagMin = (int)Math.Floor(60.0 / 220 / hop), lagMax = (int)Math.Ceiling(60.0 / 50 / hop);
        var ac = new double[lagMax + 2];
        for (int lag = lagMin; lag <= lagMax + 1 && lag < n; lag++)
        {
            double s = 0;
            for (int i = 0; i + lag < n; i++) s += o[i] * o[i + lag];
            ac[lag] = s / (n - lag);
        }
        double best = -1; int bestLag = (int)Math.Round(0.5 / hop);
        for (int lag = lagMin; lag <= lagMax; lag++)
        {
            double bpm = 60.0 / (lag * hop);
            double prior = Math.Exp(-0.5 * Math.Pow(Math.Log(bpm / 120.0, 2) / 0.9, 2));
            double s = (ac[lag] + 0.5 * (2 * lag <= lagMax + 1 ? ac[Math.Min(2 * lag, lagMax + 1)] : 0)) * prior;
            if (s > best) { best = s; bestLag = lag; }
        }
        // 포물선 보간
        double fl = bestLag;
        if (bestLag > lagMin && bestLag < lagMax)
        {
            double y0 = ac[bestLag - 1], y1 = ac[bestLag], y2 = ac[bestLag + 1], den = y0 - 2 * y1 + y2;
            if (den < 0) fl = bestLag + Math.Max(-0.5, Math.Min(0.5, 0.5 * (y0 - y2) / den));
        }
        Bpm = Math.Round(60.0 / (fl * hop), 1);
        // 박 위상: 박마다 어택 합이 가장 큰 위치
        double period = fl;
        double bestPhase = 0, bestSum = -1;
        for (int ph = 0; ph < (int)Math.Ceiling(period); ph++)
        {
            double s = 0;
            for (double x = ph; x < n; x += period) s += o[(int)x];
            if (s > bestSum) { bestSum = s; bestPhase = ph; }
        }
        BeatOffset = bestPhase * hop;
    }

    // ------------------------------------------------------------ 4~5단계

    /// <summary>조절값으로 멜로디 곡선과 음 목록을 뽑는다 (빠름).</summary>
    public TranscriptionResult Track(TranscribeOptions opt)
    {
        int lo = Math.Max(0, MidiToBin(Math.Min(opt.MinMidi, opt.MaxMidi - 1)));
        int hi = Math.Min(Bins - 1, MidiToBin(Math.Max(opt.MaxMidi, opt.MinMidi + 1)));
        int states = hi - lo + 1, U = states;   // 마지막 = 멜로디 없음
        int frames = Frames;

        // 기준 크기: 프레임 최대 살리언스의 90번째 백분위
        var fmax = new float[frames];
        for (int t = 0; t < frames; t++)
        {
            float m = 0;
            for (int b = lo; b <= hi; b++) m = Math.Max(m, salience[t * Bins + b]);
            fmax[t] = m;
        }
        var sorted = fmax.Where(x => x > 0).OrderBy(x => x).ToArray();
        double reference = sorted.Length > 0 ? sorted[(int)(sorted.Length * 0.9)] : 1;
        if (reference <= 0) reference = 1;
        double thr = 0.03 * Math.Exp(Math.Max(0, Math.Min(100, opt.Voicing)) / 100.0 * Math.Log(25));   // 0.03 ~ 0.75
        double unvoicedEmission = Math.Log(thr);

        const int Window = 2 * BinsPerSemitone;   // 이 안은 거리 벌점, 밖은 도약
        const double StepCost = 0.12, JumpCost = 3.0, OnCost = 2.0, OffCost = 2.0;
        var prev = new double[states + 1];
        var cur = new double[states + 1];
        var back = new short[frames * (states + 1)];
        for (int s = 0; s < states; s++) prev[s] = Math.Log(salience[lo + s] / reference + 1e-4);
        prev[U] = unvoicedEmission;
        for (int t = 1; t < frames; t++)
        {
            int bi = 0; double bestV = double.NegativeInfinity;
            for (int s = 0; s < states; s++) if (prev[s] > bestV) { bestV = prev[s]; bi = s; }
            int row = t * (states + 1), sal = t * Bins + lo;
            for (int s = 0; s < states; s++)
            {
                double v = bestV - JumpCost; int from = bi;
                int a = Math.Max(0, s - Window), z = Math.Min(states - 1, s + Window);
                for (int q = a; q <= z; q++)
                {
                    double c = prev[q] - StepCost * Math.Abs(q - s);
                    if (c > v) { v = c; from = q; }
                }
                double fromU = prev[U] - OnCost;
                if (fromU > v) { v = fromU; from = U; }
                cur[s] = v + Math.Log(salience[sal + s] / reference + 1e-4);
                back[row + s] = (short)from;
            }
            double uStay = prev[U], uFrom = bestV - OffCost;
            cur[U] = (uStay >= uFrom ? uStay : uFrom) + unvoicedEmission;
            back[row + U] = (short)(uStay >= uFrom ? U : bi);
            var sw = prev; prev = cur; cur = sw;
        }
        // 되짚기
        var path = new int[frames];
        int last = 0; double lv = double.NegativeInfinity;
        for (int s = 0; s <= states; s++) if (prev[s] > lv) { lv = prev[s]; last = s; }
        for (int t = frames - 1; t >= 0; t--)
        {
            path[t] = last;
            if (t > 0) last = back[t * (states + 1) + last];
        }
        var pitch = new float[frames];
        for (int t = 0; t < frames; t++)
        {
            int s = path[t];
            if (s == U) { pitch[t] = float.NaN; continue; }
            int b = lo + s;
            double fb = b;
            if (b > 0 && b < Bins - 1)
            {
                double y0 = salience[t * Bins + b - 1], y1 = salience[t * Bins + b], y2 = salience[t * Bins + b + 1], den = y0 - 2 * y1 + y2;
                if (den < 0) fb = b + Math.Max(-0.5, Math.Min(0.5, 0.5 * (y0 - y2) / den));
            }
            pitch[t] = (float)(LowMidi + fb / BinsPerSemitone);
        }
        var result = new TranscriptionResult { HopSeconds = HopSeconds, Pitch = pitch };
        result.Notes = Segment(pitch, reference, opt);
        return result;
    }

    private List<MelodyNote> Segment(float[] raw, double reference, TranscribeOptions opt)
    {
        int n = raw.Length;
        // 5프레임 중앙값 (멜로디 있는 프레임끼리)
        var p = new float[n];
        var win = new List<float>(5);
        for (int t = 0; t < n; t++)
        {
            if (float.IsNaN(raw[t])) { p[t] = float.NaN; continue; }
            win.Clear();
            for (int k = Math.Max(0, t - 2); k <= Math.Min(n - 1, t + 2); k++) if (!float.IsNaN(raw[k])) win.Add(raw[k]);
            win.Sort();
            p[t] = win[win.Count / 2];
        }
        // 어택 봉우리 (같은 음이 다시 쳐진 곳)
        var onsetPeak = new bool[n];
        double oMax = Onset.Length > 0 ? Onset.Max() : 0;
        for (int t = 2; t < n - 2; t++)
        {
            if (Onset[t] <= Onset[t - 1] || Onset[t] < Onset[t + 1] || Onset[t] < 0.12 * oMax) continue;
            double local = 0; int cnt = 0;
            for (int k = Math.Max(0, t - 25); k <= Math.Min(n - 1, t + 25); k++) { local += Onset[k]; cnt++; }
            if (Onset[t] > 1.6 * local / cnt) onsetPeak[t] = true;
        }

        var segs = new List<(int s, int e)>();
        int start = -1;
        var vals = new List<float>();
        int devCount = 0, devStart = -1;
        double minFrames = opt.MinNoteMs / 1000.0 / HopSeconds;
        void Close(int end)
        {
            if (start >= 0 && end > start) segs.Add((start, end));
            start = -1; vals.Clear(); devCount = 0; devStart = -1;
        }
        for (int t = 0; t < n; t++)
        {
            if (float.IsNaN(p[t])) { Close(t); continue; }
            if (start < 0) { start = t; vals.Add(p[t]); continue; }
            double med = Median(vals);
            if (Math.Abs(p[t] - med) > 0.65)
            {
                if (devCount == 0) devStart = t;
                devCount++;
                if (devCount >= 4) { int ds = devStart; Close(ds); start = ds; for (int k = ds; k <= t; k++) vals.Add(p[k]); }
                continue;
            }
            devCount = 0;
            if (onsetPeak[t] && t - start >= minFrames) { Close(t); start = t; }
            vals.Add(p[t]);
        }
        Close(n);

        var notes = new List<MelodyNote>();
        var noteFrames = new List<(int s, int e)>();
        foreach (var (s, e) in segs)
        {
            if (e - s < minFrames) continue;
            var v = new List<float>();
            for (int t = s; t < e; t++) if (!float.IsNaN(p[t])) v.Add(p[t]);
            if (v.Count == 0) continue;
            double med = Median(v);
            notes.Add(new MelodyNote
            {
                Start = s * HopSeconds, End = e * HopSeconds, Midi = (int)Math.Round(med), Pitch = med,
                Strength = NoteSalience(s, e, med) / reference,
            });
            noteFrames.Add((s, e));
        }
        FixOctaves(notes, noteFrames, opt);

        // 뚜렷함으로 거르기: 이 곡 음들의 가운데 값에 견준 비율 (곡마다 반주 크기가 달라서 절대값 대신)
        if (notes.Count > 0)
        {
            double median = notes.Select(x => x.Strength).OrderBy(x => x).ElementAt(notes.Count / 2);
            double cut = median * (0.15 + 0.7 * Math.Max(0, Math.Min(100, opt.Voicing)) / 100.0);
            notes.RemoveAll(x => x.Strength < cut);
            foreach (var x in notes) x.Strength = Math.Min(1, x.Strength / (2 * median));
        }
        // 같은 음이 아주 짧은 틈(40ms 미만)으로 끊긴 것은 어택이 없으면 잇는다
        for (int i = notes.Count - 2; i >= 0; i--)
        {
            var a = notes[i]; var b = notes[i + 1];
            int bf = (int)Math.Round(b.Start / HopSeconds);
            if (a.Midi == b.Midi && b.Start - a.End < 0.04 && !(bf < n && onsetPeak[bf])) { a.End = b.End; notes.RemoveAt(i + 1); }
        }
        return notes;
    }

    /// <summary>음 구간에서 그 음높이 칸의 평균 살리언스.</summary>
    private double NoteSalience(int s, int e, double midi)
    {
        int b = MidiToBin(midi);
        if (b < 0 || b >= Bins || e <= s) return 0;
        double sum = 0;
        for (int t = s; t < e; t++) sum += salience[t * Bins + b];
        return sum / (e - s);
    }

    /// <summary>
    /// 옥타브 오류 고치기: 앞뒤 2.5초 음들의 가운데 음높이(길이 × 뚜렷함 가중)에서 9반음 넘게 떨어진 음은,
    /// 그쪽으로 옥타브를 옮긴 자리에도 배음이 있으면(원래 자리 살리언스의 35% 이상) 옮긴다. 음역 밖으로는 옮기지 않는다.
    /// </summary>
    private void FixOctaves(List<MelodyNote> notes, List<(int s, int e)> frames, TranscribeOptions opt)
    {
        if (notes.Count < 3) return;
        double Weight(MelodyNote x) { return x.Duration * Math.Max(0.05, x.Strength); }
        double WeightedMedian(List<(double pitch, double w)> v)
        {
            v.Sort((a, b) => a.pitch.CompareTo(b.pitch));
            double total = v.Sum(x => x.w), acc = 0;
            foreach (var x in v) { acc += x.w; if (acc >= total / 2) return x.pitch; }
            return v[v.Count - 1].pitch;
        }
        // 고친 음높이로 다시 기준을 잡으며 세 번 (틀린 옥타브 음들이 기준을 끌어당기는 것을 줄인다)
        for (int pass = 0; pass < 3; pass++)
        {
            double global = WeightedMedian(notes.Select(x => ((double)x.Midi, Weight(x))).ToList());
            var cur = notes.Select(x => x.Midi).ToArray();
            bool changed = false;
            for (int i = 0; i < notes.Count; i++)
            {
                var n = notes[i];
                var near = new List<(double pitch, double w)>();
                for (int j = 0; j < notes.Count; j++)
                {
                    if (j == i || notes[j].End < n.Start - 3 || notes[j].Start > n.End + 3) continue;
                    near.Add((cur[j], Weight(notes[j])));
                }
                double trend = near.Count >= 2 ? 0.6 * WeightedMedian(near) + 0.4 * global : global;
                double diff = trend - n.Midi;
                if (Math.Abs(diff) < 8) continue;
                int shift = 12 * (int)Math.Round(diff / 12);
                int target = n.Midi + shift;
                if (shift == 0 || target < opt.MinMidi || target > opt.MaxMidi) continue;
                if (Math.Abs(trend - target) >= Math.Abs(diff)) continue;
                double here = NoteSalience(frames[i].s, frames[i].e, n.Pitch), there = NoteSalience(frames[i].s, frames[i].e, n.Pitch + shift);
                if (there < 0.35 * here) continue;
                n.Midi = target;
                n.Pitch += shift;
                n.Strength = Math.Max(n.Strength, there / Math.Max(1e-9, here) * n.Strength);
                changed = true;
            }
            if (!changed) break;
        }
    }

    private static double Median(List<float> v)
    {
        var c = v.ToArray();
        Array.Sort(c);
        return c.Length == 0 ? double.NaN : c[c.Length / 2];
    }
}

/// <summary>반복 기수-2 FFT (제자리, 복소수).</summary>
internal sealed class Fft
{
    private readonly int n;
    private readonly int[] rev;
    private readonly double[] cos, sin;

    public Fft(int size)
    {
        n = size;
        rev = new int[n];
        int bits = (int)Math.Round(Math.Log(n, 2));
        for (int i = 0; i < n; i++)
        {
            int r = 0;
            for (int b = 0; b < bits; b++) if ((i & (1 << b)) != 0) r |= 1 << (bits - 1 - b);
            rev[i] = r;
        }
        cos = new double[n / 2];
        sin = new double[n / 2];
        for (int i = 0; i < n / 2; i++) { cos[i] = Math.Cos(2 * Math.PI * i / n); sin[i] = -Math.Sin(2 * Math.PI * i / n); }
    }

    public void Transform(double[] re, double[] im)
    {
        for (int i = 0; i < n; i++)
        {
            int j = rev[i];
            if (j > i) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (int size = 2; size <= n; size <<= 1)
        {
            int halfSize = size >> 1, step = n / size;
            for (int i = 0; i < n; i += size)
                for (int j = 0, k = 0; j < halfSize; j++, k += step)
                {
                    int a = i + j, b = a + halfSize;
                    double tr = re[b] * cos[k] - im[b] * sin[k];
                    double ti = re[b] * sin[k] + im[b] * cos[k];
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                }
        }
    }
}
