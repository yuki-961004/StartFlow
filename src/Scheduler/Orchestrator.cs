using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.IO;
using System.Text.Json;
using System.Runtime.InteropServices;
using StartFlow.Services;
using StartFlow.Models;

namespace StartFlow.Scheduler;

public class Orchestrator
{
    private readonly ProcessLauncher _launcher;
    private readonly ProcessMonitor _monitor;

    public Orchestrator()
    {
        _launcher = new ProcessLauncher();
        _monitor = new ProcessMonitor();
    }

    private async Task WaitUntilDesktopUnlockedAsync()
    {
        // [极简且最安全的修复]：完全抛弃危险的底层 C++ 桌面指针探测，改用最稳固的 C# 进程探测！
        // 在 Windows 10/11 中，只要处于输入 PIN/密码的锁屏界面，系统必定会挂载 "LogonUI.exe" 进程。
        // 只要该进程存在，大管家就原地待命；当你输入完 PIN 进入桌面，该进程会立刻被系统销毁，此时大管家瞬间放行。
        for (int i = 0; i < 600; i++)
        {
            var logonProcesses = System.Diagnostics.Process.GetProcessesByName("LogonUI");
            if (logonProcesses.Length == 0)
            {
                return; // 锁屏界面已消失，安全进入真实桌面，放行开机流程！
            }
            await Task.Delay(1000);
        }
    }

    public class TierConfig
    {
        public int DelaySeconds { get; set; }
        public bool IsSequential { get; set; }
    }

    public class GlobalSettings
    {
        public bool EnableCurtain { get; set; } = false;
        public int TargetCurtainTier { get; set; } = 1;
        public string CurtainImagePath { get; set; } = string.Empty;
        public double CurtainBlurOpacity { get; set; } = 1.0;
        public bool CurtainShowSpinner { get; set; } = true;
        public string CurtainText { get; set; } = "Wait for it...";
        public bool CurtainEnableAnimation { get; set; } = true;
    }

    // 核心大循环：接收配对好的 (启动项, 调度规则) 集合
    public async Task RunAsync(
        IEnumerable<(AppItem Item, ScheduleRule Rule)> scheduledItems)
    {
        LoggerService.Log("=== StartFlow Orchestrator Session Started ===");

        // [核心修复]：应对 Windows 10/11 的 ARSO (自动登录并锁屏) 机制。
        // 此处强制引擎挂起，直到用户真正输入密码、系统解锁露出桌面后，才允许弹出幕布和启动软件！
        LoggerService.Log("[Orchestrator] Waiting for desktop to be unlocked...");
        await WaitUntilDesktopUnlockedAsync();
        LoggerService.Log("[Orchestrator] Desktop is active. Proceeding...");

        // 1. 读取并解析来自 UI 保存的 tier_configs.json (T级配置)
        var tierConfigs = new Dictionary<string, TierConfig>();
        try
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string configPath = Path.Combine(appData, "StartFlow", "tier_configs.json");
            if (File.Exists(configPath))
            {
                string json = File.ReadAllText(configPath);
                tierConfigs = JsonSerializer.Deserialize<Dictionary<string, TierConfig>>(json) ?? new Dictionary<string, TierConfig>();
            }
        }
        catch (Exception ex)
        {
            LoggerService.Log($"[Orchestrator] Warning: Failed to load tier_configs.json. {ex.Message}");
        }

        // 读取全局外观及幕布配置
        var globalSettings = new GlobalSettings();
        try
        {
            string globalConfigPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StartFlow", "global_settings.json");
            if (File.Exists(globalConfigPath))
            {
                string json = File.ReadAllText(globalConfigPath);
                globalSettings = JsonSerializer.Deserialize<GlobalSettings>(json) ?? new GlobalSettings();
            }
        }
        catch (Exception ex) { LoggerService.Log($"[Orchestrator] Warning: Failed to load global_settings.json. {ex.Message}"); }

        UI.CurtainWindow? curtainWindow = null;
        if (globalSettings.EnableCurtain && globalSettings.TargetCurtainTier >= 0)
        {
            LoggerService.Log($"[Orchestrator] Initiating Full-Screen Curtain... Waiting for Tier {globalSettings.TargetCurtainTier} to finish.");
            curtainWindow = new UI.CurtainWindow(globalSettings.CurtainImagePath, globalSettings.CurtainBlurOpacity, globalSettings.CurtainShowSpinner, globalSettings.CurtainText, globalSettings.CurtainEnableAnimation);
            curtainWindow.Activate();
        }

