using System;
using System.IO;

namespace StartFlow.Services;

public static class LoggerService
{
    private static readonly string LogPath;
    private static readonly object _lock = new object();

    static LoggerService()
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "StartFlow");
        Directory.CreateDirectory(appFolder);
        LogPath = Path.Combine(appFolder, "startup.log");
    }

    public static void Log(string message)
    {
        lock (_lock)
        {
            try
            {
                // 记录精确到毫秒的时间戳
                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
                File.AppendAllText(LogPath, $"[{time}] {message}\n");
            }
            catch { } // 极小概率的 IO 冲突直接忽略，防止阻塞主调度
        }
    }
}