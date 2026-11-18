using System;
using System.IO;
using System.Linq;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>XM · IT 곡: 음높이 · 미끄럼 · 엔벨로프 · 길이 · NNA (진단 --extsurvey · --extrender 폴더).</summary>
public static class ExtModuleTests
{
    private const int Rate = AdlibComposer.Audio.ExtModulePlayer.Rate;

    public static void Run(Action<string, bool, string> check)
    {
        // 음높이: 한 주기 32샘플 사인 → 출력 주파수 = 샘플 재생 속도 / 32
        double f = Pitch(Module(true, true, c5: 14112), 61);   // IT C-5
        check("XM/IT: IT C-5 plays at C5Speed", Math.Abs(f - 441) < 3, f.ToString("0.0") + " Hz (441)");
        f = Pitch(Module(true, true, c5: 14112), 73);           // C-6
        check("XM/IT: IT C-6 = one octave up", Math.Abs(f - 882) < 5, f.ToString("0.0") + " Hz (882)");
        f = Pitch(Module(false, true, c5: 8363 * 2), 49);       // XM C-4 = 8363Hz
        check("XM/IT: XM C-4 = 8363 Hz sample rate (linear)", Math.Abs(f - 8363 / 32.0) < 2, f.ToString("0.0") + " Hz (261.3)");
        f = Pitch(Module(false, false, c5: 8363 * 2), 49);
        check("XM/IT: XM C-4 = 8363 Hz sample rate (Amiga)", Math.Abs(f - 8363 / 32.0) < 2, f.ToString("0.0") + " Hz (261.3)");

        // 미끄럼: XM 1xx (위로) / 2xx (아래로), 선형 · 아미가 모두
        foreach (bool lin in new[] { true, false })
        {
            var up = Module(false, lin, c5: 8363 * 2); SetRow(up, 0, 49, 1, 0x02, 0x10);   // 1 10
            var dn = Module(false, lin, c5: 8363 * 2); SetRow(dn, 0, 49, 1, 0x03, 0x10);   // 2 10
            double fu = Pitch(up, -1, 0.3, 0.4), fd = Pitch(dn, -1, 0.3, 0.4);
            check("XM/IT: porta up/down direction (" + (lin ? "linear" : "Amiga") + ")", fu > 290 && fd < 240, fu.ToString("0") + " / " + fd.ToString("0") + " Hz from 261");
        }

        // 엔벨로프 + 키 오프 + 페이드아웃: 서스테인 동안 소리, 키 오프 뒤 사라짐
        var env = Module(false, true, c5: 8363 * 2);
        var ins = env.Instruments[0];
        ins.VolumeEnv.Enabled = true; ins.VolumeEnv.Sustain = true;
        ins.VolumeEnv.Ticks.AddRange(new[] { 0, 10 }); ins.VolumeEnv.Values.AddRange(new[] { 64, 64 });
        ins.FadeOut = 1 / 12.0;   // 12틱에 0
        env.Patterns[0][16, 0] = new ExtCell { Note = ExtModule.NoteOff };
        double before = Rms(env, 1.0, 1.5), after = Rms(env, 2.4, 3.0);
        check("XM/IT: sustain holds, key off + fadeout silences", before > 1000 && after < before * 0.02, before.ToString("0") + " → " + after.ToString("0"));

        // 길이: 2주문 × 64줄 × 속도 6 × 2.5/125초 = 15.36초
        var two = Module(false, true, c5: 8363 * 2);
        two.Orders.Add(0);
        double len = AdlibComposer.Audio.ExtModulePlayer.MeasureSeconds(two);
        check("XM/IT: song length from orders · speed · tempo", Math.Abs(len - 15.36) < 0.05, len.ToString("0.00") + " s");
        var jump = Module(false, true, c5: 8363 * 2);
        jump.Orders.Add(0);
        SetRow(jump, 31, 0, 0, 0x0D + 1, 0x00);   // D00: 다음 주문 0번 줄로 (절반만)
        len = AdlibComposer.Audio.ExtModulePlayer.MeasureSeconds(jump);
        check("XM/IT: pattern break shortens the song", Math.Abs(len - (32 + 32) * 6 * 0.02) < 0.05, len.ToString("0.00") + " s (" + ((32 + 32) * 0.12).ToString("0.00") + ")");   // 두 주문 모두 32줄에서 끊김

        // IT NNA 이어서: 첫 음(C-5)이 둘째 음(G-5) 뒤에도 울린다 / 컷이면 사라진다
        foreach (int nna in new[] { 1, 0 })
        {
            var m = Module(true, true, c5: 14112);
            m.UseInstruments = true;
            var it = new ExtInstrument { Nna = nna };
            for (int n = 0; n < 120; n++) it.KeySample[n] = 1;
            m.Instruments.Add(it);
            m.Patterns[0][0, 0] = new ExtCell { Note = 61, Instrument = 1 };
            m.Patterns[0][8, 0] = new ExtCell { Note = 68, Instrument = 1 };   // G-5
            var pcm = Render(m, 1.4);
            double c5 = Goertzel(pcm, 1.1, 1.4, 441), g5 = Goertzel(pcm, 1.1, 1.4, 441 * Math.Pow(2, 7 / 12.0));
            if (nna == 1) check("XM/IT: IT NNA continue keeps the old note", c5 > g5 * 0.3 && g5 > 0, "C-5 " + c5.ToString("0") + ", G-5 " + g5.ToString("0"));
            else check("XM/IT: IT NNA cut stops the old note", c5 < g5 * 0.05, "C-5 " + c5.ToString("0") + ", G-5 " + g5.ToString("0"));
        }

        // 오디오 엔진 (Jukebox가 쓰는 길): 소리 · 위치(ms) · 채널 상태 · 이동
        using (var e = new AdlibComposer.Audio.AudioEngine())
        {
            var m = Module(false, true, c5: 8363 * 2);
            var opts = new AdlibComposer.Audio.PlaybackOptions();
            e.PlayExtModule(m, opts);
            var buf = new short[Rate / 2 * 2];
            e.RenderStereo(buf, Rate / 2);
            double peak = buf.Max(x => Math.Abs((int)x));
            double pos = e.PositionTick;
            var st = new AdlibComposer.Audio.VoiceState[AdlibComposer.Audio.PlaybackOptions.Voices];
            e.GetVoiceStates(st);
            // XM 음 61 = C-5 = 음 번호 60 → 표시 MIDI 72 (XM C-4가 샘플 본디 높이 = 60)
            check("XM/IT: engine plays, reports ms position and channel note", peak > 1000 && Math.Abs(pos - 500) < 60 && st[0].Note == 72, "peak " + peak + ", pos " + pos.ToString("0") + " ms, note " + st[0].Note);
            e.PlayExtModule(m, opts, 5000);
            e.RenderStereo(buf, 100);
            check("XM/IT: engine seeks (skip ms)", Math.Abs(e.PositionTick - 5000) < 50, e.PositionTick.ToString("0") + " ms");
            e.Stop();
        }
    }

