using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Windows.Management.Deployment;
using StartFlow.Models;

namespace StartFlow.Core;

public class UwpScanner
{
    public IEnumerable<AppItem> ScanAllUwpStartupTasks()
    {
        var items = new List<AppItem>();
        var packageManager = new PackageManager();
        var packages = packageManager.FindPackagesForUser(string.Empty);

        foreach (var package in packages)
        {
            var uwpItem = TryParseUwpStartup(package);
            if (uwpItem != null)
            {
                items.Add(uwpItem);
            }
        }
        return items;
    }

    private AppItem? TryParseUwpStartup(Windows.ApplicationModel.Package pkg)
    {
        try
        {
            string manifestPath = Path.Combine(
                pkg.InstalledLocation.Path, "AppxManifest.xml");

            if (!File.Exists(manifestPath)) return null;

            // 快速过滤：如果清单文件里连启动任务关键字都没有，直接跳过以提升性能
            string xmlContent = File.ReadAllText(manifestPath);
            if (!xmlContent.Contains("windows.startupTask")) return null;

            XDocument doc = XDocument.Parse(xmlContent);
            XNamespace ns = doc.Root?.GetDefaultNamespace() ?? string.Empty;

            // 寻找包含 windows.startupTask 扩展的 Application 节点
            var appNode = doc.Descendants(ns + "Application").FirstOrDefault(a => 
                a.Descendants().Any(e => e.Name.LocalName == "Extension" && 
                e.Attribute("Category")?.Value == "windows.startupTask"));

            if (appNode != null)
            {
                string appId = appNode.Attribute("Id")?.Value ?? "App";
                
                // 深入提取 UWP 专属的 StartupTask ID，这是禁用它的唯一钥匙
                var extNode = appNode.Descendants().FirstOrDefault(e => 
                    e.Name.LocalName == "Extension" && 
                    e.Attribute("Category")?.Value == "windows.startupTask");
                    
                var taskNode = extNode?.Descendants().FirstOrDefault(e => 
                    e.Name.LocalName == "StartupTask");
                    
                string taskId = taskNode?.Attribute("TaskId")?.Value ?? appId;

                // UWP 应用通过 AUMID (FamilyName!AppId) 进行底层唯一标识和启动
                string aumid = $"{pkg.Id.FamilyName}!{appId}";
                // 将家族名和 TaskId 打包进 Arguments，供 Hijacker 禁用时使用
                string uwpArgs = $"{pkg.Id.FamilyName}|{taskId}";
                
                return AppItem.Create(
                    pkg.DisplayName ?? pkg.Id.Name,
                    aumid, // 将 AUMID 存在 FilePath 字段，方便后续调度拉起
                    uwpArgs, 
                    StartupSource.UwpApp);
            }
        }
        catch (Exception)
        {
            // 遇到系统沙盒保护严格、无法读取目录的系统包，直接安全忽略
        }
        
        return null;
    }
}