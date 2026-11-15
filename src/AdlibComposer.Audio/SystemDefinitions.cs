using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AdlibComposer.Audio;

/// <summary>
/// 시스템 정의 (Hoot 방식): 기계마다 CPU · 칩 · 클럭 · 포트 / 메모리 배치를 적은 것. KSS 머신 · S98 기본 장치 · IMF 속도가 이것을 본다.
/// <para>프로그램 안의 기본값이 있고, <c>resource/systems.json</c>이 있으면 같은 id를 그 값으로 덮어쓴다 (새 id는 더해진다).
/// 숫자는 10진 또는 "0x7C"처럼 16진 문자열.</para>
/// <code>
/// { "systems": [ { "id": "msx", "name": "MSX", "cpu": "Z80", "cpuClock": 3579545, "frameRate": 60,
///   "chips": [ { "chip": "AY-3-8910", "clock": 1789772.5, "ports": { "addr": "0xA0", "data": "0xA1", "read": "0xA2" } },
///              { "chip": "SCC", "clock": 3579545, "memory": "0x9800" } ] } ] }
/// </code>
/// </summary>
public sealed class SystemDefinition
{
    public string Id = "";
    public string Name = "";
    public string Cpu = "";
    public double CpuClock;
    public double FrameRate = 60;
    /// <summary>IMF처럼 틱 속도만 있는 형식 (Hz).</summary>
    public double TickRate;
    public readonly List<ChipDefinition> Chips = new List<ChipDefinition>();

    public ChipDefinition Chip(string chip) { return Chips.FirstOrDefault(c => string.Equals(c.Chip, chip, StringComparison.OrdinalIgnoreCase)); }

    public override string ToString() { return Name + " (" + string.Join(" + ", Chips.Select(c => c.Chip)) + ")"; }
}

public sealed class ChipDefinition
{
    public string Chip = "";
    public double Clock;
    /// <summary>I/O 포트 이름 → 번호 (addr · data · read · stereo · bank …).</summary>
    public readonly Dictionary<string, int> Ports = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    /// <summary>메모리에 붙은 칩의 시작 주소 (없으면 −1).</summary>
    public int Memory = -1;

    public int Port(string name, int fallback) { return Ports.TryGetValue(name, out int v) ? v : fallback; }
}

public static class SystemDefinitions
{
    private static Dictionary<string, SystemDefinition> all;
    private static readonly object gate = new object();

    /// <summary>덮어쓸 파일 (없으면 기본값만). 앱이 시작할 때 resource 폴더의 것을 넣는다.</summary>
    public static string OverridePath { get; set; }

