using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using StartFlow.UI;

namespace StartFlow;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        this.InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmdArgs = Environment.GetCommandLineArgs();
        if (cmdArgs.Contains("--silent"))
        {
            // 后台静默启动模式：不渲染界面，直接执行大管家调度任务
            _ = RunSilentOrchestratorAsync();
        }
        else
        {
            // 正常启动模式：打开 UI 界面配置规则
            _window = new MainWindow();
            _window.Activate();
        }
    }

    private async Task RunSilentOrchestratorAsync()
    {
        var scanner = new StartFlow.Core.StartupItemScanner();
        var configService = new StartFlow.Services.ConfigurationService();
        var orchestrator = new StartFlow.Scheduler.Orchestrator();

        var items = scanner.ScanAll();
        var rules = configService.LoadRules();
        var scheduledItems = new System.Collections.Generic.List<(StartFlow.Models.AppItem, StartFlow.Models.ScheduleRule)>();

        foreach (var item in items)
        {
            if (item.Name.Equals("StartFlow", StringComparison.OrdinalIgnoreCase)) continue;

            var rule = rules.FirstOrDefault(r => r.AppItemId == item.Id);
            // 只调度那些没有被用户放到 Disabled (0) 队列中的软件
            if (rule != null && rule.PriorityLevel > 0)
            {
                scheduledItems.Add((item, rule));
            }
        }

        await orchestrator.RunAsync(scheduledItems);
        
        // 调度完成后干脆利落地退出后台进程
        Environment.Exit(0);
    }
}