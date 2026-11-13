using System;
using System.Collections.Generic;
using AdlibComposer.Core;

namespace AdlibComposer.Audio;

/// <summary>
/// 오르골 시험 연주: 구멍(틱, 음)마다 빗살을 튕긴 소리를 낸다. 음 길이는 없고 소리는 저절로 사라진다.
/// 소리 = 기본음 + 약한 2배음 · 비조화 배음(빗살 금속 느낌), 짧은 어택, 높은 음일수록 빨리 사라짐.
/// 같은 줄을 다시 치면 그 빗살 소리는 새로 시작한다 (실제 빗살처럼 한 줄에 한 소리).
/// </summary>
public sealed class MusicBoxPlayer
{
    public const int Rate = 44100;

    private struct Strike { public int Tick; public int Note; }

    private sealed class Tone
    {
        public int Note;
        public double W1, W2, W3;       // 각속도 (rad/샘플)
        public double P1, P2, P3;       // 위상
        public double Amp, Decay, Decay2, Decay3;
        public double A2 = 1, A3 = 1;
        public int Age;
    }

    private readonly List<Strike> strikes = new List<Strike>();
    private readonly List<Tone> tones = new List<Tone>();
    private int next;
    private double tick, ticksPerSample, ticksPerSecond = 96;
    private int endTick;
    private bool playing, ending;
    private int tail;

    public bool IsPlaying { get { return playing; } }
    public double PositionTick { get { return tick; } }
    /// <summary>재생 속도 (틱/초). 손으로 돌리는 속도에 해당.</summary>
    public double TicksPerSecond { get { return ticksPerSecond; } }

    /// <param name="hits">(틱, 음 번호) — 순서는 상관없다.</param>
    public void Start(IEnumerable<KeyValuePair<int, int>> hits, double ticksPerSecond, int startTick, int endTick)
    {
        strikes.Clear();
        foreach (var h in hits) strikes.Add(new Strike { Tick = h.Key, Note = h.Value });
        strikes.Sort((a, b) => a.Tick.CompareTo(b.Tick));
        tones.Clear();
        SetSpeed(ticksPerSecond);
        tick = Math.Max(0, startTick);
        next = 0;
        while (next < strikes.Count && strikes[next].Tick < tick) next++;
        this.endTick = Math.Max(endTick, strikes.Count > 0 ? strikes[strikes.Count - 1].Tick : 0);
        ending = false;
        tail = 0;
        playing = true;
    }

    public void SetSpeed(double tps)
    {
        ticksPerSecond = Math.Max(0.5, tps);
        ticksPerSample = ticksPerSecond / Rate;
    }

    public void Stop()
    {
        playing = false;
        // 울리던 소리는 짧게 줄인다 (손으로 멈춘 느낌)
        foreach (var t in tones) t.Decay = Math.Min(t.Decay, Math.Exp(-1.0 / (0.04 * Rate)));
    }

    /// <summary>샘플 1개 진행 (재생 중이 아니어도 남은 소리는 계속 사라진다).</summary>
    public void Step()
    {
        if (!playing) return;
        while (next < strikes.Count && strikes[next].Tick <= tick) Pluck(strikes[next++].Note);
        tick += ticksPerSample;
        if (!ending && tick >= endTick && next >= strikes.Count) ending = true;
        if (ending && ++tail > Rate * 2) playing = false;
    }

    private void Pluck(int note)
    {
        for (int i = tones.Count - 1; i >= 0; i--) if (tones[i].Note == note) tones.RemoveAt(i);
        double f = NoteHelper.Frequency(note);
        double tau = Math.Max(0.35, Math.Min(2.6, 1.6 * Math.Sqrt(440.0 / f)));   // 낮은 음은 오래, 높은 음은 짧게
        tones.Add(new Tone
        {
            Note = note,
            W1 = 2 * Math.PI * f / Rate,
            W2 = 2 * Math.PI * f * 2.0 / Rate,
            W3 = 2 * Math.PI * f * 5.4 / Rate,     // 빗살(외팔보) 2차 진동 근처의 비조화 배음
            Amp = 0.32,
            Decay = Math.Exp(-1.0 / (tau * Rate)),
            Decay2 = Math.Exp(-1.0 / (tau * 0.35 * Rate)),
            Decay3 = Math.Exp(-1.0 / (0.025 * Rate)),
        });
        if (tones.Count > 48) tones.RemoveAt(0);
    }

    /// <summary>모노 샘플 (−1 ~ 1 근처).</summary>
    public double Generate()
    {
        double sum = 0;
        for (int i = tones.Count - 1; i >= 0; i--)
        {
            var t = tones[i];
            double attack = t.Age < 88 ? t.Age / 88.0 : 1.0;   // 2ms
            double s = Math.Sin(t.P1) + 0.28 * t.A2 * Math.Sin(t.P2) + 0.22 * t.A3 * Math.Sin(t.P3);
            sum += s * t.Amp * attack;
            t.P1 += t.W1; t.P2 += t.W2; t.P3 += t.W3;
            if (t.P1 > Math.PI * 2) t.P1 -= Math.PI * 2;
            if (t.P2 > Math.PI * 2) t.P2 -= Math.PI * 2;
            if (t.P3 > Math.PI * 2) t.P3 -= Math.PI * 2;
            t.Amp *= t.Decay; t.A2 *= t.Decay2; t.A3 *= t.Decay3;
            t.Age++;
            if (t.Amp < 1e-4) tones.RemoveAt(i);
        }
        return sum;
    }
}
