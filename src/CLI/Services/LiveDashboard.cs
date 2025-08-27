using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.CLI.UI;
using Lidarr.Plugin.Common.CLI.Models;
using Spectre.Console;
using System.Linq;

namespace Lidarr.Plugin.Common.CLI.Services
{
    /// <summary>
    /// Live dashboard implementation using Spectre.Console
    /// Provides real-time monitoring of downloads and system status
    /// </summary>
    public class LiveDashboard : IDashboard
    {
        private readonly IQueueService _queueService;
        private readonly IConsoleUI _ui;
        private CancellationTokenSource _cancellationTokenSource;
        private Task _refreshTask;

        public bool IsRunning => _refreshTask?.IsCompleted == false;
        public int RefreshIntervalMs { get; set; } = 1000;

        public LiveDashboard(IQueueService queueService, IConsoleUI ui)
        {
            _queueService = queueService;
            _ui = ui;
        }

        public async Task StartAsync(CancellationToken cancellationToken = default)
        {
            if (IsRunning)
                return;

            _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _refreshTask = RunDashboardAsync(_cancellationTokenSource.Token);
            
            await Task.Delay(100, cancellationToken); // Give it time to start
        }

        public async Task StopAsync()
        {
            if (!IsRunning)
                return;

            _cancellationTokenSource?.Cancel();
            
            if (_refreshTask != null)
            {
                try
                {
                    await _refreshTask;
                }
                catch (OperationCanceledException)
                {
                    // Expected when cancelled
                }
            }
        }

        public async Task RefreshAsync()
        {
            await DisplayDashboardAsync();
        }

        private async Task RunDashboardAsync(CancellationToken cancellationToken)
        {
            _ui.Clear();
            
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await DisplayDashboardAsync();
                    await Task.Delay(RefreshIntervalMs, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // Expected when cancelled
            }
        }

        private async Task DisplayDashboardAsync()
        {
            // Get current data
            var queue = await _queueService.GetQueueAsync();
            var stats = await _queueService.GetStatisticsAsync();
            var isPaused = await _queueService.IsQueuePausedAsync();

            // Create layout
            var layout = new Layout("Root")
                .SplitRows(
                    new Layout("Header").Size(3),
                    new Layout("Content").SplitColumns(
                        new Layout("Queue").Ratio(2),
                        new Layout("Stats").Ratio(1)
                    ),
                    new Layout("Footer").Size(2)
                );

            // Header
            layout["Header"].Update(
                new Panel(new Markup($"[bold blue]🎵 Streaming Plugin Dashboard[/] - {DateTime.Now:HH:mm:ss}"))
                    .BorderColor(Color.Blue)
            );

            // Queue panel
            var queueTable = new Table()
                .BorderColor(Color.Grey)
                .AddColumn("Status")
                .AddColumn("Title")
                .AddColumn("Artist")
                .AddColumn("Progress");

            var recentItems = queue.TakeLast(10).ToList();
            foreach (var item in recentItems)
            {
                var statusColor = item.Status switch
                {
                    DownloadStatus.Completed => "green",
                    DownloadStatus.Failed => "red",
                    DownloadStatus.Downloading => "yellow",
                    DownloadStatus.Pending => "blue",
                    _ => "grey"
                };

                var statusIcon = item.Status switch
                {
                    DownloadStatus.Completed => "✅",
                    DownloadStatus.Failed => "❌",
                    DownloadStatus.Downloading => "⬇️",
                    DownloadStatus.Pending => "⏳",
                    _ => "❓"
                };

                var progressBar = item.Status == DownloadStatus.Downloading 
                    ? $"[{item.ProgressPercent}/100]" 
                    : item.Status.ToString();

                queueTable.AddRow(
                    $"[{statusColor}]{statusIcon}[/]",
                    (item.Title ?? "Unknown").EscapeMarkup(),
                    (item.Artist ?? "Unknown").EscapeMarkup(),
                    progressBar
                );
            }

            layout["Queue"].Update(
                new Panel(queueTable)
                    .Header("[bold]Download Queue[/]")
                    .BorderColor(Color.Green)
            );

            // Stats panel
            var statsTable = new Table()
                .HideHeaders()
                .BorderColor(Color.Grey)
                .AddColumn("Label")
                .AddColumn("Value");

            statsTable.AddRow("Total", stats.TotalItems.ToString());
            statsTable.AddRow("Completed", $"[green]{stats.CompletedItems}[/]");
            statsTable.AddRow("Failed", $"[red]{stats.FailedItems}[/]");
            statsTable.AddRow("In Progress", $"[yellow]{stats.InProgressItems}[/]");
            statsTable.AddRow("Pending", $"[blue]{stats.PendingItems}[/]");
            statsTable.AddRow("Queue State", isPaused ? "[red]Paused[/]" : "[green]Running[/]");

            layout["Stats"].Update(
                new Panel(statsTable)
                    .Header("[bold]Statistics[/]")
                    .BorderColor(Color.Yellow)
            );

            // Footer
            var footerText = "[grey]Press [blue]Ctrl+C[/] to exit dashboard[/]";
            layout["Footer"].Update(
                new Panel(new Markup(footerText))
                    .BorderColor(Color.Grey)
            );

            // Render to console
            AnsiConsole.Clear();
            AnsiConsole.Write(layout);
        }
    }
}