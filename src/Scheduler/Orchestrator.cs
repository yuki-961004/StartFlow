using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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

    // 核心大循环：接收配对好的 (启动项, 调度规则) 集合
    public async Task RunAsync(
        IEnumerable<(AppItem Item, ScheduleRule Rule)> scheduledItems)
    {
        LoggerService.Log("=== StartFlow Orchestrator Session Started ===");

        // 1. 根据优先级分组 (T1, T2, T3...)，并按数字从小到大排序
        var groupedItems = scheduledItems
            .GroupBy(x => x.Rule.PriorityLevel)
            .OrderBy(g => g.Key);

        // 2. 串行遍历每一个 T 级别
        foreach (var group in groupedItems)
        {
            // 安全拦截：绝对不能启动被分配到 Disabled (0) 队列中的程序
            if (group.Key == 0) continue;

            LoggerService.Log($"--- Processing Tier {group.Key} ---");
            var currentLevelWaitTasks = new List<Task>();

            // 3. 并行拉起当前 T 级别的所有程序
            foreach (var (item, rule) in group)
            {
                Task monitorTask = ProcessItemAsync(item, rule, group.Key);
                currentLevelWaitTasks.Add(monitorTask);
            }

            // 4. 设立屏障：等待当前 T 级别所有程序的监控任务全部完成
            await Task.WhenAll(currentLevelWaitTasks);
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

        // 2. 检查是否开启了静默模式
        if (rule.IsSilent)
        {
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = item.FilePath,
                Arguments = string.IsNullOrWhiteSpace(item.Arguments) ? "--silent -minimized -hide" : item.Arguments,
                UseShellExecute = true,
                WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            };
            try { process = System.Diagnostics.Process.Start(info); } catch { }
        }
        else
        {
            process = _launcher.Launch(item);
        }
        
        if (process != null)
        {
            await _monitor.WaitAsync(process, rule);
        }
        
        LoggerService.Log($"[Tier {tier}] [END] {item.Name} ready.");
    }
}