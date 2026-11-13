using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace AdlibComposer.Audio;

/// <summary>
/// 음악 파일 → 모노 float (−1~1). WAV(PCM 8 · 16 · 24 · 32bit, float 32 · 64, WAVE_FORMAT_EXTENSIBLE)는 직접 읽고,
/// 그 밖(MP3 · WMA · M4A …)은 Windows Media Foundation으로 푼다 (Windows에 들어 있는 코덱, 추가 설치 없음).
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
        else if (OperatingSystem.IsWindows()) mono = MediaFoundationDecoder.ReadMono(path, out sourceRate);
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

/// <summary>
/// Windows Media Foundation 소스 리더로 오디오 파일을 32bit float PCM으로 푼다 (MP3 등). COM 인터페이스는 쓰는 메서드까지 vtable 순서대로만 선언.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class MediaFoundationDecoder
{
    private const int MF_VERSION = 0x00020070;
    private const int FirstAudioStream = unchecked((int)0xFFFFFFFD);
    private const int AllStreams = unchecked((int)0xFFFFFFFE);
    private const int EndOfStream = 0x2;

    private static readonly Guid MF_MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MF_MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MF_MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MF_MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid MFMediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid MFAudioFormat_Float = new Guid("00000003-0000-0010-8000-00AA00389B71");

    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true)] private static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

    public static float[] ReadMono(string path, out int rate)
    {
        rate = 0;
        int hr;
        try { hr = MFStartup(MF_VERSION, 0); }
        catch (DllNotFoundException) { throw new NotSupportedException("이 시스템에는 Media Foundation이 없어 이 형식을 읽을 수 없습니다 (WAV만 가능)."); }
        Check(hr, "MFStartup");
        IMFSourceReader reader = null;
        IMFMediaType want = null, got = null;
        try
        {
            Check(MFCreateSourceReaderFromURL(Path.GetFullPath(path), IntPtr.Zero, out reader), "파일을 열 수 없습니다");
            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(FirstAudioStream, true);
            Check(MFCreateMediaType(out want), "MFCreateMediaType");
            want.SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio);
            want.SetGUID(MF_MT_SUBTYPE, MFAudioFormat_Float);
            Check(reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, want), "PCM으로 바꿀 수 없습니다");
            Check(reader.GetCurrentMediaType(FirstAudioStream, out got), "GetCurrentMediaType");
            got.GetUINT32(MF_MT_AUDIO_NUM_CHANNELS, out int channels);
            got.GetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, out int sr);
            if (channels <= 0 || sr <= 0) throw new InvalidDataException("오디오 형식을 알 수 없습니다.");
            rate = sr;
            var outList = new System.Collections.Generic.List<float>(sr * 60 * 4);
            var tmp = new float[0];
            while (true)
            {
                Check(reader.ReadSample(FirstAudioStream, 0, out _, out int flags, out _, out IMFSample sample), "ReadSample");
                if (sample != null)
                {
                    try
                    {
                        Check(sample.ConvertToContiguousBuffer(out IMFMediaBuffer buffer), "ConvertToContiguousBuffer");
                        try
                        {
                            Check(buffer.Lock(out IntPtr ptr, out _, out int len), "Lock");
                            int count = len / 4;
                            if (tmp.Length < count) tmp = new float[count];
                            Marshal.Copy(ptr, tmp, 0, count);
                            buffer.Unlock();
                            for (int i = 0; i + channels <= count; i += channels)
                            {
                                float sum = 0;
                                for (int c = 0; c < channels; c++) sum += tmp[i + c];
                                outList.Add(sum / channels);
                            }
                        }
                        finally { Marshal.ReleaseComObject(buffer); }
                    }
                    finally { Marshal.ReleaseComObject(sample); }
                }
                if ((flags & EndOfStream) != 0) break;
            }
            return outList.ToArray();
        }
        finally
        {
            if (got != null) Marshal.ReleaseComObject(got);
            if (want != null) Marshal.ReleaseComObject(want);
            if (reader != null) Marshal.ReleaseComObject(reader);
            MFShutdown();
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidDataException(what + " (Media Foundation 0x" + hr.ToString("X8") + ")");
    }

    // IMFAttributes 30개 메서드 자리 (쓰지 않는 것은 자리만)
    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaType
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int value);
        void GetUINT64(); void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(); void SetUINT64(); void SetDouble();
        [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] out bool selected);
        [PreserveSig] int SetStreamSelection(int stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        void GetNativeMediaType();
        [PreserveSig] int GetCurrentMediaType(int stream, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(int stream, IntPtr reserved, IMFMediaType type);
        void SetCurrentPosition();
        [PreserveSig] int ReadSample(int stream, int controlFlags, out int actualStream, out int streamFlags, out long timestamp, out IMFSample sample);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFSample
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
        void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems(); void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID(); void SetString();
        void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime(); void GetSampleDuration(); void SetSampleDuration();
        void GetBufferCount(); void GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
    }
}
