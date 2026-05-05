using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using StartFlow.Models;

namespace StartFlow.Scheduler;

public class ProcessLauncher
{
    public enum ActivateOptions
    {
        None = 0x00000000,
        DesignMode = 0x00000001,
        NoErrorUI = 0x00000002,
        NoSplashScreen = 0x00000004,
        PreLaunch = 0x02000000 // UWP 后台静默启动的核心参数
    }

    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        int ActivateApplication(
            [In, MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [In, MarshalAs(UnmanagedType.LPWStr)] string arguments,
            [In] ActivateOptions options,
            [Out] out uint processId);
        
        int ActivateForFile([In] string appUserModelId, [In] IntPtr itemArray, [In] string verb, [Out] out uint processId);
        int ActivateForProtocol([In] string appUserModelId, [In] IntPtr itemArray, [Out] out uint processId);
    }

    [ComImport, Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C")]
    private class ApplicationActivationManager { }

    // 纯函数风格的方法，输入 AppItem，安全地尝试启动并返回 Process 句柄
    public Process? Launch(AppItem item, bool isSilent = false)
    {
        // 对于遗留的幽灵项，或者没有有效路径的项，直接忽略以防报错
        if (item.Source == StartupSource.RegistryGhostItem || 
            string.IsNullOrWhiteSpace(item.FilePath))
        {
            return null;
        }

        if (item.Source == StartupSource.UwpApp)
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
                    args = string.IsNullOrWhiteSpace(args) ? "--silent" : args + " --silent";
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
                try
                {
                    var activationManager = (IApplicationActivationManager)new ApplicationActivationManager();
                    ActivateOptions options = ActivateOptions.PreLaunch | ActivateOptions.NoErrorUI;
                    
                    activationManager.ActivateApplication(aumid, string.Empty, options, out uint processId);
                    
                    if (processId > 0)
                    {
                        return Process.GetProcessById((int)processId);
                    }
                }
                catch
                {
                    // 核心保护：如果 StartFlow 以管理员权限运行，调用 COM 激活 UWP 会直接抛出 Access Denied。
                    // 遇到权限墙时，平滑跳出并降级到下方使用 explorer.exe 的原生方案，绝不让启动中断。
                }
            }

            // 微软官方推荐的拉起 UWP/Appx 的标准方式 (AUMID)
            var startInfo = new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $@"shell:AppsFolder\{aumid}",
                UseShellExecute = true
            };

            if (isSilent)
            {
                // 如果 COM 激活失败并降级到了这里，通过最小化窗口尽力压制它弹出
                startInfo.WindowStyle = ProcessWindowStyle.Minimized;
            }

            return Process.Start(startInfo);
        }
        catch (Exception)
        {
            return null;
        }
    }
}