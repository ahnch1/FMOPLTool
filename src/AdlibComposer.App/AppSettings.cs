using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AdlibComposer.App;

/// <summary>
/// 프로그램 설정 (Preferences). 실행 파일과 같은 폴더의 FMOPLTool.cfg에 "키=값" 텍스트로 저장한다.
/// 경로는 실행 폴더 기준 상대 경로로 저장해서 폴더째 옮겨도 그대로 동작한다.
/// <code>
/// RolFolder=rol
/// ImsFolder=ims
/// SopFolder=sop
/// TrackerFolder=trk
/// MidiFolder=mid
/// BankFolder=resource
/// DefaultBank=resource\STANDARD.BNK
/// LoadDefaultBank=true
/// </code>
/// </summary>
internal sealed class AppSettings
{
    public const string FileName = "FMOPLTool.cfg";
    public const string DefaultSongFolder = "rol";
    public const string DefaultImsFolder = "ims";
    public const string DefaultSopFolder = "sop";
    public const string DefaultTrackerFolder = "trk";
    public const string DefaultMidiFolder = "mid";
    /// <summary>프로그램 리소스 폴더 (실행 폴더 아래): 기본 뱅크 STANDARD.BNK, MIDI FM용 GENMIDI.OP2, MT-32 ROM.</summary>
    public const string ResourceFolder = "resource";
    public const string DefaultBankFolder = ResourceFolder;
    public static readonly string DefaultBankFile = Path.Combine(ResourceFolder, "STANDARD.BNK");
    /// <summary>실행 폴더의 resource 폴더 (절대 경로).</summary>
    public static string ResourcePath { get { return Path.Combine(BaseDirectory, ResourceFolder); } }

    /// <summary>실행 파일 폴더 (dotnet 호스트로 실행해도 앱 폴더).</summary>
    public static string BaseDirectory { get { return AppContext.BaseDirectory; } }
    public static string FilePath { get { return Path.Combine(BaseDirectory, FileName); } }

    /// <summary>ROL 곡 폴더. 저장 값(상대/절대).</summary>
    public string SongFolder { get; set; } = DefaultSongFolder;
    /// <summary>IMS 곡 폴더 (ROL과 따로). 저장 값(상대/절대).</summary>
    public string ImsFolder { get; set; } = DefaultImsFolder;
    /// <summary>SOP 곡 폴더 (OPL3, Jukebox 전용). 저장 값(상대/절대).</summary>
    public string SopFolder { get; set; } = DefaultSopFolder;
    /// <summary>Scream Tracker 곡 폴더 (S3M / STM). 저장 값(상대/절대).</summary>
    public string TrackerFolder { get; set; } = DefaultTrackerFolder;
    /// <summary>MIDI 곡 폴더 (.mid / .wrk, Jukebox). 저장 값(상대/절대).</summary>
    public string MidiFolder { get; set; } = DefaultMidiFolder;
    /// <summary>MIDI를 Windows 신디사이저로 (false = OPL FM).</summary>
    public bool MidiWindowsSynth { get; set; }
    /// <summary>MIDI FM용 GM 뱅크 (GENMIDI.OP2 또는 BNK). 비우면 뱅크 폴더의 GENMIDI.OP2, 없으면 기본 뱅크를 이름으로 맞춤.</summary>
    public string MidiBank { get; set; } = "";
    /// <summary>MIDI 모듈 규격: true = Roland MT-32 (에뮬레이터 + ROM, 없으면 GM으로 대응), false = General MIDI.</summary>
    public bool MidiMt32 { get; set; }
    /// <summary>MT-32 ROM 폴더 (MT32_CONTROL.ROM + MT32_PCM.ROM 등, 사용자가 넣는다). 저장 값(상대/절대).</summary>
    public string Mt32RomFolder { get; set; } = DefaultMt32RomFolder;
    public const string DefaultMt32RomFolder = ResourceFolder;
    /// <summary>악기 뱅크(BNK) 폴더. 저장 값(상대/절대).</summary>
    public string BankFolder { get; set; } = DefaultBankFolder;
    /// <summary>프로그램 기본 뱅크 (시작할 때 불러오고, 새 곡 기본 악기를 찾는 뱅크).</summary>
    public string DefaultBank { get; set; } = DefaultBankFile;
    public bool LoadDefaultBank { get; set; } = true;
    /// <summary>OPL 에뮬레이터: true = Nuked OPL3 (사이클 정밀, 실제 칩에 가까움), false = 내장 간이 에뮬레이터. 프로그램 전체에 적용.</summary>
    public bool UseNukedOpl { get; set; }
    /// <summary>Jukebox 음색: true = 거칠게 (칩 그대로), false = 표준 (되먹임 조금 줄이고 12kHz 위를 살짝 깎음).</summary>
    public bool JukeboxRawTone { get; set; }
    /// <summary>Jukebox 스피커: true = 그 시절 (작은 PC 스피커 흉내).</summary>
    public bool JukeboxVintageSpeaker { get; set; }

