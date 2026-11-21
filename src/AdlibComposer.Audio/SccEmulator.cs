using System;

namespace AdlibComposer.Audio;

/// <summary>
/// Konami SCC 에뮬레이터: K051649 (SCC) / K052539 (SCC+), MSX 클럭 3.579545MHz.
/// <para>5채널 파형 메모리 신디: 채널마다 32바이트 부호 있는 파형(-128~127), 12비트 분주 (주파수 = 클럭 / (32 × (N + 1))),
/// 4비트 볼륨 (선형), 켜짐 마스크 (bit0~4). 하드웨어 엔벨로프 없음.
/// SCC는 4번 · 5번 채널이 파형 하나를 같이 쓰고, SCC+는 다섯 채널이 따로 쓴다. N이 8 이하면 소리를 내지 않는다 (칩과 같게).</para>
/// <para>쓰는 길 세 가지: <see cref="WriteScc"/> (MSX 0x9800~0x98FF 안의 주소), <see cref="WriteSccPlus"/> (0xB800~0xB8FF),
/// <see cref="WritePort"/> (VGM 0xD2 pp aa dd: 0 = 파형, 1 = 주파수, 2 = 볼륨, 3 = 켜기, 4 = SCC+ 파형, 5 = 시험 레지스터).</para>
/// <para>방식은 emu2212 (Mitsutaka Okazaki, MIT)를 따른다: 위상 누산 + 출력 샘플 하나 동안 지나간 파형을 평균 (높은 음에서 거친 겹침을 줄인다).</para>
/// </summary>
public sealed class SccEmulator : ISoundChip
{
    public const double MsxClock = 3579545.0;
    public const int Channels = 5;

    private readonly sbyte[,] wave = new sbyte[Channels, 32];
    private readonly int[] freq = new int[Channels];
    private readonly int[] volume = new int[Channels];
    private readonly double[] phase = new double[Channels];   // 파형 위치 0~32
    private int enable;
    private int test;

    public SccEmulator(double clock = MsxClock, int rate = 44100, bool sccPlus = false)
    {
        Clock = InputClock(clock);
        Rate = rate;
        IsSccPlus = sccPlus;
        Reset();
    }

    public double Clock { get; }

    /// <summary>
    /// VGM K051649 클럭 → 칩 입력 클럭 (음 = 입력 / (32 × (n + 1))). VGM 규약 · MAME · hoot는 1.79MHz에 ÷16을 쓰므로 2.5MHz 아래면 두 배로 본다
    /// (예전 이 앱이 적던 3.58MHz 파일은 그대로).
    /// </summary>
    public static double InputClock(double vgmClock) { return vgmClock > 0 && vgmClock < 2500000 ? vgmClock * 2 : vgmClock; }

    /// <summary>VGM에 적을 K051649 클럭 (규약 = 입력 클럭의 절반, MSX 1.789772MHz).</summary>
    public static double VgmClock(double inputClock) { return inputClock / 2; }
    public int Rate { get; }
    /// <summary>true = SCC+ (K052539): 채널 5도 자기 파형.</summary>
    public bool IsSccPlus { get; set; }
    public bool[] Mute { get; } = new bool[Channels];

    public void Reset()
    {
        Array.Clear(wave, 0, wave.Length);
        Array.Clear(freq, 0, freq.Length);
        Array.Clear(volume, 0, volume.Length);
        Array.Clear(phase, 0, phase.Length);
        enable = 0;
        test = 0;
    }

    // ------------------------------------------------------------ 쓰기

    /// <summary>SCC 모드 메모리 (0x9800 기준 주소 0x00~0xFF): 0x00~0x7F 파형 1~4 (5 = 4와 같이), 0x80~0x8F 주파수 · 볼륨 · 켜기 (0x90~0x9F 같음), 0xE0~0xFF 시험.</summary>
    public void WriteScc(int addr, int value)
    {
        addr &= 0xFF;
        if (addr < 0x80) { SetWave(addr >> 5, addr & 31, value); if (!IsSccPlus && addr >= 0x60) SetWave(4, addr & 31, value); return; }
        if (addr < 0xA0) { WriteControl(addr & 0x0F, value); return; }
        if (addr >= 0xE0) test = value & 0xFF;
    }

    /// <summary>SCC+ 모드 메모리 (0xB800 기준 0x00~0xFF): 0x00~0x9F 파형 1~5, 0xA0~0xAF 주파수 · 볼륨 · 켜기, 0xC0~0xDF 시험.</summary>
    public void WriteSccPlus(int addr, int value)
    {
        addr &= 0xFF;
        if (addr < 0xA0) { SetWave(addr >> 5, addr & 31, value); return; }
        if (addr < 0xC0) { WriteControl(addr & 0x0F, value); return; }
        if (addr < 0xE0) test = value & 0xFF;
    }

