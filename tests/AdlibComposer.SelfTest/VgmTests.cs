using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>VGM 읽기 (.vgm / .vgz · GD3 · 루프) · 재생기 (PSG · SCC · OPL2 · 조옮김 · 끄기 · 속도 · 이동) · 오디오 엔진 연결.</summary>
public static class VgmTests
{
    private const int Rate = 44100;

    /// <summary>시험용 VGM 1.61 만들기 (머리말 0x100바이트).</summary>
    private sealed class Builder
    {
        private readonly byte[] head = new byte[0x100];
        private readonly List<byte> data = new List<byte>();
        private long total, loopSample = -1;
        private int loopAt = -1;

        public Builder Clock(int ofs, uint hz) { Put(head, ofs, hz); return this; }
        public Builder AyType(int type) { head[0x78] = (byte)type; return this; }
        public Builder Psg(int r, int v) { data.Add(0xA0); data.Add((byte)r); data.Add((byte)v); return this; }
        public Builder Scc(int port, int r, int v) { data.Add(0xD2); data.Add((byte)port); data.Add((byte)r); data.Add((byte)v); return this; }
        public Builder Opl2(int r, int v) { data.Add(0x5A); data.Add((byte)r); data.Add((byte)v); return this; }
        public Builder Raw(params byte[] b) { data.AddRange(b); return this; }
        public Builder Loop() { loopAt = data.Count; loopSample = total; return this; }

        public Builder Wait(int n)
        {
            total += n;
            while (n > 0)
            {
                int k = Math.Min(n, 65535);
                data.Add(0x61); data.Add((byte)k); data.Add((byte)(k >> 8));
                n -= k;
            }
            return this;
        }

        public byte[] Build(string title = null)
        {
            var d = new List<byte>(head);
            d.AddRange(data);
            d.Add(0x66);
            int gd3 = 0;
            if (title != null)
            {
                gd3 = d.Count;
                var text = Encoding.Unicode.GetBytes(title + "\0\0" + "Test Game\0\0" + "MSX\0\0" + "Composer\0\0" + "1987\0" + "me\0" + "\0");
                d.AddRange(Encoding.ASCII.GetBytes("Gd3 "));
                d.AddRange(BitConverter.GetBytes(0x100));
                d.AddRange(BitConverter.GetBytes(text.Length));
                d.AddRange(text);
            }
            var a = d.ToArray();
            a[0] = (byte)'V'; a[1] = (byte)'g'; a[2] = (byte)'m'; a[3] = (byte)' ';
            Put(a, 0x04, (uint)(a.Length - 4));
            Put(a, 0x08, 0x161);
            Put(a, 0x14, gd3 > 0 ? (uint)(gd3 - 0x14) : 0);
            Put(a, 0x18, (uint)total);
            if (loopAt >= 0) { Put(a, 0x1C, (uint)(0x100 + loopAt - 0x1C)); Put(a, 0x20, (uint)(total - loopSample)); }
            Put(a, 0x34, 0x100 - 0x34);
            return a;
        }

        private static void Put(byte[] a, int o, uint v) { a[o] = (byte)v; a[o + 1] = (byte)(v >> 8); a[o + 2] = (byte)(v >> 16); a[o + 3] = (byte)(v >> 24); }
    }

    private static Builder PsgTone(int period, int seconds = 1)
    {
        return new Builder().Clock(0x74, 1789772).AyType(0x10)
            .Psg(0, period & 0xFF).Psg(1, period >> 8).Psg(7, 0x3E).Psg(8, 15).Wait(Rate * seconds);
    }

    /// <summary>재생기를 n개 샘플 돌려 왼쪽 채널의 RMS와 0 교차 수를 잰다 (앞 0.1초는 직류 차단 때문에 버림).</summary>
    private static void Measure(VgmPlayer p, int n, out double rms, out int crossings)
    {
        for (int i = 0; i < Rate / 10; i++) { p.Step(); p.Generate(out _, out _); }
        double sq = 0; crossings = 0; int prev = 0;
        for (int i = 0; i < n; i++)
        {
            p.Step();
            p.Generate(out double l, out _);
            sq += l * l;
            int sign = l > 1 ? 1 : l < -1 ? -1 : 0;
            if (sign != 0 && prev != 0 && sign != prev) crossings++;
            if (sign != 0) prev = sign;
        }
        rms = Math.Sqrt(sq / n);
    }

