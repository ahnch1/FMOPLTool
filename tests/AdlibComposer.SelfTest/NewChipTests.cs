using System;
using AdlibComposer.Audio;
using AdlibComposer.Formats;

namespace AdlibComposer.SelfTest;

/// <summary>
/// 공통 경로 칩 (SN76489 · YM2413 · YM2151 · YM2203 · YM2608 · YM2612 · OKIM6295): VGM을 만들어 VgmPlayer로 렌더해
/// A4 음높이 · 끄기 · 조옮김 · 키 놓기 · PCM/리듬 소리를 본다.
/// </summary>
public static class NewChipTests
{
    private const int Rate = 44100;

    public static void Run(Action<string, bool, string> check)
    {
        Sn(check);
        Opll(check);
        Opm(check);
        Opn(check, VgmWriter.ClockYm2203, 3993600, 0x55, 0x55, "YM2203", 1040);
        Opn(check, VgmWriter.ClockYm2608, 7987200, 0x56, 0x57, "YM2608", 1040);
        Opn(check, VgmWriter.ClockYm2612, 7670453, 0x52, 0x53, "YM2612", 1083);
        OpnaExtras(check);
        Opn2Dac(check);
        Oki(check);
        Parse(check);
    }

    // ------------------------------------------------------------ 도우미

    private static double[] Render(VgmSong s, double sec, PlaybackOptions o = null, double fromSec = 0)
    {
        var p = new VgmPlayer();
        p.Start(s, fromSec * 1000, o);
        var a = new double[(int)(sec * Rate)];
        for (int i = 0; i < a.Length; i++) { p.Step(); p.Generate(out double l, out double r); a[i] = (l + r) / 2; }
        return a;
    }

    private static double Rms(double[] a, int from = 0, int to = int.MaxValue)
    {
        to = Math.Min(to, a.Length);
        double s = 0; for (int i = from; i < to; i++) s += a[i] * a[i];
        return to > from ? Math.Sqrt(s / (to - from)) : 0;
    }

    /// <summary>위로 지나는 0점 수 / 초 (평균을 빼고).</summary>
    private static double Pitch(double[] a, int from, int to)
    {
        double m = 0; for (int i = from; i < to; i++) m += a[i];
        m /= to - from;
        int n = 0; int first = -1, last = -1;
        for (int i = from + 1; i < to; i++)
            if (a[i - 1] - m < 0 && a[i] - m >= 0) { n++; if (first < 0) first = i; last = i; }
        return n > 1 ? (n - 1) * (double)Rate / (last - first) : 0;
    }

    private static bool Near(double hz, double want, double pct = 2) { return Math.Abs(hz - want) <= want * pct / 100; }

    private static VgmPlayer.Channel[] Channels(VgmSong s) { return VgmPlayer.ChannelsOf(s).ToArray(); }

    /// <summary>음 하나: 1초 울리고(키 놓기) 1초 쉼.</summary>
    private static VgmSong Song(Action<VgmWriter> setup, Action<VgmWriter> keyOff, int clockOfs, double clock)
    {
        var w = new VgmWriter();
        w.SetClock(clockOfs, clock);
        setup(w);
        w.Wait(Rate);
        keyOff(w);
        w.Wait(Rate);
        return VgmFormat.Read(w.ToArray());
    }

    private static void Common(Action<string, bool, string> check, string name, VgmSong s, int voice, double want = 440)
    {
        var a = Render(s, 2);
        double hz = Pitch(a, 4410, 40000);
        check(name + ": A4 ≈ 440 Hz", Near(hz, want), hz.ToString("0.0"));
        double on = Rms(a, 4410, 40000), off = Rms(a, Rate + 30000, 2 * Rate);
        check(name + ": key off → quiet", on > 300 && off < on * 0.05, on.ToString("0") + " → " + off.ToString("0"));
        var o = new PlaybackOptions(); o.VoiceMute[voice] = true;
        double muted = Rms(Render(s, 0.9, o), 4410);
        check(name + ": channel mute", muted < on * 0.02, muted.ToString("0"));
        var t = new PlaybackOptions { Transpose = 12 };
        double up = Pitch(Render(s, 0.9, t), 4410, 38000);
        check(name + ": transpose +12 ≈ 880 Hz", Near(up, want * 2), up.ToString("0.0"));
        var used = VgmPlayer.ScanUsed(s);
        check(name + ": used channel found", used[voice], Channels(s)[voice].Label);
    }

