using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Text.Json;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using StartFlow.Core;
using StartFlow.Services;
using StartFlow.Models;

namespace StartFlow.ViewModels;

// 代表一个 T 级分组的数据结构
public class TierGroup : ObservableCollection<AppItemViewModel>
{
    public string TierName { get; }
    public int TierIndex { get; }

    // 新增：保存当前 Tier 运行时的环境配置
    public int DelaySeconds { get; set; } = 0;
    public bool IsSequential { get; set; } = false;

    // 控制折叠/展开当前 Tier 所有项
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

    // 新增：动态判断当前组内是否所有程序都是静默状态
    public bool IsAllSilent
    {
        get
        {
            if (Count == 0) return false;
            foreach (var item in this)
            {
                if (!item.IsSilent) return false;
            }
            return true;
        }
    }

    // 新增：控制右侧操作按钮面板（静默/设置）的展开状态
    private bool _isActionsExpanded;
    public bool IsActionsExpanded
    {
        get => _isActionsExpanded;
        set
        {
            if (_isActionsExpanded != value)
            {
                _isActionsExpanded = value;
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsActionsExpanded)));
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ActionsVisibility)));
                OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ActionsToggleIconGlyph)));
            }
        }
    }

    public string ExpandIconGlyph => _isExpanded ? "\uE738" : "\uE710"; // 展开时显示减号，折叠时显示加号

    public Visibility ToggleVisibility => 
        (TierIndex == 0 || TierIndex == 1) ? Visibility.Visible : Visibility.Collapsed;

    public TierGroup(string tierName, int tierIndex, IEnumerable<AppItemViewModel> items) 
        : base(items)
    {
        TierName = tierName;
        TierIndex = tierIndex;
        _isExpanded = tierIndex != 0; // 禁用组默认折叠 (false)，其他组默认展开 (true)

        foreach (var item in items)
        {
            item.IsVisible = _isExpanded;
            item.PropertyChanged += Item_PropertyChanged; // 监听子项的变化
        }
    }

    // 当列表发生拖拽增减改变时，确保事件绑定和属性刷新
    protected override void OnCollectionChanged(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        base.OnCollectionChanged(e);
        if (e.OldItems != null)
        {
            foreach (AppItemViewModel item in e.OldItems)
            {
                item.PropertyChanged -= Item_PropertyChanged;
            }
        }
        if (e.NewItems != null)
        {
            foreach (AppItemViewModel item in e.NewItems)
            {
                item.PropertyChanged += Item_PropertyChanged;
            }
        }
        OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsAllSilent)));
    }

    // 当组内的某个 APP 静默状态改变时，通知 UI 刷新组头部的静默图标
    private void Item_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppItemViewModel.IsSilent))
        {
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsAllSilent)));
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

    public Visibility ActionsVisibility => _isActionsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string ActionsToggleIconGlyph => _isActionsExpanded ? "\uE76B" : "\uE76C"; // 展开显示向左箭头，折叠显示向右箭头

    public void ToggleActions()
    {
        IsActionsExpanded = !IsActionsExpanded;
    }
}

public class TierConfig
{
    public int DelaySeconds { get; set; }
    public bool IsSequential { get; set; }
}

public class EditableTier : INotifyPropertyChanged
{
    private string _name = string.Empty;
    public string Name
    {
        get => _name;
        set { if (_name != value) { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
    }
    public int OriginalIndex { get; set; }

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing != value)
            {
                _isEditing = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditing)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEditingVisibility)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNotEditingVisibility)));
            }
        }
    }
    public Visibility IsEditingVisibility => _isEditing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IsNotEditingVisibility => _isEditing ? Visibility.Collapsed : Visibility.Visible;
    public event PropertyChangedEventHandler? PropertyChanged;
}

public class MainViewModel : INotifyPropertyChanged
{
    private readonly StartupItemScanner _scanner;
    private readonly ConfigurationService _configService;

    // UI 绑定的分组集合
    public ObservableCollection<TierGroup> TierGroups { get; } = new();
    
    // 全局所有的 T 级名称列表
    public ObservableCollection<string> AvailableTiers { get; } = new();

