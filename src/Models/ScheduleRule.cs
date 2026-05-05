using System;

namespace StartFlow.Models;

// 定义等待触发下一级任务的条件类型
public enum WaitConditionType
{
    CpuIdle,      // 监控目标进程，等待 CPU 使用率降至闲置状态
    TimeDelay,    // 简单的强制时间延迟 (如等待 10 秒)
    InputIdle     // 调用底层 WaitForInputIdle 判断进程就绪
}

public record ScheduleRule(
    Guid AppItemId,       // 关联的 AppItem 的唯一标识
    int PriorityLevel,    // 优先级 (1 代表 T1, 2 代表 T2, 依此类推)
    WaitConditionType Condition,
    int WaitParameter     // 辅助参数: 例如延迟的秒数, 或 CPU 阈值百分比
)
{
    public bool IsSilent { get; init; } = false;
    public int DelaySeconds { get; init; } = 0;
    public int OrderIndex { get; init; } = 0;

    // 提供纯函数，生成默认规则（例如默认分配到 T1 且无延迟）
    public static ScheduleRule CreateDefault(Guid appItemId, int defaultPriority = 2)
    {
        int defaultWaitTime = 0;
        
        return new ScheduleRule(
            appItemId,
            defaultPriority,
            WaitConditionType.TimeDelay,
            defaultWaitTime
        );
    }
}
