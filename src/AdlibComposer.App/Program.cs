using System;
using System.IO;
using System.Windows.Forms;
using AdlibComposer.App.Shell;

namespace AdlibComposer.App;

internal static class Program
{
    /// <summary>처리하지 못한 오류를 남기는 파일 (실행 폴더): 호출 위치(스택)까지.</summary>
    public static string ErrorLogPath { get { return Path.Combine(AppSettings.BaseDirectory, "FMOPLTool-error.log"); } }

    [STAThread]
    private static void Main()
    {
        AdlibComposer.Audio.Windows.WindowsAudio.Register();
        AdlibComposer.Audio.SystemDefinitions.OverridePath = System.IO.Path.Combine(AppSettings.ResourcePath, "systems.json");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // 화면 스레드에서 처리하지 못한 오류: 프로그램을 끝내지 않고 진행 로그 + 오류 파일에 스택까지 남긴다
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (s, e) => Report("화면", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Report("스레드", e.ExceptionObject as Exception);
        // 진단: 환경 변수 FMOPL_FIRSTCHANCE=1 이면 잡힌 것까지 모든 범위 오류(ArgumentOutOfRange)의 스택을 오류 파일에
        if (Environment.GetEnvironmentVariable("FMOPL_FIRSTCHANCE") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (s, e) =>
            {
                if (e.Exception is ArgumentOutOfRangeException) WriteErrorFile("first-chance", e.Exception.ToString() + Environment.NewLine + Environment.StackTrace);
            };

        Application.Run(new MainForm());
    }

    private static void Report(string where, Exception ex)
    {
        if (ex == null) return;
        WriteErrorFile(where, ex.ToString());
        try { AppLog.Error("오류", "처리하지 못한 오류 (" + where + ") — " + ex.GetType().Name + ": " + ex.Message + " · 스택: " + ErrorLogPath, ex); }
        catch (Exception) { }
    }

    private static readonly object fileLock = new object();

    private static void WriteErrorFile(string where, string text)
    {
        try
        {
            lock (fileLock)
                File.AppendAllText(ErrorLogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + where + "] v" + MainForm.VersionText + Environment.NewLine + text + Environment.NewLine + Environment.NewLine);
        }
        catch (Exception) { }
    }
}
