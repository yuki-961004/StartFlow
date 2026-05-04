using System;
using Microsoft.Win32;
using Xunit;
using Xunit.Abstractions;
using StartFlow.Core;
using StartFlow.Models;

namespace StartFlow.Tests;

public class RegistryHijackerTests
{
    private readonly ITestOutputHelper _output;

    public RegistryHijackerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void DisableAndEnable_ShouldModifyRegistrySuccessfully()
    {
        // 1. 创建一个虚构的启动项，避免误伤你真实的系统配置
        var dummyItem = AppItem.Create(
            "StartFlow_Dummy_Test_Item",
            "C:\\dummy.exe",
            "",
            StartupSource.RegistryCurrentUser); // 使用 HKCU 避免需要管理员权限

        var hijacker = new RegistryHijacker();

        // 2. 测试禁用 (写入 0x03 开头的二进制)
        bool disableResult = hijacker.DisableItem(dummyItem);
        _output.WriteLine($"禁用虚拟启动项结果: {disableResult}");
        Assert.True(disableResult, "禁用操作应该成功返回 true");

        // 3. 测试恢复 (写入 0x02 开头的二进制)
        bool enableResult = hijacker.EnableItem(dummyItem);
        _output.WriteLine($"启用虚拟启动项结果: {enableResult}");
        Assert.True(enableResult, "启用操作应该成功返回 true");

        // 4. 清理无用的测试数据，保持系统纯洁 (无副作用)
        string path = 
            @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
        
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path, true);
        key?.DeleteValue(dummyItem.Name, false);
    }
}