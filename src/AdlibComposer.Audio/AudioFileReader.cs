using System;
using System.IO;
using System.Text;

namespace AdlibComposer.Audio;

/// <summary>
/// 음악 파일 → 모노 float (−1~1). WAV(PCM 8 · 16 · 24 · 32bit, float 32 · 64, WAVE_FORMAT_EXTENSIBLE)는 직접 읽고,
/// 그 밖(MP3 · WMA · M4A …)은 플랫폼 디코더(<see cref="AudioPlatform.CompressedDecoder"/>, Windows = Media Foundation)로 푼다.
/// </summary>
public static class AudioFileReader
{
    public static readonly string[] Extensions = { ".wav", ".mp3", ".wma", ".m4a", ".aac", ".flac" };

    public static bool IsAudio(string path)
    {
        string ext = Path.GetExtension(path ?? "").ToLowerInvariant();
        return Array.IndexOf(Extensions, ext) >= 0;
    }

    /// <summary>파일을 읽어 모노로 섞고 <paramref name="targetRate"/>로 바꾼다. 원래 주기는 <paramref name="sourceRate"/>.</summary>
    /// <param name="forceMediaFoundation">WAV도 Media Foundation으로 (시험용).</param>
    public static float[] ReadMono(string path, int targetRate, out int sourceRate, bool forceMediaFoundation = false)
    {
        float[] mono;
        if (!forceMediaFoundation && TryReadWav(path, out mono, out sourceRate)) { }
        else if (AudioPlatform.CompressedDecoder != null) mono = AudioPlatform.CompressedDecoder(path, out sourceRate);
        else throw new NotSupportedException("이 시스템에서는 WAV만 읽을 수 있습니다.");
        return Resample(mono, sourceRate, targetRate);
    }

    // ------------------------------------------------------------ WAV

    private static bool TryReadWav(string path, out float[] mono, out int rate)
    {
        mono = null; rate = 0;
        byte[] d = File.ReadAllBytes(path);
        if (d.Length < 44 || Encoding.ASCII.GetString(d, 0, 4) != "RIFF" || Encoding.ASCII.GetString(d, 8, 4) != "WAVE") return false;
        int fmt = 0, channels = 0, bits = 0, dataAt = -1, dataLen = 0;
        int p = 12;
        while (p + 8 <= d.Length)
        {
            string id = Encoding.ASCII.GetString(d, p, 4);
            int len = BitConverter.ToInt32(d, p + 4);
            if (len < 0) break;
            if (id == "fmt " && p + 8 + 16 <= d.Length)
            {
                fmt = BitConverter.ToUInt16(d, p + 8);
                channels = BitConverter.ToUInt16(d, p + 10);
                rate = BitConverter.ToInt32(d, p + 12);
                bits = BitConverter.ToUInt16(d, p + 22);
                if (fmt == 0xFFFE && len >= 40) fmt = BitConverter.ToUInt16(d, p + 8 + 24);   // EXTENSIBLE: 서브포맷 GUID 앞 2바이트
            }
            else if (id == "data") { dataAt = p + 8; dataLen = Math.Min(len, d.Length - dataAt); break; }
            p += 8 + len + (len & 1);
        }
        if (dataAt < 0 || channels <= 0 || rate <= 0) return false;
        if (!((fmt == 1 && (bits == 8 || bits == 16 || bits == 24 || bits == 32)) || (fmt == 3 && (bits == 32 || bits == 64)))) return false;
        int bytes = bits / 8, frame = bytes * channels;
        int frames = dataLen / frame;
        mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            double sum = 0;
            int at = dataAt + i * frame;
            for (int c = 0; c < channels; c++, at += bytes)
            {
                double v;
                if (fmt == 3) v = bits == 32 ? BitConverter.ToSingle(d, at) : BitConverter.ToDouble(d, at);
                else if (bits == 8) v = (d[at] - 128) / 128.0;
                else if (bits == 16) v = BitConverter.ToInt16(d, at) / 32768.0;
                else if (bits == 24) v = ((d[at] | (d[at + 1] << 8) | ((sbyte)d[at + 2] << 16))) / 8388608.0;
                else v = BitConverter.ToInt32(d, at) / 2147483648.0;
                sum += v;
            }
            mono[i] = (float)(sum / channels);
        }
        return true;
    }

    // ------------------------------------------------------------ 주기 바꾸기

    /// <summary>
    /// 주기 바꾸기: 줄일 때는 먼저 새 나이퀴스트의 90%에서 2차 로우패스를 두 번 걸어 에일리어싱을 줄이고, 선형 보간.
    /// (채보용 분석이라 이 정도면 충분하다.)
    /// </summary>
    public static float[] Resample(float[] src, int from, int to)
    {
        if (from == to || src.Length == 0) return src;
        float[] x = src;
        if (to < from)
        {
            x = (float[])src.Clone();
            double fc = 0.45 * to;
            for (int pass = 0; pass < 2; pass++) LowPass(x, fc, from);
        }
        long n = (long)Math.Floor((double)x.Length * to / from);
        var y = new float[n];
        double step = (double)from / to;
        for (long i = 0; i < n; i++)
        {
            double pos = i * step;
            int k = (int)pos;
            double f = pos - k;
            float a = x[k], b = k + 1 < x.Length ? x[k + 1] : a;
            y[i] = (float)(a + (b - a) * f);
        }
        return y;
    }

    private static void LowPass(float[] x, double fc, double rate)
    {
        // RBJ 로우패스 Q = 0.707
        double w = 2 * Math.PI * fc / rate, cs = Math.Cos(w), al = Math.Sin(w) / (2 * 0.7071);
        double a0 = 1 + al, b0 = (1 - cs) / 2 / a0, b1 = (1 - cs) / a0, b2 = b0, a1 = -2 * cs / a0, a2 = (1 - al) / a0;
        double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
        for (int i = 0; i < x.Length; i++)
        {
            double v = x[i];
            double y = b0 * v + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2;
            x2 = x1; x1 = v; y2 = y1; y1 = y;
            x[i] = (float)y;
        }
    }
}
