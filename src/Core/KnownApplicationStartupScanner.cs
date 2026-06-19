using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using StartFlow.Models;

namespace StartFlow.Core;

public class KnownApplicationStartupScanner
{
    public IEnumerable<AppItem> ScanAll()
    {
        var items = new List<AppItem>();

        AppItem? clashVerge = ScanClashVerge();
        if (clashVerge != null)
        {
            items.Add(clashVerge);
        }

        AppItem? powerToys = ScanPowerToys();
        if (powerToys != null)
        {
            items.Add(powerToys);
        }

        return items;
    }

    private AppItem? ScanClashVerge()
    {
        string appData = Environment.GetFolderPath(
            Environment.SpecialFolder.ApplicationData);
        string configPath = Path.Combine(
            appData,
            "io.github.clash-verge-rev.clash-verge-rev",
            "verge.yaml");
        string executablePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Clash Verge",
            "clash-verge.exe");

        if (!File.Exists(configPath) || !File.Exists(executablePath))
        {
            return null;
        }

        string[] lines = File.ReadAllLines(configPath);
        bool autoLaunchEnabled = false;

        foreach (string line in lines)
        {
            string trimmedLine = line.Trim();
            if (trimmedLine.Equals(
                "enable_auto_launch: true",
                StringComparison.OrdinalIgnoreCase))
            {
                autoLaunchEnabled = true;
                break;
            }
        }

        if (!autoLaunchEnabled)
        {
            return null;
        }

        return AppItem.Create(
            "clash-verge",
            executablePath,
            string.Empty,
            StartupSource.ApplicationSetting);
    }

    private AppItem? ScanPowerToys()
    {
        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData);
        string settingsPath = Path.Combine(
            localAppData,
            "Microsoft",
            "PowerToys",
            "settings.json");
        string executablePath = Path.Combine(
            localAppData,
            "PowerToys",
            "PowerToys.exe");

        if (!File.Exists(settingsPath) || !File.Exists(executablePath))
        {
            return null;
        }

        bool startupEnabled = false;

        try
        {
            using FileStream settingsStream = File.OpenRead(settingsPath);
            using JsonDocument document = JsonDocument.Parse(settingsStream);
            if (document.RootElement.TryGetProperty(
                    "startup",
                    out JsonElement startupElement) &&
                startupElement.ValueKind == JsonValueKind.True)
            {
                startupEnabled = true;
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        if (!startupEnabled)
        {
            return null;
        }

        return AppItem.Create(
            "PowerToys",
            executablePath,
            string.Empty,
            StartupSource.ApplicationSetting);
    }
}
