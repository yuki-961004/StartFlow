using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Principal;
using Microsoft.Win32;
using StartFlow.Models;

namespace StartFlow.Core;

public class StartupItemScanner
{
    // 注册表 Run 键的通用路径
    private const string RunKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunWow64KeyPath =
        @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOnceKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    private const string ApprovedRunPath =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ApprovedFolder =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public IEnumerable<AppItem> ScanAll()
    {
        var rawItems = new List<AppItem>();

        // 1. 常规 Run 键和 RunOnce 键
        rawItems.AddRange(ScanRegistry(
            Registry.CurrentUser,
            RunKeyPath,
            StartupSource.RegistryCurrentUser));

        rawItems.AddRange(ScanCurrentUserSidRunKey());

        rawItems.AddRange(ScanRegistry(
            Registry.CurrentUser,
            RunOnceKeyPath,
            StartupSource.RegistryCurrentUserRunOnce));

        rawItems.AddRange(ScanRegistry(
            Registry.LocalMachine,
            RunKeyPath,
            StartupSource.RegistryLocalMachine));

        rawItems.AddRange(ScanRegistry(
            Registry.LocalMachine,
            RunOnceKeyPath,
            StartupSource.RegistryLocalMachineRunOnce));

        rawItems.AddRange(ScanRegistry(
            Registry.LocalMachine,
            RunWow64KeyPath,
            StartupSource.RegistryLocalMachineWow64));

        // 2. 启动文件夹
        rawItems.AddRange(ScanStartupFolder(
            Environment.SpecialFolder.Startup,
            StartupSource.StartupFolder));

        rawItems.AddRange(ScanStartupFolder(
            Environment.SpecialFolder.CommonStartup,
            StartupSource.CommonStartupFolder));

        // 3. 专属扫描器：获取现代 UWP / 微软商店应用
        var uwpScanner = new UwpScanner();
        rawItems.AddRange(uwpScanner.ScanAllUwpStartupTasks());

        var knownApplicationScanner = new KnownApplicationStartupScanner();
        rawItems.AddRange(knownApplicationScanner.ScanAll());

        var scheduledTaskScanner = new ScheduledTaskStartupScanner();
        rawItems.AddRange(scheduledTaskScanner.ScanAll());

        // 4. [核心优化] 合并去重：如果发现同一个程序在多处注册了启动项，将其所有源合并为一个单一程序卡片
        var mergedItems = new List<AppItem>();
        var grouped = rawItems.GroupBy(GetMergeKey);

        foreach (var g in grouped)
        {
            var first = g
                .OrderBy(x => GetSourceRank(x.Source))
                .First();
            var combinedSources = g.SelectMany(x => x.Sources).Distinct().ToArray();
            mergedItems.Add(AppItem.Create(first.Name, first.FilePath, first.Arguments, combinedSources));
        }

        // 5. 扫描 StartupApproved，捕获被安全软件暴力删除的遗留“幽灵”项
        var approvedItems = new List<AppItem>();
        approvedItems.AddRange(ScanStartupApproved(
            Registry.CurrentUser, ApprovedRunPath));
        approvedItems.AddRange(ScanStartupApproved(
            Registry.LocalMachine, ApprovedRunPath));

        // 6. 合并幽灵项：如果发现新名字，说明是残留在注册表中的历史幽灵项
        foreach (var approved in approvedItems)
        {
            // 兼容 UWP 的 TaskId 对比，防止重复扫描
            bool exists = mergedItems.Any(x => 
                x.Name.Equals(approved.Name, StringComparison.OrdinalIgnoreCase) ||
                x.FilePath.Equals(approved.Name, StringComparison.OrdinalIgnoreCase) ||
                (x.Source == StartupSource.UwpApp && 
                 (x.Arguments.Replace('|', '!').Equals(approved.Name, StringComparison.OrdinalIgnoreCase) ||
                  x.Arguments.Split('|').LastOrDefault()?.Equals(approved.Name, StringComparison.OrdinalIgnoreCase) == true)));
                
            if (!exists)
            {
                mergedItems.Add(approved);
            }
        }

        return mergedItems;
    }