    public const string BuiltinJson = @"{ ""systems"": [
  { ""id"": ""msx"", ""name"": ""MSX"", ""cpu"": ""Z80"", ""cpuClock"": 3579545, ""frameRate"": 60, ""chips"": [
    { ""chip"": ""AY-3-8910"", ""clock"": 1789772.5, ""ports"": { ""addr"": ""0xA0"", ""data"": ""0xA1"", ""read"": ""0xA2"" } },
    { ""chip"": ""SCC"", ""clock"": 3579545, ""memory"": ""0x9800"" },
    { ""chip"": ""SCC+"", ""clock"": 3579545, ""memory"": ""0xB800"" },
    { ""chip"": ""YM2413"", ""clock"": 3579545, ""ports"": { ""addr"": ""0x7C"", ""data"": ""0x7D"" } },
    { ""chip"": ""Y8950"", ""clock"": 3579545, ""ports"": { ""addr"": ""0xC0"", ""data"": ""0xC1"" } },
    { ""chip"": ""Mapper"", ""ports"": { ""bank"": ""0xFE"" } } ] },
  { ""id"": ""sms"", ""name"": ""Sega Master System / Game Gear"", ""cpu"": ""Z80"", ""cpuClock"": 3579545, ""frameRate"": 60, ""chips"": [
    { ""chip"": ""SN76489"", ""clock"": 3579545, ""ports"": { ""data"": ""0x7F"", ""data2"": ""0x7E"", ""stereo"": ""0x06"" } },
    { ""chip"": ""YM2413"", ""clock"": 3579545, ""ports"": { ""addr"": ""0xF0"", ""data"": ""0xF1"" } },
    { ""chip"": ""Mapper"", ""ports"": { ""bank"": ""0xFE"" }, ""memory"": ""0xFFFF"" } ] },
  { ""id"": ""pc88"", ""name"": ""NEC PC-8801 (OPN)"", ""cpu"": ""Z80"", ""cpuClock"": 3993600, ""frameRate"": 60, ""chips"": [
    { ""chip"": ""YM2203"", ""clock"": 3993600, ""ports"": { ""addr"": ""0x44"", ""data"": ""0x45"" } } ] },
  { ""id"": ""pc98-86"", ""name"": ""NEC PC-9801-86 (OPNA)"", ""cpu"": ""x86"", ""cpuClock"": 8000000, ""frameRate"": 56.4, ""chips"": [
    { ""chip"": ""YM2608"", ""clock"": 7987200, ""ports"": { ""addr"": ""0x188"", ""data"": ""0x18A"", ""addr2"": ""0x18C"", ""data2"": ""0x18E"" } } ] },
  { ""id"": ""x68000"", ""name"": ""Sharp X68000"", ""cpu"": ""68000"", ""cpuClock"": 10000000, ""frameRate"": 55.46, ""chips"": [
    { ""chip"": ""YM2151"", ""clock"": 4000000 },
    { ""chip"": ""OKIM6295"", ""clock"": 8000000 } ] },
  { ""id"": ""adlib"", ""name"": ""AdLib (OPL2)"", ""cpu"": ""x86"", ""tickRate"": 560, ""chips"": [
    { ""chip"": ""YM3812"", ""clock"": 3579545, ""ports"": { ""addr"": ""0x388"", ""data"": ""0x389"" } } ] },
  { ""id"": ""wolf3d"", ""name"": ""Wolfenstein 3D (IMF 700Hz)"", ""cpu"": ""x86"", ""tickRate"": 700, ""chips"": [
    { ""chip"": ""YM3812"", ""clock"": 3579545 } ] },
  { ""id"": ""sbpro2"", ""name"": ""Sound Blaster Pro 2 (OPL3)"", ""cpu"": ""x86"", ""chips"": [
    { ""chip"": ""YMF262"", ""clock"": 14318180, ""ports"": { ""addr"": ""0x220"", ""data"": ""0x221"", ""addr2"": ""0x222"", ""data2"": ""0x223"" } } ] },
  { ""id"": ""nes"", ""name"": ""Nintendo Famicom / NES (NTSC)"", ""cpu"": ""6502"", ""cpuClock"": 1789773, ""frameRate"": 60.0988, ""chips"": [
    { ""chip"": ""2A03"", ""clock"": 1789773, ""memory"": ""0x4000"" } ] },
  { ""id"": ""pce"", ""name"": ""NEC PC Engine / TurboGrafx-16"", ""cpu"": ""HuC6280"", ""cpuClock"": 7159090, ""frameRate"": 59.94, ""chips"": [
    { ""chip"": ""HuC6280"", ""clock"": 3579545, ""memory"": ""0x1FE800"" },
    { ""chip"": ""Timer"", ""memory"": ""0x1FEC00"" },
    { ""chip"": ""VDC"", ""memory"": ""0x1FE000"" } ] }
] }";

    public static IReadOnlyCollection<SystemDefinition> All { get { lock (gate) { Ensure(); return all.Values.ToList(); } } }

    /// <summary>id로 찾기 (없으면 null).</summary>
    public static SystemDefinition Get(string id)
    {
        lock (gate)
        {
            Ensure();
            return all.TryGetValue(id ?? "", out var s) ? s : null;
        }
    }

    /// <summary>다시 읽기 (설정 파일이 바뀌었을 때).</summary>
    public static void Reload() { lock (gate) all = null; }

    private static void Ensure()
    {
        if (all != null) return;
        var d = new Dictionary<string, SystemDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Parse(BuiltinJson)) d[s.Id] = s;
        string p = OverridePath;
        if (!string.IsNullOrEmpty(p) && File.Exists(p))
        {
            try { foreach (var s in Parse(File.ReadAllText(p))) d[s.Id] = s; }
            catch (Exception) { /* 잘못된 파일은 무시하고 기본값 */ }
        }
        all = d;
    }

    /// <summary>JSON 문서 → 시스템 목록. 형식이 틀리면 예외.</summary>
    public static List<SystemDefinition> Parse(string json)
    {
        var list = new List<SystemDefinition>();
        using (var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
        {
            if (!doc.RootElement.TryGetProperty("systems", out var arr) || arr.ValueKind != JsonValueKind.Array) throw new InvalidDataException("systems 배열이 없습니다.");
            foreach (var e in arr.EnumerateArray())
            {
                var s = new SystemDefinition
                {
                    Id = Str(e, "id"), Name = Str(e, "name"), Cpu = Str(e, "cpu"),
                    CpuClock = Num(e, "cpuClock", 0), FrameRate = Num(e, "frameRate", 60), TickRate = Num(e, "tickRate", 0),
                };
                if (s.Id.Length == 0) throw new InvalidDataException("id가 없는 시스템이 있습니다.");
                if (e.TryGetProperty("chips", out var chips) && chips.ValueKind == JsonValueKind.Array)
                    foreach (var c in chips.EnumerateArray())
                    {
                        var cd = new ChipDefinition { Chip = Str(c, "chip"), Clock = Num(c, "clock", 0), Memory = (int)Num(c, "memory", -1) };
                        if (c.TryGetProperty("ports", out var ports) && ports.ValueKind == JsonValueKind.Object)
                            foreach (var kv in ports.EnumerateObject()) cd.Ports[kv.Name] = (int)ToNumber(kv.Value);
                        s.Chips.Add(cd);
                    }
                list.Add(s);
            }
        }
        return list;
    }

    private static string Str(JsonElement e, string name) { return e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : ""; }
    private static double Num(JsonElement e, string name, double fallback) { return e.TryGetProperty(name, out var v) ? ToNumber(v) : fallback; }

    private static double ToNumber(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        if (v.ValueKind == JsonValueKind.String)
        {
            string s = v.GetString().Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h)) return h;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
        }
        throw new InvalidDataException("숫자가 아닙니다: " + v);
    }
}