    // ------------------------------------------------------------ 칩

    private static void Sn(Action<string, bool, string> check)
    {
        int n = 254;
        var s = Song(w =>
        {
            w.Write(0x50, 0x80 | (n & 15));
            w.Write(0x50, (n >> 4) & 0x3F);
            w.Write(0x50, 0x90);
            w.Write(0x50, 0xBF); w.Write(0x50, 0xDF); w.Write(0x50, 0xFF);
        }, w => w.Write(0x50, 0x9F), VgmWriter.ClockSn76489, 3579545);
        check("SN76489: header", s.Sn76489Clock == 3579545 && s.IsPlayable && s.UnsupportedChips.Count == 0, s.ChipSummary);
        Common(check, "SN76489", s, 0, 3579545.0 / (32 * n));

        var noise = Song(w => { w.Write(0x50, 0xE4); w.Write(0x50, 0xF0); }, w => w.Write(0x50, 0xFF), VgmWriter.ClockSn76489, 3579545);
        double r = Rms(Render(noise, 0.5), 2000);
        check("SN76489: white noise sounds", r > 300, r.ToString("0"));
    }

    private static void Opll(Action<string, bool, string> check)
    {
        // 사용자 악기 = 사인파 하나 (모듈레이터 TL 63, EG 지속)
        byte[] user = { 0x21, 0x21, 0x3F, 0x00, 0xF0, 0xF0, 0x0F, 0x0F };
        var s = Song(w =>
        {
            for (int i = 0; i < 8; i++) w.Write(0x51, i, user[i]);
            w.Write(0x51, 0x30, 0x00);
            w.Write(0x51, 0x10, 290 & 0xFF);
            w.Write(0x51, 0x20, 0x10 | (4 << 1) | (290 >> 8));
        }, w => w.Write(0x51, 0x20, (4 << 1) | (290 >> 8)), VgmWriter.ClockYm2413, 3579545);
        Common(check, "YM2413", s, 0, 290 * (3579545 / 72.0) / Math.Pow(2, 15));

        // 내장 악기 (피아노) · 리듬 BD
        var p = Song(w =>
        {
            w.Write(0x51, 0x30, 0x30);
            w.Write(0x51, 0x10, 290 & 0xFF);
            w.Write(0x51, 0x20, 0x10 | (4 << 1) | 1);
            w.Write(0x51, 0x36, 0x00);
            w.Write(0x51, 0x0E, 0x20);
            w.Write(0x51, 0x0E, 0x30);
        }, w => { w.Write(0x51, 0x20, 0); w.Write(0x51, 0x0E, 0x20); }, VgmWriter.ClockYm2413, 3579545);
        var a = Render(p, 0.5);
        double piano = Rms(a, 2000);
        check("YM2413: ROM piano + rhythm BD sound", piano > 300, piano.ToString("0"));
        var used = VgmPlayer.ScanUsed(p);
        check("YM2413: BD channel used (rhythm)", used[9], string.Join(",", Channels(p)[9].Label));
    }

    private static void Opm(Action<string, bool, string> check)
    {
        var s = Song(w =>
        {
            w.Write(0x54, 0x20, 0xC7);
            w.Write(0x54, 0x28, 0x4A);
            w.Write(0x54, 0x30, 0x00);
            for (int op = 0; op < 4; op++)
            {
                int o = op * 8;
                w.Write(0x54, 0x40 + o, 0x01);
                w.Write(0x54, 0x60 + o, op == 3 ? 0x00 : 0x7F);
                w.Write(0x54, 0x80 + o, 0x1F);
                w.Write(0x54, 0xA0 + o, 0x00);
                w.Write(0x54, 0xC0 + o, 0x00);
                w.Write(0x54, 0xE0 + o, 0x0F);
            }
            w.Write(0x54, 0x08, 0x78);
        }, w => w.Write(0x54, 0x08, 0x00), VgmWriter.ClockYm2151, 3579545);
        Common(check, "YM2151", s, 0);
    }

