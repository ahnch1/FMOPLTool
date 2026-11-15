using System;
using System.IO;
using System.Runtime.InteropServices;

namespace AdlibComposer.Audio.Windows;

/// <summary>
/// Windows Media Foundation 소스 리더로 오디오 파일을 32bit float PCM으로 푼다 (MP3 등). COM 인터페이스는 쓰는 메서드까지 vtable 순서대로만 선언.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class MediaFoundationDecoder
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