    /// <summary>채널 1개 · 64줄 패턴 1개 · 사인 샘플 1개 (한 주기 32샘플, 루프), 0번 줄에 음(0이면 없음).</summary>
    private static ExtModule Module(bool it, bool linear, double c5)
    {
        var m = new ExtModule { IsIt = it, LinearSlides = linear, Channels = 1, Speed = 6, Tempo = 125, GlobalVolume = 128, MixVolume = 48, UseInstruments = !it };
        for (int c = 0; c < ExtModule.MaxChannels; c++) { m.ChannelPan[c] = 32; m.ChannelVolume[c] = 64; }
        var s = new ExtSample { C5Speed = c5, Loop = true, LoopStart = 0, LoopEnd = 3200, Volume = 64, Pan = 32 };
        s.Data = new short[3200];
        for (int i = 0; i < s.Data.Length; i++) s.Data[i] = (short)(20000 * Math.Sin(2 * Math.PI * i / 32));
        m.Samples.Add(s);
        if (!it)
        {
            var ins = new ExtInstrument();
            for (int n = 0; n < 120; n++) ins.KeySample[n] = 1;
            m.Instruments.Add(ins);
        }
        m.Patterns.Add(new ExtPattern(64, 1));
        m.Orders.Add(0);
        m.Patterns[0][0, 0] = new ExtCell { Note = 61, Instrument = 1 };
        return m;
    }

