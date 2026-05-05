using System;
using System.Security.Cryptography;
using System.Text;

namespace StartFlow.Models;

// 标记启动项的来源，方便后续溯源
public enum StartupSource
{
    RegistryCurrentUser,
    RegistryLocalMachine,
    RegistryLocalMachineWow64,
    StartupFolder,
    CommonStartupFolder,
    RegistryCurrentUserRunOnce,
    RegistryLocalMachineRunOnce,
    RegistryGhostItem,
    UwpApp
}

public record AppItem(
    Guid Id,
    string Name,
    string FilePath,
    string Arguments,
    StartupSource[] Sources
)
{
    // 为了尽可能少地破坏老代码，提供一个便捷的首选来源属性
    public StartupSource Source => Sources.Length > 0 ? Sources[0] : StartupSource.RegistryCurrentUser;

    // 提供一个纯函数工厂方法来创建实例，避免外部手动生成 Guid
    public static AppItem Create(
        string name, 
        string filePath, 
        string arguments, 
        params StartupSource[] sources)
    {
        // 利用名称和路径生成固定的特征哈希，保证重启后能与配置文件精准匹配
        string idStr = $"{name}|{filePath}".ToLowerInvariant();
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(idStr));
        Guid fixedId = new Guid(hash);

        return new AppItem(
            fixedId,
            name,
            filePath,
            arguments,
            sources.Length > 0 ? sources : new[] { StartupSource.RegistryCurrentUser }
        );
    }
}