    public static void Run(Action<string, bool, string> check)
    {
        Parse(check);
        Psg(check);
        Scc(check);
        Opl(check);
        Timing(check);
        Engine(check);
    }

    private static void Parse(Action<string, bool, string> check)
    {
        var b = new Builder().Clock(0x74, 1789772).AyType(0x10).Clock(0x9C, 0x80000000u | 1789772).Clock(0x0C, 3579545).Clock(0xC8, 8000000)
            .Psg(7, 0x3F).Wait(1000).Loop().Wait(500);
        var raw = b.Build("Stage 1");
        var s = VgmFormat.Read(raw);
        check("VGM header version / length", s.Version == 0x161 && s.TotalSamples == 1500 && s.VersionText == "1.61", s.VersionText + " " + s.TotalSamples);
        check("VGM chips: PSG YM2149 + SCC+ clocks", s.HasPsg && s.AyIsYm && s.HasScc && s.SccPlus && Math.Abs(s.SccClock - 1789772) < 1 && s.ChipSummary == "SN76489 + YM2149 + SCC+", s.ChipSummary);
        check("VGM unsupported chip listed (SAA1099)", s.UnsupportedChips.Contains("SAA1099") && !s.UnsupportedChips.Contains("SN76489") && s.IsPlayable, string.Join(",", s.UnsupportedChips));
        check("VGM loop offset / samples", s.LoopOffset == 0x100 + 3 + 3 && s.LoopSamples == 500, s.LoopOffset + " " + s.LoopSamples);
        check("VGM GD3 tags", s.Title == "Stage 1" && s.Game == "Test Game" && s.SystemName == "MSX" && s.Author == "Composer" && s.ReleaseDate == "1987", s.Title + "|" + s.Game + "|" + s.Author);
        check("VGM play length = total + one loop", VgmPlayer.PlayLength(s) == 2000, VgmPlayer.PlayLength(s).ToString());

        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(raw, 0, raw.Length);
        var z = VgmFormat.Read(ms.ToArray());
        check("VGZ (gzip) reads the same", z.TotalSamples == 1500 && z.Title == "Stage 1" && z.Data.Length == raw.Length, "");

        var sn = VgmFormat.Read(new Builder().Clock(0xC8, 8000000).Raw(0xBD, 0x00, 0x00).Wait(100).Build());
        check("VGM with only SAA1099 is not playable", !sn.IsPlayable && sn.UnsupportedChips.Count == 1, "");

        bool threw = false;
        try { VgmFormat.Read(new byte[0x80]); } catch (InvalidDataException) { threw = true; }
        check("VGM bad magic rejected", threw, "");

        // 머리말 길이가 0이면 명령을 훑어 잰다 · 알지만 쓰지 않는 명령(YM2612 0x52, 데이터 블록 0x67)은 건너뛴다
        var nb = new Builder().Clock(0x74, 1789772).Raw(0x52, 0x28, 0x00).Raw(0x67, 0x66, 0x00, 3, 0, 0, 0, 1, 2, 3).Raw(0x70, 0x7F).Wait(100).Build();
        nb[0x18] = nb[0x19] = nb[0x1A] = nb[0x1B] = 0;
        var c = VgmFormat.Read(nb);
        check("VGM length counted from commands when header says 0", c.TotalSamples == 1 + 16 + 100, c.TotalSamples.ToString());
    }

    private static void Psg(Action<string, bool, string> check)
    {
        var s = VgmFormat.Read(PsgTone(254).Build());
        var p = new VgmPlayer();
        p.Start(s, 0, null);
        Measure(p, Rate / 2, out double rms, out int cross);
        double hz = cross / 2.0 * 2;
        check("VGM PSG tone A ≈ 440 Hz", Math.Abs(hz - 440.4) < 4, hz.ToString("0.0"));
        check("VGM PSG tone is loud", rms > 2000, rms.ToString("0"));
        var st = new VoiceState[PlaybackOptions.Voices];
        p.CopyStates(st);
        check("VGM PSG voice state: A = note 69, B/C silent", st[0].Note == 69 && st[1].Note == -1 && st[2].Note == -1 && st[0].Instrument == "톤", st[0].Note + " " + st[0].Instrument);
        check("VGM channels: A B C", p.Channels.Count == 3 && p.Channels[0].Label == "A" && p.Channels[2].Label == "C", "");

        // 키 +12 → 한 옥타브 위 (분주를 반으로)
        var o = new PlaybackOptions { Transpose = 12 };
        p.Start(s, 0, o);
        Measure(p, Rate / 2, out _, out int cross2);
        check("VGM PSG transpose +12 → ≈ 880 Hz", Math.Abs(cross2 * 2 / 2.0 - 880.8) < 8, (cross2).ToString());
        p.CopyStates(st);
        check("VGM voice state note is before transpose", st[0].Note == 69, st[0].Note.ToString());

        // 재생 중 채널 끄기
        var m = new PlaybackOptions();
        p.Start(s, 0, m);
        Measure(p, Rate / 10, out _, out _);
        m.VoiceMute[0] = true;
        Measure(p, Rate / 4, out double rmsMuted, out _);
        check("VGM PSG mute A → silence", rmsMuted < 50, rmsMuted.ToString("0"));

        var used = VgmPlayer.ScanUsed(s);
        check("VGM scan: only channel A used", used[0] && !used[1] && !used[2], "");
    }