        // 2. 根据优先级分组 (T1, T2, T3...)，并按数字从小到大排序
        var groupedItems = scheduledItems
            .GroupBy(x => x.Rule.PriorityLevel)
            .OrderBy(g => g.Key);

        // 3. 串行遍历每一个 T 级别
        foreach (var group in groupedItems)
        {
            // 安全拦截：绝对不能启动被分配到 Disabled (0) 队列中的程序
            if (group.Key == 0) continue;

            // 如果用户将长条拖到了最顶端（Target=0），意味着在第一个程序启动前就揭开幕布
            if (curtainWindow != null && globalSettings.TargetCurtainTier == 0)
            {
                await curtainWindow.DismissAsync();
                curtainWindow = null;
            }

            LoggerService.Log($"--- Processing Tier {group.Key} ---");

            // 提取该 T 级实际对应的延迟和运行模式设定
            int delaySeconds = 0;
            bool isSequential = false;
            if (tierConfigs.TryGetValue(group.Key.ToString(), out var config))
            {
                delaySeconds = config.DelaySeconds;
                isSequential = config.IsSequential;
            }

            // 拦截：如果该层级设置了启动前延迟，在这里让线程真正休息指定的时间
            if (delaySeconds > 0)
            {
                LoggerService.Log($"[Tier {group.Key}] Delaying startup by {delaySeconds} seconds...");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
            }

            if (isSequential)
            {
                // 串行执行模式（Sequential）：逐个启动，前一个程序拉起且状态就绪后，再启动下一个
                foreach (var (item, rule) in group)
                {
                    await ProcessItemAsync(item, rule, group.Key);
                }
            }
            else
            {
                // 并行执行模式（Parallel）：瞬间拉起当前 T 级别的所有程序
                var currentLevelWaitTasks = new List<Task>();
                foreach (var (item, rule) in group)
                {
                    Task monitorTask = ProcessItemAsync(item, rule, group.Key);
                    currentLevelWaitTasks.Add(monitorTask);
                }

                // 设立屏障：等待当前 T 级别所有程序的监控任务全部完成
                await Task.WhenAll(currentLevelWaitTasks);
            }

            LoggerService.Log($"--- Tier {group.Key} Completed ---");
            
            // 如果当前的 T 级已经达到了配置要求撤掉幕布的 T 级，立即销毁幕布窗口
            if (curtainWindow != null && group.Key >= globalSettings.TargetCurtainTier)
            {
                LoggerService.Log($"[Orchestrator] Target tier finished. Dismissing curtain.");
                await curtainWindow.DismissAsync();
                curtainWindow = null;
            }
        }

        // [保底保护机制]：如果用户设置幕布在 T3 结束，但实际上列表里并没有分配任何 T3 及以后的软件，
        // 导致上面的 foreach 循环未能命中撤销逻辑，必须在此处强制收起幕布，防止永远卡死。
        if (curtainWindow != null)
        {
            LoggerService.Log($"[Orchestrator] Fallback: Dismissing curtain at the end of all tasks.");
            await curtainWindow.DismissAsync();
            curtainWindow = null;
        }

        LoggerService.Log("=== StartFlow Orchestrator Session Ended ===");
    }

    // 辅助子函数：精确打点每个程序的开始与监控结束时间
    private async Task ProcessItemAsync(AppItem item, ScheduleRule rule, int tier)
    {
        LoggerService.Log($"[Tier {tier}] [START] {item.Name}");
        
        // 1. 如果设置了延迟，启动前先硬等一段时间
        if (rule.DelaySeconds > 0)
        {
            await Task.Delay(TimeSpan.FromSeconds(rule.DelaySeconds));
        }

        System.Diagnostics.Process? process = null;

        // 2. 将程序交由 Launcher 拉起，并传递静默状态参数
        // Launcher 内部会根据是 Win32 还是 UWP 自动分配最适合的底层静默方式。
        // 绝不篡改用户的 Arguments 启动参数，防止类似 SignalRGB 的硬件软件崩溃。
        process = _launcher.Launch(item, rule.IsSilent);
        
        if (process != null)
        {
            try
            {
                await _monitor.WaitAsync(process, rule);
            }
            catch (Exception ex)
            {
                LoggerService.Log($"[Tier {tier}] [WARN] Monitor failed for {item.Name} (Permission/Exit): {ex.Message}");
                // 终极保护：如果监控环节出现任何未知的系统级异常，绝不让引擎崩溃
                // 平滑降级为等待 2 秒后强制放行，继续拉起下一个软件
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
        
        LoggerService.Log($"[Tier {tier}] [END] {item.Name} ready.");
    }
}