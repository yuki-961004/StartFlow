using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using StartFlow.Core;
using StartFlow.Services;
using StartFlow.Models;

namespace StartFlow.ViewModels;

// 代表一个 T 级分组的数据结构
public class TierGroup : ObservableCollection<AppItemViewModel>
{
    public string TierName { get; }
    public bool IsDisabledGroup { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ExpandIconGlyph)));
            }
        }
    }

    public string ExpandIconGlyph => _isExpanded ? "\uE738" : "\uE710"; // 展开时显示减号，折叠时显示加号

    public Visibility ToggleVisibility => 
        IsDisabledGroup ? Visibility.Visible : Visibility.Collapsed;

    public TierGroup(string tierName, bool isDisabledGroup, IEnumerable<AppItemViewModel> items) 
        : base(items)
    {
        TierName = tierName;
        IsDisabledGroup = isDisabledGroup;
        _isExpanded = !isDisabledGroup; // 禁用组默认折叠 (false)，其他组默认展开 (true)

        foreach (var item in items)
        {
            item.IsVisible = _isExpanded;
        }
    }

    public void ToggleExpand()
    {
        IsExpanded = !IsExpanded;
        foreach (var item in this)
        {
            item.IsVisible = IsExpanded;
        }
    }
}

public class MainViewModel
{
    private readonly StartupItemScanner _scanner;
    private readonly ConfigurationService _configService;

    // UI 绑定的分组集合
    public ObservableCollection<TierGroup> TierGroups { get; } = new();
    
    // 全局所有的 T 级名称列表
    public ObservableCollection<string> AvailableTiers { get; } = new();

    public MainViewModel()
    {
        _scanner = new StartupItemScanner();
        _configService = new ConfigurationService();
    }

    public async Task LoadItemsAsync()
    {
        TierGroups.Clear();
        AvailableTiers.Clear();

        var tiers = _configService.LoadTiers();
        foreach (var t in tiers)
        {
            AvailableTiers.Add(t);
        }

        var savedRules = _configService.LoadRules();
        var items = await Task.Run(() => _scanner.ScanAll());
        var viewModels = new List<AppItemViewModel>();

        foreach (var item in items)
        {
            // 过滤掉大管家自己，绝不让它出现在常规 UI 列表中供用户修改
            if (item.Name.Equals("StartFlow", StringComparison.OrdinalIgnoreCase)) continue;

            // 如果本地有保存过的规则就使用，否则给它默认分配到 T1
            var rule = savedRules.FirstOrDefault(r => r.AppItemId == item.Id) 
                       ?? ScheduleRule.CreateDefault(item.Id);
                       
            // 保护机制：如果规则要求的索引大于现有列表，自动填充防止崩溃
            while (rule.PriorityLevel >= AvailableTiers.Count)
            {
                AvailableTiers.Add($"T{AvailableTiers.Count} (Unknown)");
            }
            
            viewModels.Add(new AppItemViewModel(item, rule, AvailableTiers));
        }

        // 按照优先级进行分组，并转换为 UI 可绑定的分组结构
        var grouped = viewModels
            .GroupBy(x => x.PriorityIndex)
            .OrderBy(g => g.Key == 0 ? int.MaxValue : g.Key); // 0(Disabled)会被丢到最后

        foreach (var group in grouped)
        {
            string tierName = group.Key < AvailableTiers.Count 
                ? AvailableTiers[group.Key] 
                : $"Tier {group.Key}";
            bool isDisabled = group.Key == 0;
            TierGroups.Add(new TierGroup(tierName, isDisabled, group));
        }
    }

