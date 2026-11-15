using System;

namespace AdlibComposer.Audio.Chips;

/// <summary>
/// OKI MSM6295: 4채널 4비트 ADPCM (OKI/Dialogic 방식), 소리 데이터는 바깥 ROM (VGM 데이터 블록 0x8B).
/// 샘플레이트 = 클럭 / 132 (핀7 높음) 또는 / 165. 명령: 1xxxxxxx = 소리 번호 고르기 → 다음 바이트 위 4비트 = 채널, 아래 4비트 = 감쇠, 0xxxx000 = 채널 멈춤.
/// <para>VGM 0xB8 칸: 0x00 명령, 0x0C 핀7, 0x0F 뱅크 (256KB 단위).</para>
/// </summary>
public sealed class Okim6295 : ISoundChip
{
    public const double DefaultClock = 1000000;
    private byte[] rom = new byte[0];
    private readonly double clock;
    private bool pin7;
    private int bank;
    private int pendingPhrase = -1;
    private readonly Voice[] v = { new Voice(), new Voice(), new Voice(), new Voice() };
    private readonly bool[] mute = new bool[4];
    private readonly int outRate;

    private sealed class Voice
    {
        public bool Playing;
        public int Addr, End;      // 니블 위치
        public int Signal, Step;
        public int Vol;            // 0~15 감쇠 단계
        public int Prev;
        public double Pitch = 1.0, Pos;
    }

    private static readonly int[] StepSize = BuildSteps();
    private static readonly int[] IndexShift = { -1, -1, -1, -1, 2, 4, 6, 8 };
    private static readonly double[] AttTab = { 1.0, 0.7071, 0.5, 0.3536, 0.25, 0.1768, 0.125, 0.0884, 0.0625, 0, 0, 0, 0, 0, 0, 0 };

    private static int[] BuildSteps()
    {
        var t = new int[49];
        for (int i = 0; i < 49; i++) t[i] = (int)Math.Floor(16.0 * Math.Pow(11.0 / 10.0, i));
        return t;
    }

    /// <param name="clock">클럭 (Hz). VGM 머리말의 bit31 = 핀7.</param>
    public Okim6295(double clock = DefaultClock, bool pin7High = true, int rate = 44100)
    {
        this.clock = clock > 0 ? clock : DefaultClock;
        pin7 = pin7High;
        outRate = rate;
        NativeRate = rate;
        Reset();
    }

    public string Name { get { return "OKI MSM6295 (ADPCM)"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 4; } }
    public string ChannelLabel(int ch) { return "P" + (ch + 1); }

    private double ChipRate { get { return clock / (pin7 ? 132.0 : 165.0); } }

    public void LoadRom(byte[] data, int start)
    {
        if (start < 0) return;
        int need = start + data.Length;
        if (need > rom.Length) Array.Resize(ref rom, need);
        Array.Copy(data, 0, rom, start, data.Length);
    }

    public void Reset()
    {
        foreach (var x in v) { x.Playing = false; x.Signal = 0; x.Step = 0; x.Prev = 0; }
        pendingPhrase = -1; bank = 0;
    }

    private int Rom(int a) { a += bank * 0x40000; return a >= 0 && a < rom.Length ? rom[a] : 0; }

    public void Write(int port, int r, int val)
    {
        val &= 0xFF;
        switch (r)
        {
            case 0x00: Command(val); break;
            case 0x0C: pin7 = val != 0; break;
            case 0x0F: bank = val; break;
        }
    }

    private void Command(int d)
    {
        if (pendingPhrase >= 0)
        {
            int p = pendingPhrase; pendingPhrase = -1;
            int at = p * 8;
            int start = ((Rom(at) << 16) | (Rom(at + 1) << 8) | Rom(at + 2)) & 0x3FFFF;
            int end = ((Rom(at + 3) << 16) | (Rom(at + 4) << 8) | Rom(at + 5)) & 0x3FFFF;
            for (int i = 0; i < 4; i++)
            {
                if ((d & (0x10 << i)) == 0) continue;
                var x = v[i];
                if (x.Playing) continue;   // 칩: 울리는 채널은 새로 시작하지 않는다
                x.Playing = start < end;
                x.Addr = start * 2; x.End = end * 2 + 1; x.Signal = 0; x.Step = 0; x.Prev = 0; x.Pos = 0;
                x.Vol = d & 15;
            }
            return;
        }
        if ((d & 0x80) != 0) { pendingPhrase = d & 0x7F; return; }
        for (int i = 0; i < 4; i++) if ((d & (0x08 << i)) != 0) v[i].Playing = false;
    }

    private void Decode(Voice x)
    {
        int b = Rom(x.Addr >> 1);
        int n = (x.Addr & 1) == 0 ? b >> 4 : b & 15;
        int ss = StepSize[x.Step];
        int diff = ss >> 3;
        if ((n & 1) != 0) diff += ss >> 2;
        if ((n & 2) != 0) diff += ss >> 1;
        if ((n & 4) != 0) diff += ss;
        x.Prev = x.Signal;
        x.Signal += (n & 8) != 0 ? -diff : diff;
        x.Signal = Math.Max(-2048, Math.Min(2047, x.Signal));
        x.Step = Math.Max(0, Math.Min(48, x.Step + IndexShift[n & 7]));
        x.Addr++;
        if (x.Addr > x.End) x.Playing = false;
    }

    public void Generate(out int left, out int right)
    {
        double sum = 0;
        double adv = ChipRate / outRate;
        for (int i = 0; i < 4; i++)
        {
            var x = v[i];
            if (!x.Playing) continue;
            x.Pos += adv * x.Pitch;
            while (x.Pos >= 1 && x.Playing) { x.Pos -= 1; Decode(x); }
            if (mute[i]) continue;
            sum += (x.Prev + (x.Signal - x.Prev) * x.Pos) * AttTab[x.Vol];
        }
        left = right = SoundChips.Clip(sum * 6);
    }

    public void SetMute(int ch, bool m) { if (ch >= 0 && ch < 4) mute[ch] = m; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < 4) v[ch].Pitch = ratio > 0 ? ratio : 1; }
    public double ChannelFrequency(int ch) { return 0; }
    public float ChannelLevel(int ch) { return ch >= 0 && ch < 4 && v[ch].Playing ? (float)AttTab[v[ch].Vol] : 0; }
    public bool ChannelActive(int ch) { return ch >= 0 && ch < 4 && v[ch].Playing; }
}