    private IEnumerable<AppItem> ScanRegistry(
        RegistryKey rootKey,
        string subKeyPath,
        StartupSource source)
    {
        using RegistryKey? key = rootKey.OpenSubKey(subKeyPath, false);
        if (key == null)
        {
            return Enumerable.Empty<AppItem>();
        }

        string[] valueNames = key.GetValueNames();
        var items = new List<AppItem>();

        foreach (string name in valueNames)
        {
            object? value = key.GetValue(name);
            if (value is string command && !string.IsNullOrWhiteSpace(command))
            {
                var (path, args) = ParseCommand(command);
                items.Add(AppItem.Create(name, path, args, source));
            }
        }

        return items;
    }

    private string GetMergeKey(AppItem item)
    {
        string filePath = item.FilePath.Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(filePath) &&
            !filePath.Equals(
                "uwp_app_or_disabled_item",
                StringComparison.OrdinalIgnoreCase))
        {
            string fileName = Path.GetFileName(filePath);
            if (IsSharedLauncher(fileName))
            {
                string arguments = item.Arguments.Trim().ToLowerInvariant();
                return $"{filePath}|{arguments}";
            }

            return filePath;
        }

        return item.Name.Trim().ToLowerInvariant();
    }

    private bool IsSharedLauncher(string fileName)
    {
        return fileName.Equals("wscript.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cscript.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("powershell.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase);
    }

    private int GetSourceRank(StartupSource source)
    {
        return source switch
        {
            StartupSource.RegistryCurrentUser => 0,
            StartupSource.RegistryLocalMachine => 1,
            StartupSource.RegistryLocalMachineWow64 => 2,
            StartupSource.StartupFolder => 3,
            StartupSource.CommonStartupFolder => 4,
            StartupSource.ScheduledTask => 5,
            StartupSource.ApplicationSetting => 6,
            StartupSource.UwpApp => 7,
            StartupSource.RegistryGhostItem => 8,
            _ => 9
        };
    }

    private IEnumerable<AppItem> ScanCurrentUserSidRunKey()
    {
        try
        {
            string? userSid = WindowsIdentity.GetCurrent().User?.Value;
            if (string.IsNullOrWhiteSpace(userSid))
            {
                return Enumerable.Empty<AppItem>();
            }

            string subKeyPath = $@"{userSid}\{RunKeyPath}";
            return ScanRegistry(
                Registry.Users,
                subKeyPath,
                StartupSource.RegistryCurrentUser);
        }
        catch (Exception)
        {
            return Enumerable.Empty<AppItem>();
        }
    }

    private IEnumerable<AppItem> ScanStartupFolder(
        Environment.SpecialFolder folderType,
        StartupSource source)
    {
        string folderPath = Environment.GetFolderPath(folderType);

        if (!Directory.Exists(folderPath))
        {
            return Enumerable.Empty<AppItem>();
        }

        // 过滤掉 desktop.ini 等系统自动生成的隐藏配置文件
        string[] files = Directory.GetFiles(folderPath)
            .Where(f => !Path.GetFileName(f).Equals(
                "desktop.ini", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return files.Select(file => AppItem.Create(
            Path.GetFileNameWithoutExtension(file),
            file,
            string.Empty,
            source));
    }

    // 纯函数：扫描批准名单注册表键
    private IEnumerable<AppItem> ScanStartupApproved(
        RegistryKey rootKey, 
        string subKeyPath)
    {
        using RegistryKey? key = rootKey.OpenSubKey(subKeyPath, false);
        if (key == null)
        {
            return Enumerable.Empty<AppItem>();
        }

        string[] valueNames = key.GetValueNames();
        return valueNames.Select(name => AppItem.Create(
            name, 
            "UWP_App_Or_Disabled_Item", 
            string.Empty, 
            StartupSource.RegistryGhostItem));
    }

    // 纯函数：用于安全地拆分带有引号的执行路径与参数
    private (string FilePath, string Arguments) ParseCommand(string command)
    {
        command = command.Trim();

        if (command.StartsWith("\""))
        {
            int endQuote = command.IndexOf("\"", 1);
            return endQuote > 0
                ? (command[1..endQuote], command[(endQuote + 1)..].Trim())
                : (command, string.Empty);
        }

        // 处理没有引号保护的路径 (如 C:\...\NVIDIA Broadcast.exe -launch-hidden)
        int exeIndex = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exeIndex > 0)
        {
            string path = command[..(exeIndex + 4)].Trim();
            string args = command[(exeIndex + 4)..].Trim();
            return (path, args);
        }

        return (command, string.Empty);
    }
}