    // 核心保存副作用：写入本地配置，并同步调用注册表拦截器接管系统
    public void SaveConfiguration()
    {
        var hijacker = new RegistryHijacker();
        var rulesToSave = new List<ScheduleRule>();

        foreach (var group in TierGroups)
        {
            foreach (var vm in group)
            {
                rulesToSave.Add(vm.Rule);
                
                // 核心原理：所有被纳入管理的软件，统统在系统层面禁用！
                // 这样 Windows 就不会乱拉起它们，一切由 Orchestrator 按顺序指挥
                hijacker.DisableItem(vm.Item);
            }
        }
        _configService.SaveRules(rulesToSave);

        // 将 StartFlow 自己注册为唯一启用的开机启动项
        try
        {
            string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? string.Empty;
            if (!string.IsNullOrEmpty(exePath))
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                key?.SetValue("StartFlow", $"\"{exePath}\" --silent");
                
                // 确保大管家自己在系统里绝对不能被禁用
                var selfItem = AppItem.Create("StartFlow", exePath, "--silent", StartupSource.RegistryCurrentUser);
                hijacker.EnableItem(selfItem);
            }
        }
        catch { }
    }

    public void AddNewTier(string? customName)
    {
        string name = string.IsNullOrWhiteSpace(customName) 
            ? "Custom" 
            : customName.Trim();
            
        string newTier = $"T{AvailableTiers.Count} ({name})";
        AvailableTiers.Add(newTier);
        _configService.SaveTiers(AvailableTiers);
    }

    public async Task RemoveTierAsync(int indexToRemove)
    {
        // 绝对不允许删除 Disabled (Index 0) 队列，以保证系统根基
        if (indexToRemove <= 0 || indexToRemove >= AvailableTiers.Count) return;

        AvailableTiers.RemoveAt(indexToRemove);
        _configService.SaveTiers(AvailableTiers);

        // 安全降级与索引修正
        foreach (var group in TierGroups)
        {
            foreach (var vm in group)
            {
                if (vm.PriorityIndex == indexToRemove) 
                {
                    vm.PriorityIndex = indexToRemove - 1; // 降级到前一个队列
                }
                else if (vm.PriorityIndex > indexToRemove)
                {
                    vm.PriorityIndex = vm.PriorityIndex - 1; // 索引前移，保持对应关系
                }
            }
        }
        
        SaveConfiguration();
        await LoadItemsAsync();
    }

    // 添加新程序：使用标准的注册表方式写入自启动项
    public async Task AddProgramToStartupAsync(string filePath)
    {
        try 
        { 
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key != null)
            {
                string name = Path.GetFileNameWithoutExtension(filePath);
                key.SetValue(name, $"\"{filePath}\"");
            }
        } 
        catch { } // 防止权限问题引发崩溃

        await LoadItemsAsync();
    }

    // 核心定位逻辑：自动打开文件所在位置、或注册表位置、或UWP设置
    public void OpenItemLocation(AppItem item)
    {
        try
        {
            if (item.Source == StartupSource.StartupFolder || 
                item.Source == StartupSource.CommonStartupFolder)
            {
                if (File.Exists(item.FilePath))
                {
                    System.Diagnostics.Process.Start(
                        "explorer.exe", $"/select,\"{item.FilePath}\"");
                }
            }
            else if (item.Source == StartupSource.UwpApp)
            {
                // 唤起 Win11 自带的启动项管理页面，UWP 可以在那里检查
                var info = new System.Diagnostics.ProcessStartInfo(
                    "ms-settings:startupapps") { UseShellExecute = true };
                System.Diagnostics.Process.Start(info);
            }
            else
            {
                // 黑客技巧：写入 LastKey 让注册表编辑器打开时自动跳转
                string keyPath = GetRegistryKeyString(item.Source);
                if (!string.IsNullOrEmpty(keyPath))
                {
                    using var reg = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit");
                    reg?.SetValue("LastKey", keyPath);
                    
                    var info = new System.Diagnostics.ProcessStartInfo("regedit.exe") 
                        { UseShellExecute = true };
                    System.Diagnostics.Process.Start(info);
                }
            }
        }
        catch { } // 防止权限不足或文件不存在导致崩溃
    }

    private string GetRegistryKeyString(StartupSource source)
    {
        return source switch
        {
            StartupSource.RegistryCurrentUser => 
                @"Computer\HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run",
            StartupSource.RegistryLocalMachine => 
                @"Computer\HKEY_LOCAL_MACHINE\Software\Microsoft\Windows\CurrentVersion\Run",
            StartupSource.RegistryLocalMachineWow64 => 
                @"Computer\HKEY_LOCAL_MACHINE\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run",
            StartupSource.RegistryGhostItem => 
                @"Computer\HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run",
            _ => @"Computer\HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run"
        };
    }
}