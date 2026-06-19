using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using StartFlow.Models;

namespace StartFlow.Core;

public class ScheduledTaskStartupScanner
{
    public IEnumerable<AppItem> ScanAll()
    {
        string csv = ReadScheduledTasksCsv();
        if (string.IsNullOrWhiteSpace(csv))
        {
            return Array.Empty<AppItem>();
        }

        return ParseScheduledTasks(csv);
    }

    private string ReadScheduledTasksCsv()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = "/query /fo CSV /v",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };

            using Process? process = Process.Start(startInfo);
            if (process == null)
            {
                return string.Empty;
            }

            string output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(8000);

            if (!process.HasExited || process.ExitCode != 0)
            {
                return string.Empty;
            }

            return output;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private IEnumerable<AppItem> ParseScheduledTasks(string csv)
    {
        var items = new List<AppItem>();
        List<string[]> rows = ParseCsvRows(csv);

        foreach (string[] row in rows)
        {
            Dictionary<string, string> values = MapRow(row);
            if (values.Count == 0)
            {
                continue;
            }

            if (!IsEnabledLogonTask(values))
            {
                continue;
            }

            string command = values.GetValueOrDefault(
                "Task To Run",
                string.Empty);
            string startIn = values.GetValueOrDefault("Start In", string.Empty);
            if (!TryParseCommand(
                    command,
                    startIn,
                    out string path,
                    out string args))
            {
                continue;
            }

            string taskName = values.GetValueOrDefault(
                "TaskName",
                string.Empty);
            string name = GetDisplayName(taskName, path);
            items.Add(AppItem.Create(
                name,
                path,
                args,
                StartupSource.ScheduledTask));
        }

        return items;
    }

    private bool IsEnabledLogonTask(Dictionary<string, string> values)
    {
        string state = values.GetValueOrDefault(
            "Scheduled Task State",
            string.Empty);
        string scheduleType = values.GetValueOrDefault(
            "Schedule Type",
            string.Empty);
        string command = values.GetValueOrDefault("Task To Run", string.Empty);

        return state.Equals("Enabled", StringComparison.OrdinalIgnoreCase) &&
               scheduleType.Contains(
                   "At logon",
                   StringComparison.OrdinalIgnoreCase) &&
               !command.Equals(
                   "COM handler",
                   StringComparison.OrdinalIgnoreCase);
    }

    private string GetDisplayName(string taskName, string path)
    {
        string trimmedName = taskName.Trim('\\');
        int slashIndex = trimmedName.LastIndexOf('\\');
        if (slashIndex >= 0 && slashIndex < trimmedName.Length - 1)
        {
            trimmedName = trimmedName[(slashIndex + 1)..];
        }

        if (!string.IsNullOrWhiteSpace(trimmedName))
        {
            return trimmedName;
        }

        return Path.GetFileNameWithoutExtension(path);
    }

    private bool TryParseCommand(
        string command,
        string startIn,
        out string path,
        out string args)
    {
        path = string.Empty;
        args = string.Empty;

        command = command.Trim();
        if (string.IsNullOrWhiteSpace(command) ||
            command.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (command.StartsWith("\"", StringComparison.Ordinal))
        {
            int endQuote = command.IndexOf('"', 1);
            if (endQuote <= 0)
            {
                return false;
            }

            path = command[1..endQuote];
            args = command[(endQuote + 1)..].Trim();
        }
        else
        {
            int exeIndex = command.IndexOf(
                ".exe",
                StringComparison.OrdinalIgnoreCase);
            if (exeIndex < 0)
            {
                return false;
            }

            path = command[..(exeIndex + 4)].Trim();
            args = command[(exeIndex + 4)..].Trim();
        }

        if (!Path.IsPathRooted(path) && !string.IsNullOrWhiteSpace(startIn))
        {
            path = Path.Combine(startIn.Trim('"', ' '), path);
        }

        return File.Exists(path);
    }

    private Dictionary<string, string> MapRow(string[] row)
    {
        if (row.Length == 0 || row[0] == "HostName")
        {
            return new Dictionary<string, string>();
        }

        string[] headers =
        {
            "HostName", "TaskName", "Next Run Time", "Status", "Logon Mode",
            "Last Run Time", "Last Result", "Author", "Task To Run",
            "Start In", "Comment", "Scheduled Task State", "Idle Time",
            "Power Management", "Run As User", "Delete Task If Not Rescheduled",
            "Stop Task If Runs X Hours and X Mins", "Schedule",
            "Schedule Type"
        };

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        int count = Math.Min(headers.Length, row.Length);
        for (int index = 0; index < count; index++)
        {
            values[headers[index]] = row[index];
        }

        return values;
    }

    private List<string[]> ParseCsvRows(string csv)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var field = new StringBuilder();
        bool inQuotes = false;

        for (int index = 0; index < csv.Length; index++)
        {
            char current = csv[index];
            if (current == '"')
            {
                bool escapedQuote =
                    inQuotes &&
                    index + 1 < csv.Length &&
                    csv[index + 1] == '"';
                if (escapedQuote)
                {
                    field.Append('"');
                    index++;
                }
                else
                {
                    inQuotes = !inQuotes;
                }
            }
            else if (current == ',' && !inQuotes)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if ((current == '\r' || current == '\n') && !inQuotes)
            {
                AddRow(rows, fields, field);
                if (current == '\r' &&
                    index + 1 < csv.Length &&
                    csv[index + 1] == '\n')
                {
                    index++;
                }
            }
            else
            {
                field.Append(current);
            }
        }

        AddRow(rows, fields, field);
        return rows;
    }

    private void AddRow(
        List<string[]> rows,
        List<string> fields,
        StringBuilder field)
    {
        if (field.Length == 0 && fields.Count == 0)
        {
            return;
        }

        fields.Add(field.ToString());
        rows.Add(fields.ToArray());
        fields.Clear();
        field.Clear();
    }
}
