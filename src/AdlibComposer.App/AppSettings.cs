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
/// BankFolder=bnk
/// DefaultBank=bnk\STANDARD.BNK
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
    public const string DefaultBankFolder = "bnk";
    public static readonly string DefaultBankFile = Path.Combine("bnk", "STANDARD.BNK");

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
    /// <summary>악기 뱅크(BNK) 폴더. 저장 값(상대/절대).</summary>
    public string BankFolder { get; set; } = DefaultBankFolder;
    /// <summary>프로그램 기본 뱅크 (시작할 때 불러오고, 새 곡 기본 악기를 찾는 뱅크).</summary>
    public string DefaultBank { get; set; } = DefaultBankFile;
    public bool LoadDefaultBank { get; set; } = true;

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

    /// <summary>곡 파일 형식(확장자)에 맞는 폴더: .ims → IMS 폴더, 나머지(.rol) → ROL 폴더.</summary>
    public string FolderForSong(string pathOrExtension)
    {
        string ext = string.IsNullOrEmpty(pathOrExtension) ? "" : Path.GetExtension(pathOrExtension);
        if (ext.Length == 0 && pathOrExtension != null && pathOrExtension.StartsWith(".")) ext = pathOrExtension;
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
                        case "bankfolder": s.BankFolder = value; break;
                        case "defaultbank": s.DefaultBank = value; break;
                        case "loaddefaultbank": s.LoadDefaultBank = !(value == "0" || value.Equals("false", StringComparison.OrdinalIgnoreCase)); break;
                    }
                }
            }
            else s.Save();
        }
        catch (Exception ex) { s.LoadWarning = "설정 파일을 읽을 수 없어 기본값을 씁니다: " + ex.Message; }
        s.EnsureFolders();
        return s;
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
        sb.AppendLine("BankFolder=" + BankFolder);
        sb.AppendLine("DefaultBank=" + DefaultBank);
        sb.AppendLine("LoadDefaultBank=" + (LoadDefaultBank ? "true" : "false"));
        File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>곡/뱅크 폴더가 없으면 만든다 (실패해도 프로그램은 계속).</summary>
    public void EnsureFolders()
    {
        foreach (var dir in new[] { SongFolderPath, ImsFolderPath, SopFolderPath, TrackerFolderPath, BankFolderPath })
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