    private static void OpnFmSetup(VgmWriter w, byte cmd, int fnum)
    {
        w.Write(cmd, 0xB0, 0x07);
        w.Write(cmd, 0xB4, 0xC0);
        for (int op = 0; op < 4; op++)
        {
            int o = op * 4;
            w.Write(cmd, 0x30 + o, 0x01);
            w.Write(cmd, 0x40 + o, op == 3 ? 0x00 : 0x7F);
            w.Write(cmd, 0x50 + o, 0x1F);
            w.Write(cmd, 0x60 + o, 0x00);
            w.Write(cmd, 0x70 + o, 0x00);
            w.Write(cmd, 0x80 + o, 0x0F);
        }
        w.Write(cmd, 0xA4, (4 << 3) | (fnum >> 8));
        w.Write(cmd, 0xA0, fnum & 0xFF);
        w.Write(cmd, 0x28, 0xF0);
    }

    private static void Opn(Action<string, bool, string> check, int clockOfs, double clock, byte cmd0, byte cmd1, string name, int fnum)
    {
        double fs = Math.Round(clock / (name == "YM2203" ? 72 : 144));
        double want = ((fnum << 4) >> 1) * fs / 1048576.0;
        var s = Song(w => OpnFmSetup(w, cmd0, fnum), w => w.Write(cmd0, 0x28, 0x00), clockOfs, clock);
        Common(check, name, s, 0, want);

        if (name == "YM2612") return;
        // SSG 채널 A
        double ssgClock = clock / (name == "YM2203" ? 2 : 4);
        int tp = (int)Math.Round(ssgClock / (16 * 440.0));
        var ssg = Song(w => { w.Write(cmd0, 0x00, tp & 0xFF); w.Write(cmd0, 0x01, tp >> 8); w.Write(cmd0, 0x07, 0x3E); w.Write(cmd0, 0x08, 0x0F); },
            w => w.Write(cmd0, 0x08, 0x00), clockOfs, clock);
        int ssgVoice = name == "YM2203" ? 3 : 6;
        double hz = Pitch(Render(ssg, 0.9), 4410, 38000);
        check(name + ": SSG A ≈ 440 Hz on mixer " + Channels(ssg)[ssgVoice].Label, Near(hz, ssgClock / (16.0 * tp)) && Channels(ssg)[ssgVoice].Label == "A", hz.ToString("0.0"));
    }

