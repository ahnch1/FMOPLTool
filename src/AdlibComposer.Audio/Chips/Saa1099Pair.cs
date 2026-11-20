namespace AdlibComposer.Audio.Chips;

/// <summary>
/// Creative Game Blaster (C/MS): SAA1099 둘 (7.159MHz) = 채널 12 (Q1~Q6 · Q7~Q12). Write port 0 = 첫 칩, 1 = 둘째 칩 (hoot PC/AT 기계 쓰기 종류 0 · 1).
/// </summary>
public sealed class Saa1099Pair : ISoundChip
{
    public const double GameBlasterClock = 14318180 / 2.0;
    private readonly Saa1099 a, b;

    public Saa1099Pair(double clock = GameBlasterClock, int rate = 44100)
    {
        a = new Saa1099(clock, rate); b = new Saa1099(clock, rate);
        NativeRate = a.NativeRate;
    }

    public string Name { get { return "SAA1099 × 2 (Game Blaster)"; } }
    public int NativeRate { get; }
    public int ChannelCount { get { return 12; } }
    public string ChannelLabel(int ch) { return "Q" + (ch + 1); }
    public void Reset() { a.Reset(); b.Reset(); }
    public void Write(int port, int reg, int value) { (port == 0 ? a : b).Write(0, reg, value); }

    public void Generate(out int left, out int right)
    {
        a.Generate(out int l1, out int r1); b.Generate(out int l2, out int r2);
        left = l1 + l2; right = r1 + r2;
    }

    public void SetMute(int ch, bool mute) { if (ch < 6) a.SetMute(ch, mute); else b.SetMute(ch - 6, mute); }
    public void SetPitch(int ch, double ratio) { if (ch < 6) a.SetPitch(ch, ratio); else b.SetPitch(ch - 6, ratio); }
    public double ChannelFrequency(int ch) { return ch < 6 ? a.ChannelFrequency(ch) : b.ChannelFrequency(ch - 6); }
    public float ChannelLevel(int ch) { return ch < 6 ? a.ChannelLevel(ch) : b.ChannelLevel(ch - 6); }
    public bool ChannelActive(int ch) { return ch < 6 ? a.ChannelActive(ch) : b.ChannelActive(ch - 6); }
}
