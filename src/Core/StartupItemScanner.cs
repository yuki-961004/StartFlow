using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        var items = new List<AppItem>();

        // 1. 常规 Run 键和 RunOnce 键
        items.AddRange(ScanRegistry(
            Registry.CurrentUser,
            RunKeyPath,
            StartupSource.RegistryCurrentUser));

        items.AddRange(ScanRegistry(
            Registry.CurrentUser,
            RunOnceKeyPath,
            StartupSource.RegistryCurrentUserRunOnce));

        items.AddRange(ScanRegistry(
            Registry.LocalMachine,
            RunKeyPath,
            StartupSource.RegistryLocalMachine));

        items.AddRange(ScanRegistry(
            Registry.LocalMachine,
            RunOnceKeyPath,
            StartupSource.RegistryLocalMachineRunOnce));

        items.AddRange(ScanRegistry(
            Registry.LocalMachine,
            RunWow64KeyPath,
            StartupSource.RegistryLocalMachineWow64));

        // 2. 启动文件夹
        items.AddRange(ScanStartupFolder(
            Environment.SpecialFolder.Startup,
            StartupSource.StartupFolder));

        items.AddRange(ScanStartupFolder(
            Environment.SpecialFolder.CommonStartup,
            StartupSource.CommonStartupFolder));

        // 3. 专属扫描器：获取现代 UWP / 微软商店应用
        var uwpScanner = new UwpScanner();
        items.AddRange(uwpScanner.ScanAllUwpStartupTasks());

        // 4. 扫描 StartupApproved，捕获被安全软件暴力删除的遗留“幽灵”项
        var approvedItems = new List<AppItem>();
        approvedItems.AddRange(ScanStartupApproved(
            Registry.CurrentUser, ApprovedRunPath));
        approvedItems.AddRange(ScanStartupApproved(
            Registry.LocalMachine, ApprovedRunPath));
        approvedItems.AddRange(ScanStartupApproved(
            Registry.CurrentUser, ApprovedFolder));
        approvedItems.AddRange(ScanStartupApproved(
            Registry.LocalMachine, ApprovedFolder));

        // 5. 合并去重：如果发现新名字，说明是残留在注册表中的历史幽灵项
        foreach (var approved in approvedItems)
        {
            // 兼容 UWP 的 TaskId 对比，防止重复扫描
            bool exists = items.Any(x => 
                x.Name.Equals(approved.Name, StringComparison.OrdinalIgnoreCase) ||
                x.FilePath.Equals(approved.Name, StringComparison.OrdinalIgnoreCase) ||
                (x.Source == StartupSource.UwpApp && 
                 (x.Arguments.Replace('|', '!').Equals(approved.Name, StringComparison.OrdinalIgnoreCase) ||
                  x.Arguments.Split('|').LastOrDefault()?.Equals(approved.Name, StringComparison.OrdinalIgnoreCase) == true)));
                
            if (!exists)
            {
                items.Add(approved);
            }
        }

        return items;
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