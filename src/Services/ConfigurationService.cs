using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StartFlow.Models;

namespace StartFlow.Services;

public class ConfigurationService
{
    private readonly string _configPath;
    private readonly string _tiersPath;

    public ConfigurationService()
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        string appFolder = Path.Combine(appData, "StartFlow");
        Directory.CreateDirectory(appFolder);
        _configPath = Path.Combine(appFolder, "rules.json");
        _tiersPath = Path.Combine(appFolder, "tiers.json");
    }

    public void SaveRules(IEnumerable<ScheduleRule> rules)
    {
        string json = JsonSerializer.Serialize(rules);
        File.WriteAllText(_configPath, json);
    }

    public List<ScheduleRule> LoadRules()
    {
        if (!File.Exists(_configPath)) return new List<ScheduleRule>();
        try
        {
            string json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<List<ScheduleRule>>(json) 
                   ?? new List<ScheduleRule>();
        }
        catch { return new List<ScheduleRule>(); }
    }

    public void SaveTiers(IEnumerable<string> tiers)
    {
        string json = JsonSerializer.Serialize(tiers);
        File.WriteAllText(_tiersPath, json);
    }

    public List<string> LoadTiers()
    {
        if (!File.Exists(_tiersPath)) return GetDefaultTiers();
        try
        {
            string json = File.ReadAllText(_tiersPath);
            return JsonSerializer.Deserialize<List<string>>(json) 
                   ?? GetDefaultTiers();
        }
        catch { return GetDefaultTiers(); }
    }

    private List<string> GetDefaultTiers()
    {
        return new List<string> 
        { 
            "Disabled", "T1 (Fast)", "T2 (Normal)", "T3 (Delay)", "T4 (Late)" 
        };
    }
}