using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using StartFlow.Models;

namespace StartFlow.Scheduler;

public class ProcessLauncher
{
    // 纯函数风格的方法，输入 AppItem，安全地尝试启动并返回 Process 句柄
    public Process? Launch(AppItem item, bool isSilent = false)
    {
        // 对于遗留的幽灵项，或者没有有效路径的项，直接忽略以防报错
        if (item.Sources.Contains(StartupSource.RegistryGhostItem) || 
            string.IsNullOrWhiteSpace(item.FilePath))
        {
            return null;
        }

        if (item.Sources.Contains(StartupSource.UwpApp))
        {
            return LaunchUwp(item.FilePath, isSilent);
        }

        return LaunchWin32(item, isSilent);
    }

    private Process? LaunchWin32(AppItem item, bool isSilent)
    {
        try
        {
            // 尝试提取工作目录，部分游戏和老软件必须在自己的目录下启动才正常
            string workDir = Path.GetDirectoryName(item.FilePath) 
                             ?? string.Empty;

            string args = item.Arguments ?? string.Empty;

            if (isSilent)
            {
                // 智能注入：多数现代软件 (Electron/Qt) 如 Clash Verge 无视底层 WindowStyle，必须靠参数静默。
                // 为了防止 SignalRGB 这种异类崩溃，我们仅在没有敏感静默参数时注入最通用的 "--silent"
                if (!args.Contains("silent", StringComparison.OrdinalIgnoreCase) &&
                    !args.Contains("hide", StringComparison.OrdinalIgnoreCase) &&
                    !args.Contains("minimized", StringComparison.OrdinalIgnoreCase) &&
                    !args.Contains("background", StringComparison.OrdinalIgnoreCase))
                {
                    // 智能注入：Qt/原生 Win32 多用 -silent，而 Electron 多用 --silent。
                    // 同时注入两者以覆盖所有主流 UI 框架的静默解析规范，互不干扰。
                    args = string.IsNullOrWhiteSpace(args) ? "-silent --silent" : args + " -silent --silent";
                }
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = item.FilePath,
                Arguments = args,
                UseShellExecute = true, // 必须为 true 才能解析环境变量和 .lnk
                WorkingDirectory = workDir
            };

            if (isSilent)
            {
                startInfo.WindowStyle = ProcessWindowStyle.Hidden;
                startInfo.CreateNoWindow = true;
            }

            return Process.Start(startInfo);
        }
        catch (Exception)
        {
            // 遇到权限不足或文件已损坏/丢失的情况，安全捕获并跳过
            return null;
        }
    }

    private Process? LaunchUwp(string aumid, bool isSilent)
    {
        try
        {
            if (isSilent)
            {
                // 彻底放弃第三方手动静默拉起 UWP（极易引发弹窗或权限崩溃）。
                // 现已改为：让 ViewModel 将静默 UWP 的注册表状态保持开启，交由 Windows 原生 BAM 完美后台静默调度。
                return null;
            }

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