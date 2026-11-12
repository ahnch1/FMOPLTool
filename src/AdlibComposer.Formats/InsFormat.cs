using System;
using System.IO;
using System.Text;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

/// <summary>AdLib 악기 1개 파일 (.INS). 필드당 UINT16LE. 80바이트(표준) / 54바이트(초기) / 캐리어 없는 변형.</summary>
public sealed class InsFormat : IInstrumentFormat
{
    public const int FullSize = 80;
    public const int EarlySize = 54;
    private const int OperatorSize = 26;

    public string Id { get { return "ins"; } }
    public string DisplayName { get { return "AdLib instrument"; } }
    public string[] Extensions { get { return new[] { ".ins" }; } }

    public AdlibInstrument Read(Stream stream, string nameHint)
    {
        long length = stream.Length - stream.Position;
        var r = new BinaryReader(stream, Encoding.ASCII, true);
        var ins = new AdlibInstrument { Name = AdlibInstrument.NormalizeName(nameHint ?? "INS") };
        if (length < 2 + OperatorSize) throw new InvalidDataException("INS 파일이 너무 짧습니다.");

        ins.InsUnknown = r.ReadUInt16();
        ReadOperator(r, ins.Modulator);
        long remain = length - 2 - OperatorSize;
        if (remain >= OperatorSize)
        {
            ReadOperator(r, ins.Carrier);
            remain -= OperatorSize;
        }
        if (remain >= 4)
        {
            ins.Modulator.WaveSelect = r.ReadUInt16() & 3;
            ins.Carrier.WaveSelect = r.ReadUInt16() & 3;
            remain -= 4;
        }
        if (remain >= 22)
        {
            ins.InsTail = r.ReadBytes(22);
        }
        return ins;
    }

    private static void ReadOperator(BinaryReader r, OplOperator op)
    {
        op.Ksl = r.ReadUInt16();
        op.Multiple = r.ReadUInt16();
        op.Feedback = r.ReadUInt16();
        op.Attack = r.ReadUInt16();
        op.Sustain = r.ReadUInt16();
        op.Eg = r.ReadUInt16();
        op.Decay = r.ReadUInt16();
        op.ReleaseRate = r.ReadUInt16();
        op.TotalLevel = r.ReadUInt16();
        op.Am = r.ReadUInt16();
        op.Vib = r.ReadUInt16();
        op.Ksr = r.ReadUInt16();
        op.Con = r.ReadUInt16();
    }

    public void Write(AdlibInstrument ins, Stream stream)
    {
        var w = new BinaryWriter(stream, Encoding.ASCII, true);
        w.Write((ushort)ins.InsUnknown);
        WriteOperator(w, ins.Modulator);
        WriteOperator(w, ins.Carrier);
        w.Write(BinaryUtil.ToUInt16(ins.Modulator.WaveSelect));
        w.Write(BinaryUtil.ToUInt16(ins.Carrier.WaveSelect));
        byte[] tail = new byte[22];
        if (ins.InsTail != null) Array.Copy(ins.InsTail, tail, Math.Min(22, ins.InsTail.Length));
        w.Write(tail);
        w.Flush();
    }

    private static void WriteOperator(BinaryWriter w, OplOperator op)
    {
        w.Write(BinaryUtil.ToUInt16(op.Ksl));
        w.Write(BinaryUtil.ToUInt16(op.Multiple));
        w.Write(BinaryUtil.ToUInt16(op.Feedback));
        w.Write(BinaryUtil.ToUInt16(op.Attack));
        w.Write(BinaryUtil.ToUInt16(op.Sustain));
        w.Write(BinaryUtil.ToUInt16(op.Eg));
        w.Write(BinaryUtil.ToUInt16(op.Decay));
        w.Write(BinaryUtil.ToUInt16(op.ReleaseRate));
        w.Write(BinaryUtil.ToUInt16(op.TotalLevel));
        w.Write(BinaryUtil.ToUInt16(op.Am));
        w.Write(BinaryUtil.ToUInt16(op.Vib));
        w.Write(BinaryUtil.ToUInt16(op.Ksr));
        w.Write(BinaryUtil.ToUInt16(op.Con));
    }
}
