using System;
using System.IO;
using AdlibComposer.Audio;
using AdlibComposer.Core;
using AdlibComposer.Formats;
using AdlibComposer.Nuked;

namespace AdlibComposer.SelfTest;

/// <summary>Nuked OPL3 포팅 · 코어 바꾸기 (OplChip.GlobalCore).</summary>
public static class NukedTests
{
    private static readonly int[] ModOff = { 0, 1, 2, 8, 9, 10, 16, 17, 18 };
    private static readonly int[] CarOff = { 3, 4, 5, 11, 12, 13, 19, 20, 21 };

    public static void Run(Action<string, bool, string> check)
    {
        var saved = OplChip.GlobalCore;
        try { RunChip(check); RunEngine(check); }
        finally { OplChip.GlobalCore = saved; }
    }

    // ------------------------------------------------------------ 칩 단독

    private static void RunChip(Action<string, bool, string> check)
    {
        // 사인 A4 근처: F-num 0x241, block 4 → 49716 × 0x241 / 2^(20-4) ≈ 438.6Hz
        var c = new NukedOpl3();
        WriteSine(c, 0x000, 0, 0x00);
        KeyOn(c, 0x000, 0, true);
        Measure(c, 2000, out _, out _, out _);
        Measure(c, NukedOpl3.NativeRate, out double l, out double r, out int crossings);
        check("Nuked sine pitch ≈ 438.6 Hz", Math.Abs(crossings / 2.0 - 438.6) < 2, (crossings / 2.0).ToString("0.0") + " Hz");
        check("Nuked OPL2 mode outputs both sides", l > 1000 && Math.Abs(l - r) < l * 0.01, l.ToString("0") + "/" + r.ToString("0"));

        var s = new NukedOpl3();
        s.WriteReg(0x105, 1);
        WriteSine(s, 0x000, 0, 0x10);    // 왼쪽만
        WriteSine(s, 0x100, 0, 0x20);    // 오른쪽만
        KeyOn(s, 0x000, 0, true);
        Measure(s, 4000, out l, out r, out _);
        check("Nuked OPL3 left-only channel", l > 1000 && r < 1, l.ToString("0") + "/" + r.ToString("0"));
        KeyOn(s, 0x000, 0, false);
        KeyOn(s, 0x100, 0, true);
        Measure(s, 30000, out _, out _, out _);
        Measure(s, 4000, out l, out r, out _);
        check("Nuked OPL3 bank 2 right-only channel", r > 1000 && l < 50, l.ToString("0") + "/" + r.ToString("0"));

        // 4-op: 채널 0+3, 연결 11. 채널 0 캐리어는 무음 → 2-op로는 소리 없음, 4-op면 채널 3 캐리어가 울린다
        var q = new NukedOpl3();
        q.WriteReg(0x105, 1);
        WriteSine(q, 0x000, 0, 0x31);
        q.WriteReg(0x40 + CarOff[0], 0x3F);
        WriteSine(q, 0x000, 3, 0x31);
        KeyOn(q, 0x000, 0, true);
        Measure(q, 4000, out l, out _, out _);
        double twoOp = l;
        q.WriteReg(0x104, 0x01);
        KeyOn(q, 0x000, 0, false); Measure(q, 100, out _, out _, out _);
        KeyOn(q, 0x000, 0, true);
        Measure(q, 4000, out l, out _, out _);
        check("Nuked 4-op pair keys operators 3·4 from channel 0", twoOp < 100 && l > 1000, twoOp.ToString("0") + " → " + l.ToString("0"));

        // 속도: 18채널 모두 울리는 OPL3 5초 분량이 실시간보다 충분히 빨라야 한다
        var p = new NukedOpl3();
        p.WriteReg(0x105, 1);
        for (int b = 0; b < 2; b++) for (int ch = 0; ch < 9; ch++) { WriteSine(p, b << 8, ch, 0x3E); KeyOn(p, b << 8, ch, true); }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < NukedOpl3.NativeRate * 5; i++) p.Generate(out _, out _);
        double speed = 5.0 / sw.Elapsed.TotalSeconds;
        check("Nuked runs ≥ 10× real time", speed >= 10, speed.ToString("0") + "×");