    private static void OpnaExtras(Action<string, bool, string> check)
    {
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockYm2608, 7987200);
        w.Write(0x56, 0x11, 0x3F);
        w.Write(0x56, 0x18, 0xDF);
        w.Write(0x56, 0x10, 0x01);
        w.Wait(Rate / 2);
        // ADPCM-B: 블록 0x81 (크기 · 시작 + 데이터)
        var adpcm = new byte[4096];
        for (int i = 0; i < adpcm.Length; i++) adpcm[i] = (byte)((i / 8) % 2 == 0 ? 0x77 : 0xFF);
        var blk = new byte[8 + adpcm.Length];
        BitConverter.GetBytes(0x40000).CopyTo(blk, 0);
        BitConverter.GetBytes(0).CopyTo(blk, 4);
        adpcm.CopyTo(blk, 8);
        w.Write(0x67, 0x66, 0x81); w.Write(BitConverter.GetBytes(blk.Length)); w.Write(blk);
        w.Write(0x57, 0x01, 0xC2);           // L R, RAM ×8 (32바이트 단위)
        w.Write(0x57, 0x02, 0x00); w.Write(0x57, 0x03, 0x00);
        w.Write(0x57, 0x04, 0x7F); w.Write(0x57, 0x05, 0x00);
        w.Write(0x57, 0x09, 0x00); w.Write(0x57, 0x0A, 0x40);   // ≈ 13.8kHz
        w.Write(0x57, 0x0B, 0xFF);
        w.Write(0x57, 0x00, 0xA0);
        w.Wait(Rate / 2);
        var s = VgmFormat.Read(w.ToArray());
        var a = Render(s, 1.0);
        double bd = Rms(a, 0, Rate / 4), pcm = Rms(a, Rate / 2 + 1000, Rate / 2 + 8000);
        check("YM2608: rhythm BD sounds (synthesized)", bd > 200, bd.ToString("0"));
        check("YM2608: ADPCM-B from data block 0x81 plays", pcm > 200, pcm.ToString("0"));
        var ch = Channels(s);
        check("YM2608: mixer = FM 6 + SSG 3 + rhythm 6 + ADPCM", ch.Length == 16 && ch[9].Label == "BD" && ch[15].Label == "PCM", ch.Length.ToString());
    }

    private static void Opn2Dac(Action<string, bool, string> check)
    {
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockYm2612, 7670453);
        var pcm = new byte[8000];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (byte)((i / 20) % 2 == 0 ? 0xE0 : 0x20);
        w.Write(0x67, 0x66, 0x00); w.Write(BitConverter.GetBytes(pcm.Length)); w.Write(pcm);
        w.Write(0x52, 0x2B, 0x80);
        w.Write(0x53, 0xB6, 0xC0);
        w.Write(0xE0, 0, 0, 0, 0);
        for (int i = 0; i < pcm.Length; i++) { w.Write(0x85); }
        // 0x8n의 기다림은 VgmWriter가 모르므로 길이를 따로 더한다
        w.Wait(1);
        var bytes = w.ToArray();
        BitConverter.GetBytes((uint)(pcm.Length * 5 + 1)).CopyTo(bytes, 0x18);
        var s = VgmFormat.Read(bytes);
        var a = Render(s, 0.8);
        double r = Rms(a, 0, Rate * 3 / 4);
        double hz = Pitch(a, 0, Rate * 3 / 4);
        check("YM2612: DAC stream (0x80~0x8F) sounds", r > 300, r.ToString("0"));
        check("YM2612: DAC square ≈ 220 Hz", Near(hz, Rate / 200.0, 5), hz.ToString("0.0"));
    }

    private static void Oki(Action<string, bool, string> check)
    {
        var rom = new byte[0x2000];
        int start = 0x400, end = 0xBFF;
        rom[8] = (byte)(start >> 16); rom[9] = (byte)(start >> 8); rom[10] = (byte)start;
        rom[11] = (byte)(end >> 16); rom[12] = (byte)(end >> 8); rom[13] = (byte)end;
        for (int i = start; i <= end; i++) rom[i] = (byte)((i / 4) % 2 == 0 ? 0x77 : 0xFF);
        var w = new VgmWriter();
        w.SetClock(VgmWriter.ClockOkim6295, 1000000, 0x80000000);
        var blk = new byte[8 + rom.Length];
        BitConverter.GetBytes(rom.Length).CopyTo(blk, 0);
        BitConverter.GetBytes(0).CopyTo(blk, 4);
        rom.CopyTo(blk, 8);
        w.Write(0x67, 0x66, 0x8B); w.Write(BitConverter.GetBytes(blk.Length)); w.Write(blk);
        w.Write(0xB8, 0x00, 0x81);
        w.Write(0xB8, 0x00, 0x10);
        w.Wait(Rate);
        var s = VgmFormat.Read(w.ToArray());
        var a = Render(s, 1.0);
        double on = Rms(a, 100, 8000), after = Rms(a, Rate * 6 / 10, Rate);
        // 0x800바이트 = 4096 니블 / (1MHz / 132) ≈ 0.54초
        check("OKIM6295: phrase from ROM block 0x8B plays then stops", on > 200 && after < on * 0.05, on.ToString("0") + " → " + after.ToString("0"));
        check("OKIM6295: channel P1 used", VgmPlayer.ScanUsed(s)[0], Channels(s)[0].Label);
    }

    private static void Parse(Action<string, bool, string> check)
    {
        // 1.01 파일: 0x10 칸 하나가 YM2612 클럭 (명령 0x52로 구별)
        var b = new byte[0x40 + 4 + 3 + 1];
        b[0] = (byte)'V'; b[1] = (byte)'g'; b[2] = (byte)'m'; b[3] = (byte)' ';
        BitConverter.GetBytes(b.Length - 4).CopyTo(b, 0x04);
        BitConverter.GetBytes(0x101).CopyTo(b, 0x08);
        BitConverter.GetBytes(7670453).CopyTo(b, 0x10);
        BitConverter.GetBytes(100).CopyTo(b, 0x18);
        b[0x40] = 0x52; b[0x41] = 0x28; b[0x42] = 0x00; b[0x43] = 0x61; b[0x44] = 100; b[0x45] = 0; b[0x46] = 0x66;
        var s = VgmFormat.Read(b);
        check("VGM 1.01: shared 0x10 clock → YM2612 by command", s.Ym2612Clock == 7670453 && s.Ym2413Clock == 0, s.ChipSummary);
    }
}
