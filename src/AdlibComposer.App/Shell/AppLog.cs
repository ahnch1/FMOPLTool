using System;
using System.Collections.Generic;

namespace AdlibComposer.App.Shell;

internal enum LogLevel { Info, Warning, Error }

internal sealed class LogEntry
{
    public DateTime Time;
    public LogLevel Level;
    public string Category;
    public string Message;
    /// <summary>"상세"를 켰을 때만 보이는 추가 내용 (예외 스택 등).</summary>
    public string Detail;
}

/// <summary>진행 로그 (화면 아래 로그 패널). 어디서든 AppLog.Info/Warn/Error로 남긴다.</summary>
internal static class AppLog
{
    public const int MaxEntries = 2000;
    private static readonly List<LogEntry> entries = new List<LogEntry>();

    public static event Action<LogEntry> Added;
    public static event Action Cleared;

    public static IReadOnlyList<LogEntry> Entries { get { return entries; } }

    public static int Count(LogLevel level)
    {
        int n = 0;
        foreach (var e in entries) if (e.Level == level) n++;
        return n;
    }

    public static void Info(string category, string message, string detail = null) { Write(LogLevel.Info, category, message, detail); }
    public static void Warn(string category, string message, string detail = null) { Write(LogLevel.Warning, category, message, detail); }
    public static void Error(string category, string message, string detail = null) { Write(LogLevel.Error, category, message, detail); }

    public static void Error(string category, string message, Exception ex)
    {
        Write(LogLevel.Error, category, message + (ex != null ? ": " + ex.Message : ""), ex != null ? ex.ToString() : null);
    }

    public static void Write(LogLevel level, string category, string message, string detail)
    {
        var e = new LogEntry { Time = DateTime.Now, Level = level, Category = category ?? "", Message = message ?? "", Detail = detail };
        entries.Add(e);
        if (entries.Count > MaxEntries) entries.RemoveRange(0, entries.Count - MaxEntries);
        if (Added != null) Added(e);
    }

    public static void Clear()
    {
        entries.Clear();
        if (Cleared != null) Cleared();
    }
}