    private static void SetRow(ExtModule m, int row, int note, int ins, int fx, int prm)
    {
        var c = m.Patterns[0][row, 0];
        if (note > 0) { c.Note = (byte)note; c.Instrument = (byte)ins; }
        c.Effect = (byte)fx; c.Param = (byte)prm;
        m.Patterns[0][row, 0] = c;
    }

    private static double[] Render(ExtModule m, double seconds)
    {
        var p = new AdlibComposer.Audio.ExtModulePlayer();
        p.Start(m);
        var r = new double[(int)(seconds * Rate)];
        for (int i = 0; i < r.Length; i++) { p.Generate(out double l, out double rr); r[i] = (l + rr) / 2; }
        return r;
    }

    /// <summary>0번 줄 음을 note로 바꿔 (−1 = 그대로) from~to초의 주파수 (영점 교차).</summary>
    private static double Pitch(ExtModule m, int note, double from = 0.2, double to = 0.6)
    {
        if (note > 0) { var c = m.Patterns[0][0, 0]; c.Note = (byte)note; m.Patterns[0][0, 0] = c; }
        var pcm = Render(m, to);
        int a = (int)(from * Rate), b = pcm.Length, cross = 0, first = -1, last = -1;
        for (int i = a + 1; i < b; i++)
            if (pcm[i - 1] < 0 && pcm[i] >= 0) { cross++; if (first < 0) first = i; last = i; }
        return cross < 2 ? 0 : (cross - 1) * (double)Rate / (last - first);
    }

    private static double Rms(ExtModule m, double from, double to)
    {
        var pcm = Render(m, to);
        double sum = 0; int a = (int)(from * Rate);
        for (int i = a; i < pcm.Length; i++) sum += pcm[i] * pcm[i];
        return Math.Sqrt(sum / Math.Max(1, pcm.Length - a));
    }

