using System;
using System.Diagnostics;
using System.IO;
using StartFlow.Models;

namespace StartFlow.Scheduler;

public class ProcessLauncher
{
    // 纯函数风格的方法，输入 AppItem，安全地尝试启动并返回 Process 句柄
    public Process? Launch(AppItem item)
    {
        // 对于遗留的幽灵项，或者没有有效路径的项，直接忽略以防报错
        if (item.Source == StartupSource.RegistryGhostItem || 
            string.IsNullOrWhiteSpace(item.FilePath))
        {
            return null;
        }

        if (item.Source == StartupSource.UwpApp)
        {
            return LaunchUwp(item.FilePath);
        }

        return LaunchWin32(item);
    }

    private Process? LaunchWin32(AppItem item)
    {
        try
        {
            // 尝试提取工作目录，部分游戏和老软件必须在自己的目录下启动才正常
            string workDir = Path.GetDirectoryName(item.FilePath) 
                             ?? string.Empty;

            var startInfo = new ProcessStartInfo
            {
                FileName = item.FilePath,
                Arguments = item.Arguments,
                UseShellExecute = true, // 必须为 true 才能解析环境变量和 .lnk
                WorkingDirectory = workDir
            };

            return Process.Start(startInfo);
        }
        catch (Exception)
        {
            // 遇到权限不足或文件已损坏/丢失的情况，安全捕获并跳过
            return null;
        }
    }

    private Process? LaunchUwp(string aumid)
    {
        try
        {
            // 微软官方推荐的拉起 UWP/Appx 的标准方式 (AUMID)
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $@"shell:AppsFolder\{aumid}",
                UseShellExecute = true
            };

            return Process.Start(startInfo);
        }
        catch (Exception)
        {
            return null;
        }
    }
}