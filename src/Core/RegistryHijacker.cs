using System;
using Microsoft.Win32;
using StartFlow.Models;

namespace StartFlow.Core;

public class RegistryHijacker
{
    // 禁用状态的二进制特征 (0x03 开头表示禁用)
    private static readonly byte[] DisabledValue =
    {
        0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    // 启用状态的二进制特征 (0x02 开头表示启用)
    private static readonly byte[] EnabledValue =
    {
        0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
    };

    public bool DisableItem(AppItem item)
    {
        bool success = ModifyStartupApprovedKey(item, DisabledValue);
        // UWP 需要“双管齐下”：既修改沙盒状态，又修改 StartupApproved 供任务管理器显示
        if (item.Source == StartupSource.UwpApp) success |= ModifyUwpState(item, 1); // State 1 = Disabled
        return success;
    }

    public bool EnableItem(AppItem item)
    {
        bool success = ModifyStartupApprovedKey(item, EnabledValue);
        if (item.Source == StartupSource.UwpApp) success |= ModifyUwpState(item, 2); // State 2 = Enabled
        return success;
    }

    // 核心副作用函数：修改注册表键值
    private bool ModifyStartupApprovedKey(AppItem item, byte[] targetValue)
    {
        var (rootKey, subKeyPath) = GetStartupApprovedPath(item.Source);
        if (rootKey == null || subKeyPath == null)
        {
            return false;
        }

        // UWP 在任务管理器 StartupApproved 里的键名规则是 FamilyName!TaskId
        string valueName = item.Name;
        if (item.Source == StartupSource.UwpApp)
        {
            valueName = item.Arguments.Replace('|', '!');
        }

        try
        {
            // true 表示我们需要写入权限
            using RegistryKey? key = rootKey.CreateSubKey(subKeyPath, true);
            if (key != null)
            {
                key.SetValue(valueName, targetValue, RegistryValueKind.Binary);
                return true;
            }
        }
        catch (Exception)
        {
            // 如果没有管理员权限导致 HKLM 写入失败，自动降级为 HKCU 覆盖写入
            if (rootKey == Registry.LocalMachine)
            {
                try
                {
                    using RegistryKey? fallbackKey = Registry.CurrentUser.CreateSubKey(subKeyPath, true);
                    if (fallbackKey != null)
                    {
                        fallbackKey.SetValue(valueName, targetValue, RegistryValueKind.Binary);
                        return true;
                    }
                }
                catch { }
            }
            return false;
        }

        return false;
    }

    // 核心副作用：直接穿透修改 UWP 的 SystemAppData 沙盒自启状态
    private bool ModifyUwpState(AppItem item, int state)
    {
        try
        {
            var parts = item.Arguments.Split('|');
            if (parts.Length >= 2)
            {
                string familyName = parts[0];
                string taskId = parts[1];
                // 修复核心 Bug：移除路径末尾多余的 \State 文件夹，因为 State 是一个值(Value)而不是子键(Key)
                string path = $@"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\SystemAppData\{familyName}\{taskId}";
                
                using var key = Registry.CurrentUser.CreateSubKey(path, true);
                // State: 1 = Disabled, 2 = Enabled
                key?.SetValue("State", state, RegistryValueKind.DWord);
                return true;
            }
        }
        catch { }
        return false;
    }

    // 纯函数：根据启动源路由到对应的 StartupApproved 注册表路径
    private (RegistryKey? Root, string? Path) GetStartupApprovedPath(
        StartupSource source)
    {
        const string runPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        const string run32Path =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
        const string folderPath =
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

        return source switch
        {
            StartupSource.RegistryCurrentUser => 
                (Registry.CurrentUser, runPath),
            StartupSource.RegistryLocalMachine => 
                (Registry.LocalMachine, runPath),
            StartupSource.RegistryLocalMachineWow64 => 
                (Registry.LocalMachine, run32Path),
            StartupSource.StartupFolder => 
                (Registry.CurrentUser, folderPath),
            StartupSource.CommonStartupFolder => 
                (Registry.LocalMachine, folderPath),
            StartupSource.RegistryCurrentUserRunOnce => 
                (Registry.CurrentUser, runPath),
            StartupSource.RegistryLocalMachineRunOnce => 
                (Registry.LocalMachine, runPath),
            StartupSource.RegistryGhostItem => 
                (Registry.CurrentUser, runPath),
            StartupSource.UwpApp => 
                (Registry.CurrentUser, runPath),
            _ => (null, null)
        };
    }
}