    private static void Scc(Action<string, bool, string> check)
    {
        // 사각파, N = 253 → 3579545 / (32 × 254) = 440.4Hz, 채널 1만
        var b = new Builder().Clock(0x9C, 3579545);
        for (int i = 0; i < 32; i++) b.Scc(0, i, i < 16 ? 100 : -100 & 0xFF);
        b.Scc(1, 0, 253).Scc(1, 1, 0).Scc(2, 0, 15).Scc(3, 0, 0x01).Wait(Rate);
        var s = VgmFormat.Read(b.Build());
        var p = new VgmPlayer();
        p.Start(s, 0, null);
        Measure(p, Rate / 2, out double rms, out int cross);
        check("VGM SCC channel 1 ≈ 440 Hz", Math.Abs(cross - 440.4) < 4, cross.ToString());
        check("VGM SCC is loud", rms > 2000, rms.ToString("0"));
        var st = new VoiceState[PlaybackOptions.Voices];
        p.CopyStates(st);
        check("VGM SCC voice state: S1 = note 69", st[0].Note == 69 && st[1].Note == -1 && p.Channels[0].Label == "S1", st[0].Note.ToString());

        p.Start(s, 0, new PlaybackOptions { Transpose = -12 });
        Measure(p, Rate / 2, out _, out int crossLow);
        check("VGM SCC transpose -12 → ≈ 220 Hz", Math.Abs(crossLow * 2 / 2.0 - 220.2) < 6, crossLow.ToString());
    }

    private static void Opl(Action<string, bool, string> check)
    {
        // 채널 0: 사인파 캐리어만 (모듈레이터 0), F-number 580 · 블록 4 ≈ 440Hz
        var b = new Builder().Clock(0x50, 3579545)
            .Opl2(0x20, 0x01).Opl2(0x23, 0x01)       // MULT 1
            .Opl2(0x40, 0x3F).Opl2(0x43, 0x00)       // 모듈레이터 꺼짐 · 캐리어 최대
            .Opl2(0x60, 0xF0).Opl2(0x63, 0xF0)       // 빠른 어택
            .Opl2(0x80, 0x00).Opl2(0x83, 0x00)       // 서스테인 최대
            .Opl2(0xC0, 0x01)                        // 덧셈 연결
            .Opl2(0xA0, 580 & 0xFF).Opl2(0xB0, 0x20 | (4 << 2) | (580 >> 8))
            .Wait(Rate);
        var s = VgmFormat.Read(b.Build());
        check("VGM OPL2 chip detected", s.HasOpl && !s.IsOpl3 && s.ChipSummary == "OPL2", s.ChipSummary);
        var p = new VgmPlayer();
        p.Start(s, 0, null);
        Measure(p, Rate / 2, out double rms, out int cross);
        check("VGM OPL2 ≈ 440 Hz", Math.Abs(cross - 440) < 6, cross.ToString());
        check("VGM OPL2 is audible", rms > 500, rms.ToString("0"));
        var st = new VoiceState[PlaybackOptions.Voices];
        p.CopyStates(st);
        check("VGM OPL2 voice state: F1 = note 69, 9 channels", st[0].Note == 69 && p.Channels.Count == 9 && p.Channels[0].Label == "F1", st[0].Note.ToString());

        var m = new PlaybackOptions();
        m.VoiceMute[0] = true;
        p.Start(s, 0, m);
        Measure(p, Rate / 4, out double muted, out _);
        check("VGM OPL2 mute F1 → key off", muted < rms * 0.1, muted.ToString("0"));

        p.Start(s, 0, new PlaybackOptions { Transpose = 12 });
        Measure(p, Rate / 2, out _, out int crossUp);
        check("VGM OPL2 transpose +12 → ≈ 880 Hz", Math.Abs(crossUp - 880) < 12, crossUp.ToString());
    }

