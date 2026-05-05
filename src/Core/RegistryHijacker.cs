using System;
using Microsoft.Win32;
using StartFlow.Models;

namespace StartFlow.Core;

public class RegistryHijacker
{
    // 动态生成禁用状态二进制特征 (附带当前有效的时间戳，防止被系统或驱动视为损坏)
    private static byte[] GetDisabledValue()
    {
        byte[] val = new byte[12];
        val[0] = 0x03;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTime()).CopyTo(val, 4);
        return val;
    }

    // 动态生成启用状态二进制特征
    private static byte[] GetEnabledValue()
    {
        byte[] val = new byte[12];
        val[0] = 0x02;
        BitConverter.GetBytes(DateTime.UtcNow.ToFileTime()).CopyTo(val, 4);
        return val;
    }

    public bool DisableItem(AppItem item)
    {
        bool success = ModifyStartupApprovedKey(item, GetDisabledValue());
        // UWP 需要“双管齐下”：既修改沙盒状态，又修改 StartupApproved 供任务管理器显示
        if (item.Source == StartupSource.UwpApp) success |= ModifyUwpState(item, 1); // State 1 = Disabled
        return success;
    }

    public bool EnableItem(AppItem item)
    {
        bool success = ModifyStartupApprovedKey(item, GetEnabledValue());
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
        else if (item.Source == StartupSource.StartupFolder || item.Source == StartupSource.CommonStartupFolder)
        {
            // 启动文件夹必须匹配带后缀的完整文件名（如 .lnk），否则任务管理器无法建立映射
            if (!string.IsNullOrEmpty(item.FilePath))
            {
                valueName = System.IO.Path.GetFileName(item.FilePath);
            }
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