    /// <summary>읽을 때 생긴 문제 (없으면 null). 화면에 알려 줄 때 쓴다.</summary>
    public string LoadWarning { get; private set; }

    // ---------------------------------------------------------------- 경로 변환

    public static string Resolve(string stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return BaseDirectory;
        string p = Environment.ExpandEnvironmentVariables(stored.Trim());
        return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(BaseDirectory, p));
    }

    /// <summary>실행 폴더 안이면 상대 경로로, 밖이면 절대 경로로 저장한다.</summary>
    public static string ToStored(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return "";
        string full = Path.GetFullPath(fullPath);
        string rel = Path.GetRelativePath(BaseDirectory, full);
        return rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel) ? full : rel;
    }

    public string SongFolderPath { get { return Resolve(SongFolder); } }
    public string ImsFolderPath { get { return Resolve(ImsFolder); } }
    public string SopFolderPath { get { return Resolve(SopFolder); } }
    public string TrackerFolderPath { get { return Resolve(TrackerFolder); } }
    public string MidiFolderPath { get { return Resolve(MidiFolder); } }
    public string MidiBankPath { get { return string.IsNullOrWhiteSpace(MidiBank) ? null : Resolve(MidiBank); } }
    public string Mt32RomFolderPath { get { return Resolve(Mt32RomFolder); } }

    /// <summary>곡 파일 형식(확장자)에 맞는 폴더: .ims → IMS 폴더, 나머지(.rol) → ROL 폴더.</summary>
    public string FolderForSong(string pathOrExtension)
    {
        string ext = string.IsNullOrEmpty(pathOrExtension) ? "" : Path.GetExtension(pathOrExtension);
        if (ext.Length == 0 && pathOrExtension != null && pathOrExtension.StartsWith(".")) ext = pathOrExtension;
        if (string.Equals(ext, ".sop", StringComparison.OrdinalIgnoreCase)) return SopFolderPath;
        return string.Equals(ext, ".ims", StringComparison.OrdinalIgnoreCase) ? ImsFolderPath : SongFolderPath;
    }
    public string BankFolderPath { get { return Resolve(BankFolder); } }
    public string DefaultBankPath { get { return string.IsNullOrWhiteSpace(DefaultBank) ? null : Resolve(DefaultBank); } }

    // ---------------------------------------------------------------- 읽기 / 쓰기

    /// <summary>설정 파일을 읽는다. 없으면 기본값으로 새로 만든다. 폴더도 없으면 만든다.</summary>
    public static AppSettings Load()
    {
        var s = new AppSettings();
        try
        {
            if (File.Exists(FilePath))
            {
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim(), value = line.Substring(eq + 1).Trim();
                    switch (key.ToLowerInvariant())
                    {
                        case "songfolder":                       // 예전 이름
                        case "rolfolder": s.SongFolder = value; break;
                        case "imsfolder": s.ImsFolder = value; break;
                        case "sopfolder": s.SopFolder = value; break;
                        case "trackerfolder": s.TrackerFolder = value; break;
                        case "midifolder": s.MidiFolder = value; break;
                        case "midisynth": s.MidiWindowsSynth = value.Equals("windows", StringComparison.OrdinalIgnoreCase); break;
                        case "midibank": s.MidiBank = value; break;
                        case "midimodule": s.MidiMt32 = value.Equals("mt32", StringComparison.OrdinalIgnoreCase); break;
                        case "mt32romfolder": s.Mt32RomFolder = value; break;
                        case "bankfolder": s.BankFolder = value; break;
                        case "defaultbank": s.DefaultBank = value; break;
                        case "loaddefaultbank": s.LoadDefaultBank = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                        case "oplemulator": s.UseNukedOpl = value.Equals("nuked", StringComparison.OrdinalIgnoreCase); break;
                        case "jukeboxtone": s.JukeboxRawTone = value.Equals("raw", StringComparison.OrdinalIgnoreCase); break;
                        case "jukeboxspeaker": s.JukeboxVintageSpeaker = value.Equals("vintage", StringComparison.OrdinalIgnoreCase); break;
                    }
                }
            }
            else s.Save();
        }
        catch (Exception ex) { s.LoadWarning = "설정 파일을 읽을 수 없어 기본값을 씁니다: " + ex.Message; }
        s.MigrateOldDefaults();
        s.EnsureFolders();
        return s;
    }

    /// <summary>
    /// 예전 기본값(bnk 폴더, bnk\STANDARD.BNK, mt32rom)을 그대로 쓰던 설정은 resource 폴더로 옮긴다 (사용자가 직접 바꾼 경로는 그대로).
    /// </summary>
    private void MigrateOldDefaults()
    {
        bool Same(string a, string b) { return string.Equals((a ?? "").Replace('/', '\\').Trim().TrimEnd('\\'), b, StringComparison.OrdinalIgnoreCase); }
        bool changed = false;
        if (Same(BankFolder, "bnk")) { BankFolder = DefaultBankFolder; changed = true; }
        if (Same(DefaultBank, "bnk\\STANDARD.BNK")) { DefaultBank = DefaultBankFile; changed = true; }
        if (Same(Mt32RomFolder, "mt32rom")) { Mt32RomFolder = DefaultMt32RomFolder; changed = true; }
        if (!changed) return;
        try { Save(); } catch (Exception) { }
    }

    public void Save()
    {
        var sb = new StringBuilder();
        sb.AppendLine("; FMOPLTool / AdLib Composer settings");
        sb.AppendLine("; Paths are relative to this file's folder (the program folder) or absolute.");
        sb.AppendLine("; RolFolder = ROL songs, ImsFolder = IMS songs, SopFolder = SOP songs (Jukebox), TrackerFolder = S3M/STM, BankFolder = BNK banks, DefaultBank = bank loaded at startup");
        sb.AppendLine("RolFolder=" + SongFolder);
        sb.AppendLine("ImsFolder=" + ImsFolder);
        sb.AppendLine("SopFolder=" + SopFolder);
        sb.AppendLine("TrackerFolder=" + TrackerFolder);
        sb.AppendLine("MidiFolder=" + MidiFolder);
        sb.AppendLine("BankFolder=" + BankFolder);
        sb.AppendLine("DefaultBank=" + DefaultBank);
        sb.AppendLine("LoadDefaultBank=" + (LoadDefaultBank ? "true" : "false"));
        sb.AppendLine("; OplEmulator = builtin | nuked (Nuked OPL3, cycle-accurate)");
        sb.AppendLine("OplEmulator=" + (UseNukedOpl ? "nuked" : "builtin"));
        sb.AppendLine("; MidiSynth = fm | windows, MidiBank = GENMIDI.OP2 or BNK for FM (empty = GENMIDI.OP2 in BankFolder or resource, else default bank by name)");
        sb.AppendLine("MidiSynth=" + (MidiWindowsSynth ? "windows" : "fm"));
        sb.AppendLine("MidiBank=" + MidiBank);
        sb.AppendLine("; MidiModule = gm | mt32 (Roland MT-32 via Munt emulator; needs ROM files in Mt32RomFolder)");
        sb.AppendLine("MidiModule=" + (MidiMt32 ? "mt32" : "gm"));
        sb.AppendLine("Mt32RomFolder=" + Mt32RomFolder);
        sb.AppendLine("; Jukebox: JukeboxTone = standard | raw, JukeboxSpeaker = default | vintage");
        sb.AppendLine("JukeboxTone=" + (JukeboxRawTone ? "raw" : "standard"));
        sb.AppendLine("JukeboxSpeaker=" + (JukeboxVintageSpeaker ? "vintage" : "default"));
        File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>곡/뱅크 폴더가 없으면 만든다 (실패해도 프로그램은 계속).</summary>
    public void EnsureFolders()
    {
        foreach (var dir in new[] { SongFolderPath, ImsFolderPath, SopFolderPath, TrackerFolderPath, MidiFolderPath, BankFolderPath, Mt32RomFolderPath })
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception) { }
        }
    }

    public AppSettings Clone() { return (AppSettings)MemberwiseClone(); }

    /// <summary>폴더가 있으면 그 폴더, 없으면 null (파일 대화상자 InitialDirectory용).</summary>
    public static string ExistingOrNull(string dir)
    {
        return !string.IsNullOrEmpty(dir) && Directory.Exists(dir) ? dir : null;
    }
}