        // 리듬 모드: BD 키 → 소리
        var d = new NukedOpl3();
        WriteSine(d, 0x000, 6, 0x00);
        d.WriteReg(0xBD, 0x20 | 0x10);
        Measure(d, 4000, out l, out _, out _);
        check("Nuked rhythm bass drum sounds", l > 500, l.ToString("0"));
    }

    private static void WriteSine(NukedOpl3 c, int bank, int ch, int c0)
    {
        foreach (var (off, tl) in new[] { (ModOff[ch], 0x3F), (CarOff[ch], 0x00) })
        {
            c.WriteReg(bank | (0x20 + off), 0x21);
            c.WriteReg(bank | (0x40 + off), tl);
            c.WriteReg(bank | (0x60 + off), 0xF0);
            c.WriteReg(bank | (0x80 + off), 0x0F);
            c.WriteReg(bank | (0xE0 + off), 0);
        }
        c.WriteReg(bank | (0xC0 + ch), c0);
        c.WriteReg(bank | (0xA0 + ch), 0x41);
    }

    private static void KeyOn(NukedOpl3 c, int bank, int ch, bool on)
    {
        c.WriteReg(bank | (0xB0 + ch), (on ? 0x20 : 0) | (4 << 2) | 0x02);
    }

    private static void Measure(NukedOpl3 c, int n, out double l, out double r, out int crossings)
    {
        double sl = 0, sr = 0; crossings = 0; short prev = 0;
        for (int i = 0; i < n; i++)
        {
            c.Generate(out short a, out short b);
            sl += (double)a * a; sr += (double)b * b;
            if (i > 0 && (prev < 0) != (a < 0)) crossings++;
            prev = a;
        }
        l = Math.Sqrt(sl / n); r = Math.Sqrt(sr / n);
    }

    // ------------------------------------------------------------ 엔진 (전체 반영)

    private static void RunEngine(Action<string, bool, string> check)
    {
        string dir = SopTests.FindDir("samples");
        if (dir == null || !File.Exists(Path.Combine(dir, "demo.rol"))) { Console.WriteLine("SKIP  Nuked engine (samples/demo.rol not found)"); return; }
        Song song; InstrumentBank bank;
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.rol"))) song = new RolFormat().Read(fs);
        using (var fs = File.OpenRead(Path.Combine(dir, "demo.bnk"))) bank = new BnkFormat().Read(fs);

        double Rol(OplCore core, out int crossings)
        {
            OplChip.GlobalCore = core;
            var e = new AudioEngine();
            var buf = new short[44100 * 2];
            e.Play(song, bank, 0, -1, -1, -1, new PlaybackOptions());
            e.Render(buf, 4410);
            e.Render(buf, buf.Length);
            double sum = 0; crossings = 0;
            for (int i = 0; i < buf.Length; i++) { sum += (double)buf[i] * buf[i]; if (i > 0 && (buf[i - 1] < 0) != (buf[i] < 0)) crossings++; }
            return Math.Sqrt(sum / buf.Length);
        }
        double rb = Rol(OplCore.Builtin, out int xb), rn = Rol(OplCore.Nuked, out int xn);
        double db = 20 * Math.Log10(rn / rb);
        check("Nuked ROL loudness close to builtin (±3 dB)", Math.Abs(db) < 3, rb.ToString("0") + " / " + rn.ToString("0") + " = " + db.ToString("0.0") + " dB");

        // 같은 사인을 두 코어로: 음높이가 같아야 한다 (곡 전체의 영점 교차는 음색 차이도 섞여 비교하지 않는다)
        int SineCrossings(OplCore core)
        {
            OplChip.GlobalCore = core;
            var chip = new OplChip(false);
            foreach (var (off, tl) in new[] { (ModOff[0], 0x3F), (CarOff[0], 0x00) })
            {
                chip.Write(0x20 + off, 0x21); chip.Write(0x40 + off, tl); chip.Write(0x60 + off, 0xF0);
                chip.Write(0x80 + off, 0x0F); chip.Write(0xE0 + off, 0);
            }
            chip.Write(0xC0, 0); chip.Write(0xA0, 0x41); chip.Write(0xB0, 0x20 | (4 << 2) | 0x02);
            for (int i = 0; i < 2000; i++) chip.Generate();
            int n = 0; short p = 0;
            for (int i = 0; i < chip.NativeRate; i++) { short v = chip.Generate(); if (i > 0 && (p < 0) != (v < 0)) n++; p = v; }
            return n;
        }
        int sb0 = SineCrossings(OplCore.Builtin), sn0 = SineCrossings(OplCore.Nuked);
        check("Nuked sine pitch same as builtin (±1 Hz)", Math.Abs(sb0 - sn0) <= 2, (sb0 / 2.0).ToString("0.0") + " / " + (sn0 / 2.0).ToString("0.0") + " Hz");

        // SOP (4-op · 스테레오)
        OplChip.GlobalCore = OplCore.Nuked;
        var sop = SopFormat.Read(new MemoryStream(SopTests.BuildSop()));
        var eng = new AudioEngine();
        eng.PlaySop(sop);
        var sb = new short[44100 * 2];
        eng.RenderStereo(sb, 44100 * 4 / 10);
        double sl = 0, sr = 0;
        for (int i = 0; i < 44100 * 4 / 10; i++) { sl += Math.Abs((int)sb[2 * i]); sr += Math.Abs((int)sb[2 * i + 1]); }
        check("Nuked SOP plays both sides (4-op right + 2-op left)", sl > 1000 && sr > 1000, sl.ToString("0") + " / " + sr.ToString("0"));

        // 재생 중에 코어 바꾸기: 소리가 끊기지 않고 이어진다
        OplChip.GlobalCore = OplCore.Builtin;
        var sw = new AudioEngine();
        var b2 = new short[22050];
        sw.Play(song, bank, 0, -1, -1, -1, new PlaybackOptions());
        sw.Render(b2, b2.Length);
        OplChip.GlobalCore = OplCore.Nuked;
        sw.Render(b2, b2.Length);
        double after = 0;
        foreach (var v in b2) after += (double)v * v;
        after = Math.Sqrt(after / b2.Length);
        check("Switching core mid-song keeps playing", after > 100, after.ToString("0"));
    }
}
