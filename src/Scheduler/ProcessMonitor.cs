using System;
using System.Diagnostics;
using System.Threading.Tasks;
using StartFlow.Models;

namespace StartFlow.Scheduler;

public class ProcessMonitor
{
    // 异步等待入口，不阻塞主调度线程
    public async Task WaitAsync(Process? process, ScheduleRule rule)
    {
        // 1. 如果规则是强制时间延迟，无视进程状态，直接等待
        if (rule.Condition == WaitConditionType.TimeDelay)
        {
            await Task.Delay(TimeSpan.FromSeconds(rule.WaitParameter));
            return;
        }

        // 2. 如果进程为空或瞬间退出 (例如 UWP 的 explorer 启动器)
        // 我们平滑降级为 2 秒的保护性延迟，防止下一个 T 级被瞬间并发拉起
        try
        {
            if (process == null || process.HasExited)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                return;
            }
        }
        catch (Exception)
        {
            // 核心修复：如果目标程序是 SignalRGB 这种需要管理员权限的软件，
            // 普通权限的 StartFlow 尝试读取 HasExited 会抛出 Access Denied (拒绝访问) 异常。
            // 遇到此异常直接忽略，默认认为该进程依然存活，继续往下走到各自的监控分支。
        }

        // 3. 根据条件路由到不同的监控策略
        switch (rule.Condition)
        {
            case WaitConditionType.InputIdle:
                await WaitInputIdleAsync(process, rule.WaitParameter);
                break;

            case WaitConditionType.CpuIdle:
                await WaitCpuIdleAsync(process, rule.WaitParameter);
                break;
        }
    }

    private async Task WaitInputIdleAsync(Process process, int timeoutSeconds)
    {
        try
        {
            // WaitForInputIdle 仅对拥有独立消息循环的 GUI 程序有效
            // 用 Task.Run 包装以避免阻塞 UI 线程
            await Task.Run(() =>
            {
                if (!process.HasExited)
                {
                    // 将秒转换为毫秒传入
                    process.WaitForInputIdle(timeoutSeconds * 1000);
                }
            });
        }
        catch (Exception)
        {
            // 目标程序如果是无 GUI 的后台服务，调用此方法会抛出异常
            // 捕获异常并降级为保护性延迟
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private async Task WaitCpuIdleAsync(Process process, int thresholdPercent)
    {
        try
        {
            int consecutiveIdleSeconds = 0;
            TimeSpan prevCpu = process.TotalProcessorTime;
            DateTime prevTime = DateTime.UtcNow;

            // 设置 30 秒的硬超时，防止某些流氓软件 CPU 居高不下导致队列卡死
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000); // 采样间隔 1 秒

                if (process.HasExited) break;

                TimeSpan curCpu = process.TotalProcessorTime;
                DateTime curTime = DateTime.UtcNow;

                double usage = CalculateCpuUsage(
                    prevCpu, curCpu, prevTime, curTime);

                if (usage < thresholdPercent)
                {
                    consecutiveIdleSeconds++;
                    if (consecutiveIdleSeconds >= 2) break; // 连续2秒达标即放行
                }
                else
                {
                    consecutiveIdleSeconds = 0; // 出现波动则重新计时
                }

                prevCpu = curCpu;
                prevTime = curTime;
            }
        }
        catch (Exception)
        {
            // 权限不足无法读取 TotalProcessorTime 时安全退出
        }
    }

    // 纯函数：根据两次采样的数据计算真实 CPU 占用百分比
    private double CalculateCpuUsage(
        TimeSpan prevCpu, TimeSpan curCpu, DateTime prevTime, DateTime curTime)
    {
        double cpuMs = (curCpu - prevCpu).TotalMilliseconds;
        double timeMs = (curTime - prevTime).TotalMilliseconds;
        return timeMs > 0 ? (cpuMs / (timeMs * Environment.ProcessorCount)) * 100 : 0;
    }
}