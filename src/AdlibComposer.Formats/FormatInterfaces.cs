using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

public interface IFileFormat
{
    string Id { get; }
    string DisplayName { get; }
    /// <summary>점 포함 소문자 확장자 (예: ".rol")</summary>
    string[] Extensions { get; }
}

/// <summary>곡 포맷 (ROL, 앞으로 추가할 IMS/SOP 등). 새 포맷은 이 인터페이스를 구현해 레지스트리에 등록한다.</summary>
public interface ISongFormat : IFileFormat
{
    bool CanRead { get; }
    bool CanWrite { get; }
    Song Read(Stream stream);
    void Write(Song song, Stream stream);
}

/// <summary>악기 뱅크 포맷 (BNK 등).</summary>
public interface IBankFormat : IFileFormat
{
    bool CanWrite { get; }
    InstrumentBank Read(Stream stream);
    void Write(InstrumentBank bank, Stream stream);
}

/// <summary>악기 1개 포맷 (INS 등).</summary>
public interface IInstrumentFormat : IFileFormat
{
    AdlibInstrument Read(Stream stream, string nameHint);
    void Write(AdlibInstrument instrument, Stream stream);
}

public sealed class FormatRegistry
{
    public List<ISongFormat> Songs { get; } = new List<ISongFormat>();
    public List<IBankFormat> Banks { get; } = new List<IBankFormat>();
    public List<IInstrumentFormat> Instruments { get; } = new List<IInstrumentFormat>();

    public static FormatRegistry CreateDefault()
    {
        var r = new FormatRegistry();
        r.Songs.Add(new RolFormat());
        r.Songs.Add(new ImsFormat());
        r.Banks.Add(new BnkFormat());
        r.Instruments.Add(new InsFormat());
        // SOP(OPL3)는 곡 안에 악기가 있어 ISongFormat이 아니다: SopFormat(SopSong) + SopConvert(Song SOP 모드 ↔ SopSong)로 Workspace가 따로 다룬다.
        return r;
    }

    public ISongFormat FindSong(string path) { return Find(Songs, path); }
    public IBankFormat FindBank(string path) { return Find(Banks, path); }
    public IInstrumentFormat FindInstrument(string path) { return Find(Instruments, path); }

    private static T Find<T>(IEnumerable<T> list, string path) where T : class, IFileFormat
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        foreach (var f in list)
            foreach (var e in f.Extensions)
                if (e == ext) return f;
        return null;
    }

    /// <summary>OpenFileDialog/SaveFileDialog용 필터 문자열.</summary>
    public static string BuildFilter(IEnumerable<IFileFormat> formats)
    {
        var sb = new StringBuilder();
        var all = new List<string>();
        foreach (var f in formats)
        {
            var pats = new List<string>();
            foreach (var e in f.Extensions) pats.Add("*" + e);
            string p = string.Join(";", pats.ToArray());
            all.AddRange(pats);
            if (sb.Length > 0) sb.Append('|');
            sb.Append(f.DisplayName).Append(" (").Append(p).Append(")|").Append(p);
        }
        if (sb.Length > 0) sb.Append('|');
        sb.Append("All files (*.*)|*.*");
        return sb.ToString();
    }
}

internal static class BinaryUtil
{
    private static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    public static string ReadFixedString(BinaryReader r, int length)
    {
        byte[] b = r.ReadBytes(length);
        if (b.Length < length) throw new EndOfStreamException();
        int n = Array.IndexOf(b, (byte)0);
        if (n < 0) n = length;
        return Latin1.GetString(b, 0, n);
    }

    /// <summary>고정 길이 NULL 패딩 문자열. 마지막 바이트는 항상 0으로 남긴다.</summary>
    public static void WriteFixedString(BinaryWriter w, string s, int length)
    {
        byte[] buf = new byte[length];
        byte[] src = Latin1.GetBytes(s ?? "");
        Array.Copy(src, buf, Math.Min(src.Length, length - 1));
        w.Write(buf);
    }

    public static byte ToByte(int v) { return (byte)Calc.Clamp(v, 0, 255); }
    public static ushort ToUInt16(int v) { return (ushort)Calc.Clamp(v, 0, 65535); }
}
