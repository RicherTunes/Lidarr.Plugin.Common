using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Lidarr.Plugin.Common.Base;
using Lidarr.Plugin.Common.CLI.Services;
using Lidarr.Plugin.Common.CLI.UI;
using Lidarr.Plugin.Common.CLI.Models;

namespace Lidarr.Plugin.Common.CLI.Commands
{
    /// <summary>
    /// Base class for CLI commands providing common infrastructure
    /// </summary>
    /// <typeparam name="TSettings">Settings type for the streaming service</typeparam>
    public abstract class BaseCommand<TSettings>
        where TSettings : BaseStreamingSettings, new()
    {
        protected IConsoleUI UI { get; }
        protected IConfigService ConfigService { get; }
        protected IPluginHost<TSettings> PluginHost { get; }
        protected ILogger Logger { get; }

        public Command Command { get; protected set; }

        protected BaseCommand(
            IConsoleUI ui,
            IConfigService configService,
            IPluginHost<TSettings> pluginHost,
            ILogger logger)
        {
            UI = ui ?? throw new ArgumentNullException(nameof(ui));
            ConfigService = configService ?? throw new ArgumentNullException(nameof(configService));
            PluginHost = pluginHost ?? throw new ArgumentNullException(nameof(pluginHost));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));

            Command = CreateCommand();
        }

        /// <summary>
        /// Create the command with options and handlers
        /// </summary>
        protected abstract Command CreateCommand();

        /// <summary>
        /// Handle command execution with error handling
        /// </summary>
        protected async Task<int> ExecuteWithErrorHandlingAsync(Func<Task<int>> handler)
        {
            try
            {
                return await handler();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Command execution failed");
                UI.WriteError(ex.Message);
                return 1;
            }
        }

        /// <summary>
        /// Ensure plugin is authenticated before proceeding
        /// </summary>
        protected async Task<bool> EnsureAuthenticatedAsync()
        {
            try
            {
                var indexer = await PluginHost.GetIndexerAsync();
                var initResult = await indexer.InitializeAsync();
                
                if (!initResult.IsValid)
                {
                    UI.WriteError("Authentication required. Run 'auth login' first.");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Authentication check failed");
                UI.WriteError($"Authentication failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Show operation progress to user
        /// </summary>
        protected async Task<T> ShowProgressAsync<T>(string description, Func<IProgress<ProgressInfo>, Task<T>> operation)
        {
            return await UI.ShowProgressAsync(description, operation);
        }
    }

    #region Standard Commands

    /// <summary>
    /// Authentication command for login/logout/status
    /// </summary>
    public class AuthCommand<TSettings> : BaseCommand<TSettings>
        where TSettings : BaseStreamingSettings, new()
    {
        public AuthCommand(IConsoleUI ui, IConfigService configService, IPluginHost<TSettings> pluginHost, ILogger<AuthCommand<TSettings>> logger)
            : base(ui, configService, pluginHost, logger)
        {
        }

        protected override Command CreateCommand()
        {
            var authCommand = new Command("auth", "Manage authentication");

            // auth login
            var loginCommand = new Command("login", "Login to streaming service");
            var emailOption = new Option<string?>("--email", "Email address");
            var passwordOption = new Option<string?>("--password", "Password"); 
            loginCommand.AddOption(emailOption);
            loginCommand.AddOption(passwordOption);
            loginCommand.SetHandler(async (string? email, string? password) => 
                await ExecuteWithErrorHandlingAsync(() => HandleLoginAsync(email, password)),
                emailOption, passwordOption);

            // auth status
            var statusCommand = new Command("status", "Check authentication status");
            statusCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleStatusAsync));

            // auth logout
            var logoutCommand = new Command("logout", "Clear credentials");
            logoutCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleLogoutAsync));

            authCommand.AddCommand(loginCommand);
            authCommand.AddCommand(statusCommand);
            authCommand.AddCommand(logoutCommand);

            return authCommand;
        }

        private async Task<int> HandleLoginAsync(string? email, string? password)
        {
            // Get credentials if not provided
            email ??= UI.Ask("Email:");
            password ??= UI.AskPassword("Password:");

            UI.WriteLine("Authenticating...");

            var settings = await PluginHost.GetSettingsAsync();
            settings.Email = email;
            settings.Password = password;

            var indexer = await PluginHost.GetIndexerAsync();
            var result = await indexer.InitializeAsync();

            if (result.IsValid)
            {
                await ConfigService.SaveAsync("auth", new { email, password });
                UI.WriteSuccess("Authentication successful!");
                return 0;
            }
            else
            {
                UI.WriteError($"Authentication failed: {string.Join(", ", result.Errors.Select(e => e.ErrorMessage))}");
                return 1;
            }
        }

        private async Task<int> HandleStatusAsync()
        {
            if (await EnsureAuthenticatedAsync())
            {
                UI.WriteSuccess("Authenticated and ready");
                return 0;
            }
            else
            {
                UI.WriteError("Not authenticated");
                return 1;
            }
        }

        private async Task<int> HandleLogoutAsync()
        {
            await ConfigService.DeleteAsync("auth");
            UI.WriteSuccess("Logged out successfully");
            return 0;
        }
    }

    /// <summary>
    /// Search command for finding albums and tracks
    /// </summary>
    public class SearchCommand<TSettings> : BaseCommand<TSettings>
        where TSettings : BaseStreamingSettings, new()
    {
        public SearchCommand(IConsoleUI ui, IConfigService configService, IPluginHost<TSettings> pluginHost, ILogger<SearchCommand<TSettings>> logger)
            : base(ui, configService, pluginHost, logger)
        {
        }

        protected override Command CreateCommand()
        {
            var searchCommand = new Command("search", "Search for music");
            var queryArgument = new Argument<string>("query", "Search query");
            var limitOption = new Option<int>("--limit", () => 10, "Maximum results");

            searchCommand.AddArgument(queryArgument);
            searchCommand.AddOption(limitOption);
            
            searchCommand.SetHandler(async (string query, int limit) =>
                await ExecuteWithErrorHandlingAsync(() => HandleSearchAsync(query, limit)),
                queryArgument, limitOption);

            return searchCommand;
        }

        private async Task<int> HandleSearchAsync(string query, int limit)
        {
            if (!await EnsureAuthenticatedAsync())
                return 1;

            var results = await ShowProgressAsync("Searching...", async progress =>
            {
                progress.Report(new ProgressInfo { Percentage = 50, CurrentTask = "Performing search" });
                
                var indexer = await PluginHost.GetIndexerAsync();
                var searchResults = await indexer.SearchAsync(query);
                
                progress.Report(new ProgressInfo { Percentage = 100, CurrentTask = "Complete" });
                return searchResults.Take(limit).ToList();
            });

            if (results.Any())
            {
                UI.ShowTable(results, 
                    ("Title", album => album.Title ?? "Unknown"),
                    ("Artist", album => album.Artist?.Name ?? "Unknown"),
                    ("Year", album => album.ReleaseDate?.Year.ToString() ?? "Unknown"),
                    ("Tracks", album => album.TrackCount.ToString())
                );
            }
            else
            {
                UI.WriteWarning("No results found");
            }

            return 0;
        }
    }

    /// <summary>
    /// Download command for downloading albums
    /// </summary>
    public class DownloadCommand<TSettings> : BaseCommand<TSettings>
        where TSettings : BaseStreamingSettings, new()
    {
        public DownloadCommand(IConsoleUI ui, IConfigService configService, IPluginHost<TSettings> pluginHost, ILogger<DownloadCommand<TSettings>> logger)
            : base(ui, configService, pluginHost, logger)
        {
        }

        protected override Command CreateCommand()
        {
            var downloadCommand = new Command("download", "Download music");
            var urlArgument = new Argument<string>("url", "Album or track URL/ID");
            var outputOption = new Option<string>("--output", () => "./downloads", "Output directory");

            downloadCommand.AddArgument(urlArgument);
            downloadCommand.AddOption(outputOption);
            
            downloadCommand.SetHandler(async (string url, string output) =>
                await ExecuteWithErrorHandlingAsync(() => HandleDownloadAsync(url, output)),
                urlArgument, outputOption);

            return downloadCommand;
        }

        private async Task<int> HandleDownloadAsync(string url, string output)
        {
            if (!await EnsureAuthenticatedAsync())
                return 1;

            var downloadId = await ShowProgressAsync($"Starting download to {output}...", async progress =>
            {
                progress.Report(new ProgressInfo { Percentage = 25, CurrentTask = "Initializing download" });
                
                var downloadClient = await PluginHost.GetDownloadClientAsync();
                var result = await downloadClient.AddDownloadAsync(url, output);
                
                progress.Report(new ProgressInfo { Percentage = 100, CurrentTask = "Download queued" });
                return result;
            });

            UI.WriteSuccess($"Download started with ID: {downloadId}");
            UI.WriteLine("Use 'queue status' to check progress");
            
            return 0;
        }
    }

    /// <summary>
    /// Configuration command for managing settings
    /// </summary>
    public class ConfigCommand<TSettings> : BaseCommand<TSettings>
        where TSettings : BaseStreamingSettings, new()
    {
        public ConfigCommand(IConsoleUI ui, IConfigService configService, IPluginHost<TSettings> pluginHost, ILogger<ConfigCommand<TSettings>> logger)
            : base(ui, configService, pluginHost, logger)
        {
        }

        protected override Command CreateCommand()
        {
            var configCommand = new Command("config", "Manage configuration settings");

            // config show
            var showCommand = new Command("show", "Show current configuration");
            showCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleShowConfigAsync));

            // config set
            var setCommand = new Command("set", "Set configuration value");
            var keyArgument = new Argument<string>("key", "Configuration key");
            var valueArgument = new Argument<string>("value", "Configuration value");
            setCommand.AddArgument(keyArgument);
            setCommand.AddArgument(valueArgument);
            setCommand.SetHandler(async (string key, string value) => 
                await ExecuteWithErrorHandlingAsync(() => HandleSetConfigAsync(key, value)),
                keyArgument, valueArgument);

            // config get
            var getCommand = new Command("get", "Get configuration value");
            var getKeyArgument = new Argument<string>("key", "Configuration key");
            getCommand.AddArgument(getKeyArgument);
            getCommand.SetHandler(async (string key) => 
                await ExecuteWithErrorHandlingAsync(() => HandleGetConfigAsync(key)),
                getKeyArgument);

            // config reset
            var resetCommand = new Command("reset", "Reset all configuration");
            resetCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleResetConfigAsync));

            configCommand.AddCommand(showCommand);
            configCommand.AddCommand(setCommand);
            configCommand.AddCommand(getCommand);
            configCommand.AddCommand(resetCommand);

            return configCommand;
        }

        private async Task<int> HandleShowConfigAsync()
        {
            var settings = await PluginHost.GetSettingsAsync();
            
            UI.ShowStatus("Current Configuration", new Dictionary<string, object>
            {
                ["Service"] = typeof(TSettings).Name.Replace("Settings", ""),
                ["Email"] = settings.Email ?? "Not set",
                ["Organize by Artist"] = settings.OrganizeByArtist
            });

            return 0;
        }

        private async Task<int> HandleSetConfigAsync(string key, string value)
        {
            await ConfigService.SaveAsync($"setting.{key}", value);
            UI.WriteSuccess($"Configuration '{key}' set to '{value}'");
            return 0;
        }

        private async Task<int> HandleGetConfigAsync(string key)
        {
            var value = await ConfigService.LoadAsync<string>($"setting.{key}");
            if (value != null)
            {
                UI.WriteLine($"{key}: {value}");
            }
            else
            {
                UI.WriteWarning($"Configuration '{key}' not found");
            }
            return 0;
        }

        private async Task<int> HandleResetConfigAsync()
        {
            if (UI.Confirm("Are you sure you want to reset all configuration?"))
            {
                await ConfigService.ClearAllAsync();
                UI.WriteSuccess("Configuration reset successfully");
                return 0;
            }
            
            UI.WriteLine("Reset cancelled");
            return 1;
        }
    }

    /// <summary>
    /// Queue command for managing downloads
    /// </summary>
    public class QueueCommand<TSettings> : BaseCommand<TSettings>
        where TSettings : BaseStreamingSettings, new()
    {
        private readonly IQueueService _queueService;
        private readonly IDashboard _dashboard;

        public QueueCommand(IConsoleUI ui, IConfigService configService, IPluginHost<TSettings> pluginHost, 
            ILogger<QueueCommand<TSettings>> logger, IQueueService queueService, IDashboard dashboard)
            : base(ui, configService, pluginHost, logger)
        {
            _queueService = queueService;
            _dashboard = dashboard;
        }

        protected override Command CreateCommand()
        {
            var queueCommand = new Command("queue", "Manage download queue");

            // queue status
            var statusCommand = new Command("status", "Show queue status");
            statusCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleStatusAsync));

            // queue list
            var listCommand = new Command("list", "List queue items");
            listCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleListAsync));

            // queue clear
            var clearCommand = new Command("clear", "Clear completed downloads");
            clearCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleClearAsync));

            // queue pause
            var pauseCommand = new Command("pause", "Pause queue processing");
            pauseCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandlePauseAsync));

            // queue resume
            var resumeCommand = new Command("resume", "Resume queue processing");
            resumeCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleResumeAsync));

            // queue dashboard
            var dashboardCommand = new Command("dashboard", "Show live dashboard");
            dashboardCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleDashboardAsync));

            queueCommand.AddCommand(statusCommand);
            queueCommand.AddCommand(listCommand);
            queueCommand.AddCommand(clearCommand);
            queueCommand.AddCommand(pauseCommand);
            queueCommand.AddCommand(resumeCommand);
            queueCommand.AddCommand(dashboardCommand);

            return queueCommand;
        }

        private async Task<int> HandleStatusAsync()
        {
            var stats = await _queueService.GetStatisticsAsync();
            var isPaused = await _queueService.IsQueuePausedAsync();

            UI.ShowStatus("Queue Status", new Dictionary<string, object>
            {
                ["Total Items"] = stats.TotalItems,
                ["Completed"] = stats.CompletedItems,
                ["Failed"] = stats.FailedItems,
                ["In Progress"] = stats.InProgressItems,
                ["Pending"] = stats.PendingItems,
                ["Queue State"] = isPaused ? "Paused" : "Running",
                ["Last Activity"] = stats.LastActivity,
                ["Total Downloaded"] = $"{stats.TotalBytesDownloaded / (1024 * 1024):F1} MB"
            });

            return 0;
        }

        private async Task<int> HandleListAsync()
        {
            var queue = await _queueService.GetQueueAsync();
            
            if (!queue.Any())
            {
                UI.WriteWarning("Queue is empty");
                return 0;
            }

            UI.ShowTable(queue,
                ("Status", item => GetStatusDisplay(item.Status)),
                ("Title", item => item.Title ?? "Unknown"),
                ("Artist", item => item.Artist ?? "Unknown"),
                ("Progress", item => item.Status == DownloadStatus.Downloading ? $"{item.ProgressPercent}%" : item.Status.ToString()),
                ("Added", item => item.AddedDate?.ToString("HH:mm:ss") ?? "Unknown")
            );

            return 0;
        }

        private async Task<int> HandleClearAsync()
        {
            await _queueService.ClearCompletedAsync();
            UI.WriteSuccess("Cleared completed downloads from queue");
            return 0;
        }

        private async Task<int> HandlePauseAsync()
        {
            await _queueService.SetQueueStateAsync(true);
            UI.WriteSuccess("Queue paused");
            return 0;
        }

        private async Task<int> HandleResumeAsync()
        {
            await _queueService.SetQueueStateAsync(false);
            UI.WriteSuccess("Queue resumed");
            return 0;
        }

        private async Task<int> HandleDashboardAsync()
        {
            UI.WriteLine("Starting live dashboard... Press Ctrl+C to exit");
            UI.WriteLine();

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (s, e) => 
            {
                e.Cancel = true;
                cts.Cancel();
            };

            try
            {
                await _dashboard.StartAsync(cts.Token);
                
                // Keep running until cancelled
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                await _dashboard.StopAsync();
                UI.Clear();
                UI.WriteSuccess("Dashboard stopped");
            }

            return 0;
        }

        private string GetStatusDisplay(DownloadStatus status)
        {
            return status switch
            {
                DownloadStatus.Completed => "✅ Completed",
                DownloadStatus.Failed => "❌ Failed",
                DownloadStatus.Downloading => "⬇️ Downloading",
                DownloadStatus.Pending => "⏳ Pending",
                _ => "❓ Unknown"
            };
        }
    }

    /// <summary>
    /// History command for viewing download history
    /// </summary>
    public class HistoryCommand<TSettings> : BaseCommand<TSettings>
        where TSettings : BaseStreamingSettings, new()
    {
        private readonly IStateService _stateService;

        public HistoryCommand(IConsoleUI ui, IConfigService configService, IPluginHost<TSettings> pluginHost, 
            ILogger<HistoryCommand<TSettings>> logger, IStateService stateService)
            : base(ui, configService, pluginHost, logger)
        {
            _stateService = stateService;
        }

        protected override Command CreateCommand()
        {
            var historyCommand = new Command("history", "View download history");

            // history show
            var showCommand = new Command("show", "Show recent downloads");
            var limitOption = new Option<int>("--limit", () => 20, "Maximum entries to show");
            showCommand.AddOption(limitOption);
            showCommand.SetHandler(async (int limit) => 
                await ExecuteWithErrorHandlingAsync(() => HandleShowAsync(limit)),
                limitOption);

            // history clear
            var clearCommand = new Command("clear", "Clear download history");
            clearCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleClearAsync));

            // history stats
            var statsCommand = new Command("stats", "Show download statistics");
            statsCommand.SetHandler(async () => 
                await ExecuteWithErrorHandlingAsync(HandleStatsAsync));

            historyCommand.AddCommand(showCommand);
            historyCommand.AddCommand(clearCommand);
            historyCommand.AddCommand(statsCommand);

            return historyCommand;
        }

        private async Task<int> HandleShowAsync(int limit)
        {
            // In a real implementation, this would load from persistent history storage
            var historyItems = await LoadHistoryItemsAsync(limit);

            if (!historyItems.Any())
            {
                UI.WriteWarning("No download history found");
                return 0;
            }

            UI.ShowTable(historyItems,
                ("Date", item => item.CompletedDate?.ToString("MM/dd HH:mm") ?? "Unknown"),
                ("Status", item => GetStatusDisplay(item.Status)),
                ("Title", item => item.Title ?? "Unknown"),
                ("Artist", item => item.Artist ?? "Unknown"),
                ("Duration", item => FormatDuration(item.StartedDate, item.CompletedDate))
            );

            return 0;
        }

        private async Task<int> HandleClearAsync()
        {
            if (UI.Confirm("Are you sure you want to clear download history?"))
            {
                await _stateService.RemoveAsync("download_history");
                UI.WriteSuccess("Download history cleared");
                return 0;
            }
            
            UI.WriteLine("Clear cancelled");
            return 1;
        }

        private async Task<int> HandleStatsAsync()
        {
            var historyItems = await LoadHistoryItemsAsync(1000); // Load more for stats
            
            var totalDownloads = historyItems.Count;
            var successfulDownloads = historyItems.Count(x => x.Status == DownloadStatus.Completed);
            var failedDownloads = historyItems.Count(x => x.Status == DownloadStatus.Failed);
            var totalBytes = historyItems.Where(x => x.Status == DownloadStatus.Completed).Sum(x => x.TotalBytes);
            
            var avgDownloadTime = TimeSpan.Zero;
            if (successfulDownloads > 0)
            {
                var totalSeconds = historyItems
                    .Where(x => x.Status == DownloadStatus.Completed && x.StartedDate.HasValue && x.CompletedDate.HasValue)
                    .Select(x => (x.CompletedDate.Value - x.StartedDate.Value).TotalSeconds)
                    .Where(x => x > 0)
                    .Average();
                
                avgDownloadTime = TimeSpan.FromSeconds(totalSeconds);
            }

            UI.ShowStatus("Download Statistics", new Dictionary<string, object>
            {
                ["Total Downloads"] = totalDownloads,
                ["Successful"] = successfulDownloads,
                ["Failed"] = failedDownloads,
                ["Success Rate"] = totalDownloads > 0 ? $"{(successfulDownloads * 100.0 / totalDownloads):F1}%" : "N/A",
                ["Total Data"] = $"{totalBytes / (1024.0 * 1024 * 1024):F2} GB",
                ["Average Download Time"] = FormatTimeSpan(avgDownloadTime)
            });

            return 0;
        }

        private async Task<List<CliDownloadItem>> LoadHistoryItemsAsync(int limit)
        {
            // In a real implementation, this would load from persistent storage
            // For now, return empty list as placeholder
            var history = await _stateService.GetAsync<List<CliDownloadItem>>("download_history");
            return history?.TakeLast(limit).ToList() ?? new List<CliDownloadItem>();
        }

        private string GetStatusDisplay(DownloadStatus status)
        {
            return status switch
            {
                DownloadStatus.Completed => "✅ Completed",
                DownloadStatus.Failed => "❌ Failed",
                DownloadStatus.Downloading => "⬇️ Downloading",
                DownloadStatus.Pending => "⏳ Pending",
                _ => "❓ Unknown"
            };
        }

        private string FormatDuration(DateTime? started, DateTime? completed)
        {
            if (!started.HasValue || !completed.HasValue)
                return "Unknown";

            var duration = completed.Value - started.Value;
            return FormatTimeSpan(duration);
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalSeconds < 1)
                return "< 1s";
            
            if (timeSpan.TotalMinutes < 1)
                return $"{timeSpan.TotalSeconds:F0}s";
            
            if (timeSpan.TotalHours < 1)
                return $"{timeSpan.TotalMinutes:F0}m {timeSpan.Seconds}s";
            
            return $"{timeSpan.TotalHours:F0}h {timeSpan.Minutes}m";
        }
    }

    #endregion
}