    private static void Timing(Action<string, bool, string> check)
    {
        // 1초 뒤 B 톤을 켬 → 0.5초 지점에서 시작하면 A만, 1.2초에서 시작하면 B도
        var b = PsgTone(254).Psg(2, 100).Psg(7, 0x3C).Psg(9, 15).Wait(Rate);
        var s = VgmFormat.Read(b.Build());
        var p = new VgmPlayer();
        var st = new VoiceState[PlaybackOptions.Voices];
        p.Start(s, 500, null);
        p.CopyStates(st);
        check("VGM seek 500 ms: position and state", Math.Abs(p.PositionMs - 500) < 0.1 && st[0].Note == 69 && st[1].Note == -1, p.PositionMs.ToString("0.0"));
        p.Start(s, 1200, null);
        p.CopyStates(st);
        check("VGM seek 1200 ms: B already on", st[1].Note >= 0, st[1].Note.ToString());

        // 속도 2배 → 2초 곡이 1초에 끝
        p.Start(s, 0, new PlaybackOptions { Speed = 2.0 });
        int n = 0;
        while (p.IsPlaying && n < Rate * 3) { p.Step(); p.Generate(out _, out _); n++; }
        check("VGM speed 2x halves play time", Math.Abs(n - Rate) < 10, n.ToString());

        // 루프: 1초 + 0.5초 루프 → 1.5 + 0.5 = 2초에 끝, 마지막은 줄어든다
        var lb = PsgTone(254, 1).Loop().Psg(8, 15).Wait(Rate / 2);
        var ls = VgmFormat.Read(lb.Build());
        check("VGM loop song play seconds = 2.0", Math.Abs(VgmPlayer.PlaySeconds(ls) - 2.0) < 1e-9, VgmPlayer.PlaySeconds(ls).ToString());
        p.Start(ls, 0, null);
        n = 0;
        double peakEarly = 0, peakLate = 0;
        while (p.IsPlaying && n < Rate * 4)
        {
            p.Step(); p.Generate(out double l, out _); n++;
            if (n > Rate / 4 && n < Rate / 2) peakEarly = Math.Max(peakEarly, Math.Abs(l));
            if (n > Rate * 2 - 400) peakLate = Math.Max(peakLate, Math.Abs(l));
        }
        check("VGM loop plays once more then stops at 2 s", Math.Abs(n - 2 * Rate) < 10, n.ToString());
        check("VGM loop end is faded out", peakLate < peakEarly * 0.05, peakLate.ToString("0") + " / " + peakEarly.ToString("0"));

        // 루프 없는 곡: 끝에서 멈춘다
        p.Start(s, 0, null);
        n = 0;
        while (p.IsPlaying && n < Rate * 5) { p.Step(); p.Generate(out _, out _); n++; }
        check("VGM non-loop song stops at its length", Math.Abs(n - 2 * Rate) < 10, n.ToString());
    }

    private static void Engine(Action<string, bool, string> check)
    {
        var s = VgmFormat.Read(PsgTone(254).Build());
        using (var engine = new AudioEngine())
        {
            engine.PlayVgm(s, 0, new PlaybackOptions());
            var buf = new short[Rate / 2];
            engine.Render(buf, buf.Length);
            double sq = 0;
            for (int i = Rate / 10; i < buf.Length; i++) sq += (double)buf[i] * buf[i];
            double rms = Math.Sqrt(sq / (buf.Length - Rate / 10));
            check("AudioEngine.PlayVgm renders sound", rms > 1000 && engine.IsPlaying, rms.ToString("0"));
            check("AudioEngine VGM position in ms", Math.Abs(engine.PositionTick - 500) < 2, engine.PositionTick.ToString("0.0"));
            var st = new VoiceState[PlaybackOptions.Voices];
            engine.GetVoiceStates(st);
            check("AudioEngine VGM voice states", st[0].Note == 69, st[0].Note.ToString());
            engine.Stop();
            check("AudioEngine VGM stop", !engine.IsPlaying, "");
        }
    }
}
