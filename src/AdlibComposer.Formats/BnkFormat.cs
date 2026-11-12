using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AdlibComposer.Core;

namespace AdlibComposer.Formats;

/// <summary>AdLib 악기 뱅크 (.BNK). 헤더 28B + 이름 목록(12B씩) + 악기 데이터(30B씩).</summary>
public sealed class BnkFormat : IBankFormat
{
    public const int HeaderSize = 28;
    public const int NameEntrySize = 12;
    public const int DataSize = 30;

    public string Id { get { return "bnk"; } }
    public string DisplayName { get { return "AdLib instrument bank"; } }
    public string[] Extensions { get { return new[] { ".bnk" }; } }
    public bool CanWrite { get { return true; } }

    public InstrumentBank Read(Stream stream)
    {
        var r = new BinaryReader(stream, Encoding.ASCII, true);
        int verMajor = r.ReadByte();
        int verMinor = r.ReadByte();
        string sig = BinaryUtil.ReadFixedString(r, 6);
        if (!string.Equals(sig, "ADLIB-", StringComparison.Ordinal))
            throw new InvalidDataException("BNK 시그니처(ADLIB-)가 없습니다.");
        int numUsed = r.ReadUInt16();
        int numInstruments = r.ReadUInt16();
        uint offsetName = r.ReadUInt32();
        uint offsetData = r.ReadUInt32();
        r.ReadBytes(8);

        var bank = new InstrumentBank { VersionMajor = verMajor, VersionMinor = verMinor };
        bool hmi = bank.IsHmiVariant;

        stream.Position = offsetName;
        var entries = new List<Tuple<int, int, string>>();
        for (int i = 0; i < numInstruments; i++)
        {
            int index = r.ReadUInt16();
            int flags = r.ReadByte();
            string name = BinaryUtil.ReadFixedString(r, 9);
            entries.Add(Tuple.Create(index, flags, name));
        }

        foreach (var e in entries)
        {
            long pos = (long)offsetData + (long)e.Item1 * DataSize;
            if (pos + DataSize > stream.Length) continue;
            stream.Position = pos;
            var ins = ReadInstrument(r);
            ins.Name = e.Item3;
            ins.IsUsed = hmi || e.Item2 != 0;
            bank.Instruments.Add(ins);
        }
        return bank;
    }

    private static AdlibInstrument ReadInstrument(BinaryReader r)
    {
        var ins = new AdlibInstrument();
        ins.Percussive = r.ReadByte() != 0;
        ins.VoiceNum = r.ReadByte();
        ReadOperator(r, ins.Modulator);
        ReadOperator(r, ins.Carrier);
        ins.Modulator.WaveSelect = r.ReadByte();
        ins.Carrier.WaveSelect = r.ReadByte();
        return ins;
    }

    private static void ReadOperator(BinaryReader r, OplOperator op)
    {
        op.Ksl = r.ReadByte();
        op.Multiple = r.ReadByte();
        op.Feedback = r.ReadByte();
        op.Attack = r.ReadByte();
        op.Sustain = r.ReadByte();
        op.Eg = r.ReadByte();
        op.Decay = r.ReadByte();
        op.ReleaseRate = r.ReadByte();
        op.TotalLevel = r.ReadByte();
        op.Am = r.ReadByte();
        op.Vib = r.ReadByte();
        op.Ksr = r.ReadByte();
        op.Con = r.ReadByte();
    }

    public void Write(InstrumentBank bank, Stream stream)
    {
        var list = new List<AdlibInstrument>(bank.Instruments);
        list.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        if (list.Count > 65535) throw new InvalidDataException("악기가 너무 많습니다.");

        var w = new BinaryWriter(stream, Encoding.ASCII, true);
        int used = 0;
        foreach (var i in list) if (i.IsUsed) used++;

        w.Write((byte)bank.VersionMajor);
        w.Write((byte)bank.VersionMinor);
        w.Write(Encoding.ASCII.GetBytes("ADLIB-"));
        w.Write((ushort)used);
        w.Write((ushort)list.Count);
        w.Write((uint)HeaderSize);
        w.Write((uint)(HeaderSize + list.Count * NameEntrySize));
        w.Write(new byte[8]);

        for (int i = 0; i < list.Count; i++)
        {
            w.Write((ushort)i);
            w.Write((byte)(list[i].IsUsed ? 1 : 0));
            BinaryUtil.WriteFixedString(w, AdlibInstrument.NormalizeName(list[i].Name), 9);
        }
        foreach (var ins in list)
        {
            w.Write((byte)(ins.Percussive ? 1 : 0));
            w.Write(BinaryUtil.ToByte(ins.VoiceNum));
            WriteOperator(w, ins.Modulator);
            WriteOperator(w, ins.Carrier);
            w.Write(BinaryUtil.ToByte(ins.Modulator.WaveSelect));
            w.Write(BinaryUtil.ToByte(ins.Carrier.WaveSelect));
        }
        w.Flush();
    }

    private static void WriteOperator(BinaryWriter w, OplOperator op)
    {
        w.Write(BinaryUtil.ToByte(op.Ksl));
        w.Write(BinaryUtil.ToByte(op.Multiple));
        w.Write(BinaryUtil.ToByte(op.Feedback));
        w.Write(BinaryUtil.ToByte(op.Attack));
        w.Write(BinaryUtil.ToByte(op.Sustain));
        w.Write(BinaryUtil.ToByte(op.Eg));
        w.Write(BinaryUtil.ToByte(op.Decay));
        w.Write(BinaryUtil.ToByte(op.ReleaseRate));
        w.Write(BinaryUtil.ToByte(op.TotalLevel));
        w.Write(BinaryUtil.ToByte(op.Am));
        w.Write(BinaryUtil.ToByte(op.Vib));
        w.Write(BinaryUtil.ToByte(op.Ksr));
        w.Write(BinaryUtil.ToByte(op.Con));
    }
}
