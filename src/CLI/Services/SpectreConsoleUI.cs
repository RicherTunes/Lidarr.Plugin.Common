using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Spectre.Console;

namespace Lidarr.Plugin.Common.CLI.UI
{
    /// <summary>
    /// Rich console UI implementation using Spectre.Console
    /// Provides professional CLI experience with colors, tables, progress bars, etc.
    /// </summary>
    public class SpectreConsoleUI : IConsoleUI
    {
        public void WriteMarkup(string markup)
        {
            AnsiConsole.Markup(markup);
        }

        public void WriteMarkupLine(string markup)
        {
            AnsiConsole.MarkupLine(markup);
        }

        public void Write(string text)
        {
            AnsiConsole.Write(text);
        }

        public void WriteLine(string text = "")
        {
            AnsiConsole.WriteLine(text);
        }

        public void WriteError(string message)
        {
            AnsiConsole.MarkupLine($"[red]❌ Error:[/] {message.EscapeMarkup()}");
        }

        public void WriteWarning(string message)
        {
            AnsiConsole.MarkupLine($"[yellow]⚠️  Warning:[/] {message.EscapeMarkup()}");
        }

        public void WriteSuccess(string message)
        {
            AnsiConsole.MarkupLine($"[green]✅ Success:[/] {message.EscapeMarkup()}");
        }

        public string Ask(string prompt)
        {
            return AnsiConsole.Ask<string>(prompt);
        }

        public string AskPassword(string prompt)
        {
            return AnsiConsole.Prompt(
                new TextPrompt<string>(prompt)
                    .Secret()
            );
        }

        public bool Confirm(string prompt, bool defaultValue = false)
        {
            return AnsiConsole.Confirm(prompt, defaultValue);
        }

        public T Select<T>(string prompt, IEnumerable<T> choices, Func<T, string> displaySelector = null)
        {
            // Avoid Spectre.Console generic constraint (T : notnull) by projecting to strings and mapping back.
            displaySelector ??= (item => item?.ToString() ?? string.Empty);
            var list = choices?.ToList() ?? new List<T>();
            var labels = list.Select((item, i) => $"{i}: {displaySelector(item)}").ToList();

            var selection = new SelectionPrompt<string>()
                .Title(prompt)
                .AddChoices(labels);

            var chosen = AnsiConsole.Prompt(selection);
            var idxStr = chosen.Split(':')[0];
            if (int.TryParse(idxStr, out var idx) && idx >= 0 && idx < list.Count)
            {
                return list[idx];
            }
            // Fallback: first or default
            return list.Count > 0 ? list[0] : default;
        }

        public IEnumerable<T> MultiSelect<T>(string prompt, IEnumerable<T> choices, Func<T, string> displaySelector = null)
        {
            displaySelector ??= (item => item?.ToString() ?? string.Empty);
            var list = choices?.ToList() ?? new List<T>();
            var labels = list.Select((item, i) => $"{i}: {displaySelector(item)}").ToList();

            var selection = new MultiSelectionPrompt<string>()
                .Title(prompt)
                .AddChoices(labels)
                .InstructionsText("[grey](Press [blue]<space>[/] to toggle, [green]<enter>[/] to accept)[/]");

            var chosen = AnsiConsole.Prompt(selection);
            var indices = chosen.Select(c => c.Split(':')[0]).Select(s => int.TryParse(s, out var i) ? i : -1)
                .Where(i => i >= 0 && i < list.Count)
                .ToList();

            return indices.Select(i => list[i]).ToList();
        }

        public void ShowTable<T>(IEnumerable<T> data, params (string header, Func<T, string> getValue)[] columns)
        {
            var table = new Table();
            table.BorderColor(Color.Grey);

            // Add columns
            foreach (var (header, _) in columns)
            {
                table.AddColumn(header);
            }

            // Add rows
            foreach (var item in data)
            {
                var row = columns.Select(col => col.getValue(item).EscapeMarkup()).ToArray();
                table.AddRow(row);
            }

            AnsiConsole.Write(table);
        }

        public async Task<T> ShowProgressAsync<T>(string taskDescription, Func<IProgress<ProgressInfo>, Task<T>> operation)
        {
            return await AnsiConsole.Progress()
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask(taskDescription);
                    
                    var progress = new Progress<ProgressInfo>(info =>
                    {
                        task.Value = info.Percentage;
                        if (!string.IsNullOrEmpty(info.CurrentTask))
                        {
                            task.Description = info.CurrentTask;
                        }
                    });

                    var result = await operation(progress);
                    task.Value = 100;
                    return result;
                });
        }

        public void ShowStatus(string title, Dictionary<string, object> data)
        {
            var panel = new Panel(CreateStatusTable(data))
                .Header($"[bold]{title.EscapeMarkup()}[/]")
                .BorderColor(Color.Blue);

            AnsiConsole.Write(panel);
        }

        public void Clear()
        {
            AnsiConsole.Clear();
        }

        private Table CreateStatusTable(Dictionary<string, object> data)
        {
            var table = new Table()
                .HideHeaders()
                .Border(TableBorder.None);

            table.AddColumn("Key");
            table.AddColumn("Value");

            foreach (var (key, value) in data)
            {
                var displayValue = value switch
                {
                    bool b => b ? "[green]✓[/]" : "[red]✗[/]",
                    null => "[grey]N/A[/]",
                    _ => value.ToString().EscapeMarkup()
                };

                table.AddRow($"[bold]{key}:[/]", displayValue);
            }

            return table;
        }
    }
}