    /// <summary>VGM 0xD2 명령 (포트, 레지스터, 값).</summary>
    public void WritePort(int port, int r, int value)
    {
        switch (port)
        {
            case 0: WriteScc(r & 0x7F, value); break;                         // 파형 (SCC 방식: 4 · 5 같이)
            case 1: WriteControl(r & 0x0F, value); break;                      // 주파수 0~9
            case 2: WriteControl(0x0A + (r % Channels), value); break;         // 볼륨 0~4
            case 3: WriteControl(0x0F, value); break;                          // 켜기
            case 4: SetWave((r & 0xFF) / 32 % Channels, r & 31, value); break; // SCC+ 파형
            case 5: test = value & 0xFF; break;
        }
    }

    private void SetWave(int ch, int i, int value)
    {
        if (ch >= Channels) return;
        wave[ch, i] = unchecked((sbyte)(byte)value);
    }

    /// <summary>0~9 = 주파수 (짝: 낮은 8비트, 홀: 높은 4비트), 0x0A~0x0E = 볼륨, 0x0F = 켜기.</summary>
    private void WriteControl(int r, int value)
    {
        if (r < 10)
        {
            int ch = r >> 1;
            freq[ch] = (r & 1) == 0 ? (freq[ch] & 0xF00) | (value & 0xFF) : (freq[ch] & 0x0FF) | ((value & 0x0F) << 8);
            // 파형 위치는 그대로 둔다 (칩도 분주 카운터만 새로 센다)
        }
        else if (r < 15) volume[r - 10] = value & 0x0F;
        else enable = value & 0x1F;
    }

    // ------------------------------------------------------------ 소리

    /// <summary>출력 샘플 1개 (모노). 다섯 채널 모두 최대 볼륨 · 최대 파형이면 약 ±24000.</summary>
    public short Generate()
    {
        double sum = 0;
        for (int c = 0; c < Channels; c++)
        {
            int n = freq[c];
            if (n <= 8) continue;
            // 이 샘플 동안 파형 위치가 나아가는 양 (칸)
            double step = Clock / ((n + 1) * (double)Rate) * pitch[c];
            double p0 = phase[c];
            double p1 = p0 + step;
            phase[c] = p1 % 32.0;
            if ((enable & (1 << c)) == 0 || volume[c] == 0 || Mute[c]) continue;
            sum += Average(c, p0, p1) * volume[c];
        }
        double s = sum * (24000.0 / (Channels * 128.0 * 15.0));
        return (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s));
    }

    /// <summary>파형 c의 위치 p0~p1 구간 평균 (계단 모양 그대로).</summary>
    private double Average(int c, double p0, double p1)
    {
        double len = p1 - p0;
        if (len <= 0) return wave[c, (int)p0 & 31];
        if (len >= 32)
        {
            double all = 0;
            for (int i = 0; i < 32; i++) all += wave[c, i];
            return all / 32.0;
        }
        double acc = 0, p = p0;
        while (p < p1)
        {
            double edge = Math.Min(Math.Floor(p) + 1, p1);
            acc += wave[c, (int)Math.Floor(p) & 31] * (edge - p);
            p = edge;
        }
        return acc / len;
    }

    // ------------------------------------------------------------ 상태 (믹서 · 건반)

    /// <summary>채널 주파수 (Hz). 꺼졌거나 볼륨 0 · 분주 8 이하면 0.</summary>
    public double ChannelFrequency(int ch)
    {
        if ((enable & (1 << ch)) == 0 || volume[ch] == 0 || freq[ch] <= 8) return 0;
        return Clock / (32.0 * (freq[ch] + 1));
    }

    /// <summary>채널 음량 0~1.</summary>
    public float ChannelLevel(int ch) { return (enable & (1 << ch)) == 0 || freq[ch] <= 8 ? 0 : volume[ch] / 15f; }

    public sbyte WaveSample(int ch, int i) { return wave[ch % Channels, i & 31]; }

    // ------------------------------------------------------------ ISoundChip

    private readonly double[] pitch = { 1.0, 1.0, 1.0, 1.0, 1.0 };
    public string Name { get { return IsSccPlus ? "K052539 (SCC+)" : "K051649 (SCC)"; } }
    int ISoundChip.NativeRate { get { return Rate; } }
    public int ChannelCount { get { return Channels; } }
    public string ChannelLabel(int ch) { return "S" + (ch + 1); }
    void ISoundChip.Write(int port, int r, int value) { WritePort(port, r, value); }
    void ISoundChip.Generate(out int left, out int right) { left = right = Generate(); }
    public void SetMute(int ch, bool mute) { if (ch >= 0 && ch < Channels) Mute[ch] = mute; }
    public void SetPitch(int ch, double ratio) { if (ch >= 0 && ch < Channels) pitch[ch] = ratio > 0 ? ratio : 1.0; }
    public bool ChannelActive(int ch) { return false; }
}
