using System.Linq;
using Xunit;
using Xunit.Abstractions;
using StartFlow.Core;

namespace StartFlow.Tests;

public class StartupItemScannerTests
{
    // 用于在 xUnit 测试中向控制台输出日志
    private readonly ITestOutputHelper _output;

    public StartupItemScannerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void ScanAll_ShouldReturnSystemStartupItems()
    {
        // 1. 准备并执行
        var scanner = new StartupItemScanner();
        var items = scanner.ScanAll().ToList();

        // 2. 断言验证
        Assert.NotNull(items);
        
        // 3. 打印结果以便我们肉眼核对是否正确
        _output.WriteLine($"总共扫描到启动项数量: {items.Count}");
        
        foreach (var item in items)
        {
            _output.WriteLine(
                $"[{item.Source}] 名字: {item.Name} | " +
                $"路径: {item.FilePath} | 参数: {item.Arguments}");
        }
    }
}