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

    public class TierConfig
    {
        public int DelaySeconds { get; set; }
        public bool IsSequential { get; set; }
    }

    // 核心大循环：接收配对好的 (启动项, 调度规则) 集合
    public async Task RunAsync(
        IEnumerable<(AppItem Item, ScheduleRule Rule)> scheduledItems)
    {
        LoggerService.Log("=== StartFlow Orchestrator Session Started ===");

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

        // 2. 根据优先级分组 (T1, T2, T3...)，并按数字从小到大排序
        var groupedItems = scheduledItems
            .GroupBy(x => x.Rule.PriorityLevel)
            .OrderBy(g => g.Key);

        // 3. 串行遍历每一个 T 级别
        foreach (var group in groupedItems)
        {
            // 安全拦截：绝对不能启动被分配到 Disabled (0) 或 Ignored (1) 队列中的程序
            // Ignored 已经完全交还给 Windows 原生系统去拉起了，大管家不再插手。
            if (group.Key == 0 || group.Key == 1) continue;

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
                var currentLevelWaitTasks = group.Select(x => ProcessItemAsync(x.Item, x.Rule, group.Key));

                // 设立屏障：等待当前 T 级别所有程序的监控任务全部完成
                await Task.WhenAll(currentLevelWaitTasks);
            }

            LoggerService.Log($"--- Tier {group.Key} Completed ---");
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