    private readonly string _tierConfigsPath;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsLoading)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel()
    {
        _scanner = new StartupItemScanner();
        _configService = new ConfigurationService();
        _tierConfigsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StartFlow", "tier_configs.json");
    }

    public async Task LoadItemsAsync()
    {
        IsLoading = true;
        try
        {
        TierGroups.Clear();
        AvailableTiers.Clear();

        var tiers = _configService.LoadTiers();
        
        // 如果存在旧配置且没有 Ignored，执行无缝迁移
        bool needsMigration = false;
        if (tiers.Count > 1 && !tiers[1].Equals("Ignored", StringComparison.OrdinalIgnoreCase))
        {
            tiers.Insert(1, "Ignored");
            needsMigration = true;
        }

        foreach (var t in tiers)
        {
            AvailableTiers.Add(t);
        }

        var savedRules = _configService.LoadRules();
        
        if (needsMigration)
        {
            for (int i = 0; i < savedRules.Count; i++)
            {
                if (savedRules[i].PriorityLevel >= 1)
                {
                    savedRules[i] = savedRules[i] with { PriorityLevel = savedRules[i].PriorityLevel + 1 };
                }
            }
            _configService.SaveRules(savedRules);
            _configService.SaveTiers(tiers);
            
            try
            {
                if (File.Exists(_tierConfigsPath))
                {
                    string json = File.ReadAllText(_tierConfigsPath);
                    var oldTierConfigs = JsonSerializer.Deserialize<Dictionary<string, TierConfig>>(json);
                    if (oldTierConfigs != null)
                    {
                        var newTierConfigs = new Dictionary<string, TierConfig>();
                        foreach(var kvp in oldTierConfigs)
                        {
                            if (int.TryParse(kvp.Key, out int oldKey) && oldKey >= 1)
                                newTierConfigs[(oldKey + 1).ToString()] = kvp.Value;
                            else
                                newTierConfigs[kvp.Key] = kvp.Value;
                        }
                        File.WriteAllText(_tierConfigsPath, JsonSerializer.Serialize(newTierConfigs));
                    }
                }
            }
            catch { }
        }

        var items = await Task.Run(() => _scanner.ScanAll());
        var viewModels = new List<AppItemViewModel>();

        foreach (var item in items)
        {
            // 过滤掉大管家自己，绝不让它出现在常规 UI 列表中供用户修改
            if (item.Name.Equals("StartFlow", StringComparison.OrdinalIgnoreCase)) continue;

            // 如果本地有保存过的规则就使用，否则给它默认分配到 T1
            var rule = savedRules.FirstOrDefault(r => r.AppItemId == item.Id) 
                       ?? ScheduleRule.CreateDefault(item.Id, 2);
                       
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

        // 读取本地保存的 Tier 延迟和执行模式配置
        Dictionary<string, TierConfig>? tierConfigs = null;
        try
        {
            if (File.Exists(_tierConfigsPath))
            {
                string json = File.ReadAllText(_tierConfigsPath);
                tierConfigs = JsonSerializer.Deserialize<Dictionary<string, TierConfig>>(json);
            }
        }
        catch { }

        foreach (var group in grouped)
        {
            string tierName = group.Key < AvailableTiers.Count 
                ? AvailableTiers[group.Key] 
                : $"Tier {group.Key}";
            
            // 恢复拖拽后的组内顺序
            var sortedGroup = group.OrderBy(x => x.Rule.OrderIndex);
            var tierGroup = new TierGroup(tierName, group.Key, sortedGroup);
            
            if (tierConfigs != null && tierConfigs.TryGetValue(group.Key.ToString(), out var cfg))
            {
                tierGroup.DelaySeconds = cfg.DelaySeconds;
                tierGroup.IsSequential = cfg.IsSequential;
            }
            TierGroups.Add(tierGroup);
        }
        }
        finally
        {
            IsLoading = false;
        }
    }

    // 核心保存副作用：写入本地配置，并同步调用注册表拦截器接管系统
    public void SaveConfiguration()
    {
        var hijacker = new RegistryHijacker();
        var rulesToSave = new List<ScheduleRule>();
        var tierConfigs = new Dictionary<string, TierConfig>();

        foreach (var group in TierGroups)
        {
            int tierIndex = group.FirstOrDefault()?.PriorityIndex ?? 0;
            tierConfigs[tierIndex.ToString()] = new TierConfig 
            { 
                DelaySeconds = group.DelaySeconds, 
                IsSequential = group.IsSequential
            };

            int orderIndex = 0;
            foreach (var vm in group)
            {
                // 覆盖保存拖拽排序后的真实顺序
                var updatedRule = vm.Rule with { OrderIndex = orderIndex++ };
                vm.UpdateRule(updatedRule); // 同步更新 ViewModel 内存状态
                rulesToSave.Add(updatedRule);
                
                // 核心分流机制：
                if (group.TierIndex == 1) 
                {
                    // Ignored 组：完全放行，让 Windows 原生接管启动
                    hijacker.EnableItem(vm.Item);
                }
                else if (group.TierIndex == 0)
                {
                    // Disabled 组：彻底禁用
                    hijacker.DisableItem(vm.Item);
                }
                else
                {
                    // T1+ 托管组：由大管家调度拉起，因此必须在系统层禁用。
                    // 唯一例外：要求静默的 UWP 我们将其放行，交由 Windows BAM 完美后台唤醒。
                    if (vm.Item.Sources.Contains(StartupSource.UwpApp) && updatedRule.IsSilent)
                        hijacker.EnableItem(vm.Item);
                    else
                        hijacker.DisableItem(vm.Item);
                }
            }
        }
        _configService.SaveRules(rulesToSave);

        // 保存 Tier 延迟和顺序模式配置到独立 JSON 文件
        try 
        { 
            File.WriteAllText(_tierConfigsPath, JsonSerializer.Serialize(tierConfigs)); 
        } 
        catch { }

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

    public async Task ApplyTiersManagementAsync(IEnumerable<EditableTier> modifiedTiers)
    {
        var newTiersList = modifiedTiers.ToList();
        
        var newAvailableTiers = new List<string> { AvailableTiers[0], AvailableTiers[1] }; 
        foreach (var tier in newTiersList)
        {
            newAvailableTiers.Add(tier.Name);
        }

        var oldToNewIndexMap = new Dictionary<int, int>();
        for (int i = 2; i < AvailableTiers.Count; i++)
        {
            var found = newTiersList.FindIndex(t => t.OriginalIndex == i);
            if (found >= 0)
            {
                oldToNewIndexMap[i] = found + 2; 
            }
            else
            {
                oldToNewIndexMap[i] = 0; 
            }
        }

        // 1. 同步更新所有关联 Apps 的排队层级并进行保存
        var rulesToSave = new List<ScheduleRule>();
        foreach (var group in TierGroups)
        {
            foreach (var vm in group)
            {
                var newRule = vm.Rule;
                if (newRule.PriorityLevel > 0)
                {
                    if (oldToNewIndexMap.TryGetValue(newRule.PriorityLevel, out int newIdx))
                    {
                        newRule = newRule with { PriorityLevel = newIdx };
                    }
                    else
                    {
                        newRule = newRule with { PriorityLevel = 0 }; // 已被删除的队列，将其包含的应用退回到 Disabled 保护伞
                    }
                }
                rulesToSave.Add(newRule);
            }
        }
        _configService.SaveRules(rulesToSave);

        // 2. 映射重排 Tier 的配置文件 (解决拖动后 60s/120s 延迟未随之移动的问题)
        Dictionary<string, TierConfig>? tierConfigs = null;
        try
        {
            if (File.Exists(_tierConfigsPath))
            {
                string json = File.ReadAllText(_tierConfigsPath);
                if (!string.IsNullOrWhiteSpace(json))
                {
                    tierConfigs = JsonSerializer.Deserialize<Dictionary<string, TierConfig>>(json);
                }
            }
        }
        catch { }

        if (tierConfigs != null)
        {
            var newTierConfigs = new Dictionary<string, TierConfig>();
            foreach (var kvp in oldToNewIndexMap)
            {
                if (kvp.Value > 0 && tierConfigs.TryGetValue(kvp.Key.ToString(), out var config))
                {
                    newTierConfigs[kvp.Value.ToString()] = config;
                }
            }
            try { File.WriteAllText(_tierConfigsPath, JsonSerializer.Serialize(newTierConfigs)); } catch { }
        }

        // 3. 永久保存 Tier 名字与排布
        AvailableTiers.Clear();
        foreach (var t in newAvailableTiers)
        {
            AvailableTiers.Add(t);
        }
        _configService.SaveTiers(AvailableTiers);

        // 4. 从硬盘加载最新数据重新构建 UI
        await LoadItemsAsync();
        
        // 5. 将崭新的顺序注入注册表与 Orchestrator 管家引擎
        SaveConfiguration();
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

    // 核心定位逻辑：针对多路注册的情况，精确打开指定的来源位置
    public void OpenSpecificLocation(AppItem item, StartupSource source)
    {
        try
        {
            if (source == StartupSource.StartupFolder || 
                source == StartupSource.CommonStartupFolder)
            {
                if (File.Exists(item.FilePath))
                {
                    System.Diagnostics.Process.Start(
                        "explorer.exe", $"/select,\"{item.FilePath}\"");
                }
                else
                {
                    System.Diagnostics.Process.Start("explorer.exe", Environment.GetFolderPath(source == StartupSource.StartupFolder ? Environment.SpecialFolder.Startup : Environment.SpecialFolder.CommonStartup));
                }
            }
            else if (source == StartupSource.UwpApp)
            {
                // 唤起 Win11 自带的启动项管理页面，UWP 可以在那里检查
                var info = new System.Diagnostics.ProcessStartInfo(
                    "ms-settings:startupapps") { UseShellExecute = true };
                System.Diagnostics.Process.Start(info);
            }
            else
            {
                // 黑客技巧：写入 LastKey 让注册表编辑器打开时自动跳转
                string keyPath = GetRegistryKeyString(source);
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