    private static double Goertzel(double[] x, double from, double to, double hz)
    {
        int a = (int)(from * Rate), b = Math.Min(x.Length, (int)(to * Rate));
        double w = 2 * Math.PI * hz / Rate, k = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        for (int i = a; i < b; i++) { double s0 = x[i] + k * s1 - s2; s2 = s1; s1 = s0; }
        return Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - k * s1 * s2)) / (b - a);
    }

    /// <summary>폴더 아래 모든 XM · IT를 읽어 요약을 찍는다. 읽지 못한 파일이 있으면 1.</summary>
    public static int Survey(string dir)
    {
        int ok = 0, bad = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(ExtModule.IsExtModule).OrderBy(x => x))
        {
            try
            {
                var m = ExtModule.Load(f);
                int smp = m.Samples.Count(s => s.Length > 0);
                long peak = m.Samples.Count == 0 ? 0 : m.Samples.Max(s => s.Data.Length == 0 ? 0 : s.Data.Max(x => Math.Abs((int)x)));
                Console.WriteLine("OK   " + Path.GetFileName(f) + "  [" + m.FormatName + " · " + m.Tracker + "] ch " + m.Channels + ", ord " + m.PlayOrders().Count + ", pat " + m.Patterns.Count
                    + ", ins " + m.Instruments.Count + ", smp " + smp + "/" + m.Samples.Count + ", peak " + peak + ", lin " + m.LinearSlides + (m.IsIt ? ", insmode " + m.UseInstruments + ", old " + m.OldEffects : "") + " — " + m.Title);
                ok++;
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + Path.GetFileName(f) + "  " + ex.Message); bad++; }
        }
        Console.WriteLine("ok=" + ok + " fail=" + bad);
        return bad > 0 ? 1 : 0;
    }

    /// <summary>
    /// 폴더(또는 파일)의 XM · IT를 길이 재고 앞 seconds초를 그려 본다: 길이, 최고 · RMS, 찢어짐(±32767 넘은 비율), 빠르기(실시간의 몇 배).
    /// wavDir가 있으면 WAV로도 쓴다. 샘플 매끄러움(이웃 차이 / 크기)이 1 넘으면 압축 풀기가 깨졌을 수 있다.
    /// </summary>
    public static int Render(string path, double seconds, string wavDir)
    {
        var files = File.Exists(path) ? new[] { path } : Directory.EnumerateFiles(path, "*.*", SearchOption.AllDirectories).Where(ExtModule.IsExtModule).OrderBy(x => x).ToArray();
        int bad = 0;
        foreach (var f in files)
        {
            try
            {
                var m = ExtModule.Load(f);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                double len = AdlibComposer.Audio.ExtModulePlayer.MeasureSeconds(m);
                double measureMs = sw.Elapsed.TotalMilliseconds;
                var p = new AdlibComposer.Audio.ExtModulePlayer();
                p.Start(m);
                int n = (int)(Math.Min(seconds, len + 1) * AdlibComposer.Audio.ExtModulePlayer.Rate);
                var pcm = new short[n * 2];
                double peak = 0, sum = 0; int clip = 0;
                sw.Restart();
                for (int i = 0; i < n; i++)
                {
                    p.Generate(out double l, out double r);
                    if (double.IsNaN(l) || double.IsNaN(r)) { l = r = 0; clip++; }
                    peak = Math.Max(peak, Math.Max(Math.Abs(l), Math.Abs(r)));
                    sum += l * l + r * r;
                    if (Math.Abs(l) > 32767 || Math.Abs(r) > 32767) clip++;
                    pcm[i * 2] = (short)Math.Max(-32768, Math.Min(32767, l));
                    pcm[i * 2 + 1] = (short)Math.Max(-32768, Math.Min(32767, r));
                }
                double speed = n / (double)AdlibComposer.Audio.ExtModulePlayer.Rate / Math.Max(1e-6, sw.Elapsed.TotalSeconds);
                double rms = Math.Sqrt(sum / Math.Max(1, 2 * n));
                var rs = m.Samples.Where(s => s.Length > 256).Select(Roughness).OrderBy(x => x).ToList(); double rough = rs.Count == 0 ? 0 : rs[rs.Count / 2];
                Console.WriteLine(Path.GetFileName(f).PadRight(22) + " len " + len.ToString("0.0").PadLeft(6) + "s (" + measureMs.ToString("0") + "ms)  peak " + peak.ToString("0").PadLeft(6)
                    + "  rms " + rms.ToString("0").PadLeft(5) + "  clip " + (clip * 100.0 / n).ToString("0.00") + "%  x" + speed.ToString("0") + "  rough " + rough.ToString("0.00"));
                if (rms < 50 || len < 1) bad++;
                if (wavDir != null)
                {
                    Directory.CreateDirectory(wavDir);
                    WriteWav(Path.Combine(wavDir, Path.GetFileNameWithoutExtension(f) + ".wav"), pcm);
                }
            }
            catch (Exception ex) { Console.WriteLine("FAIL " + Path.GetFileName(f) + "  " + ex); bad++; }
        }
        Console.WriteLine("files=" + files.Length + " suspicious=" + bad);
        return bad > 0 ? 1 : 0;
    }

    /// <summary>평균 |이웃 차이| / 평균 |값|: 소리 샘플은 보통 0.5 아래, 잡음은 1.4 근처.</summary>
    private static double Roughness(ExtSample s)
    {
        double diff = 0, mag = 0;
        for (int i = 1; i < s.Data.Length; i++) { diff += Math.Abs(s.Data[i] - s.Data[i - 1]); mag += Math.Abs((int)s.Data[i]); }
        return mag < 1 ? 0 : diff / mag;
    }

    private static void WriteWav(string path, short[] pcm)
    {
        using (var w = new BinaryWriter(File.Create(path)))
        {
            int bytes = pcm.Length * 2;
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + bytes); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
            w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(44100); w.Write(44100 * 4); w.Write((short)4); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(bytes);
            foreach (var v in pcm) w.Write(v);
        }
    }
}
