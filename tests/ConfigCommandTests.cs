using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Lidarr.Plugin.Common.Base;
using Lidarr.Plugin.Common.CLI;
using Lidarr.Plugin.Common.CLI.Commands;
using Lidarr.Plugin.Common.CLI.Services;
using Lidarr.Plugin.Common.CLI.UI;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class ConfigCommandTests
    {
        [Fact]
        public async Task Set_UpdatesSettingAndPersists()
        {
            var console = new TestConsole();
            var configService = new FakeConfigService();
            var pluginHost = new FakePluginHost();
            var logger = NullLoggerFactory.Instance.CreateLogger<ConfigCommand<TestSettings>>();
            var command = new ConfigCommand<TestSettings>(console, configService, pluginHost, logger);

            var exitCode = await command.Command.InvokeAsync(new[] { "set", "SearchLimit", "250" });

            Assert.Equal(0, exitCode);
            Assert.Equal(250, pluginHost.Settings.SearchLimit);
            Assert.Equal(1, pluginHost.SaveInvocations);
            Assert.Contains("SearchLimit", console.SuccessMessages[^1]);
        }

        [Fact]
        public async Task Get_ReturnsFormattedValue()
        {
            var console = new TestConsole();
            var configService = new FakeConfigService();
            var pluginHost = new FakePluginHost
            {
                Settings = { IncludeSingles = true }
            };
            var logger = NullLoggerFactory.Instance.CreateLogger<ConfigCommand<TestSettings>>();
            var command = new ConfigCommand<TestSettings>(console, configService, pluginHost, logger);

            var exitCode = await command.Command.InvokeAsync(new[] { "get", "IncludeSingles" });

            Assert.Equal(0, exitCode);
            Assert.Contains("IncludeSingles: true", console.OutputLines);
        }

        [Fact]
        public async Task Set_ReturnsWarningForUnknownSetting()
        {
            var console = new TestConsole();
            var configService = new FakeConfigService();
            var pluginHost = new FakePluginHost();
            var logger = NullLoggerFactory.Instance.CreateLogger<ConfigCommand<TestSettings>>();
            var command = new ConfigCommand<TestSettings>(console, configService, pluginHost, logger);

            var exitCode = await command.Command.InvokeAsync(new[] { "set", "does_not_exist", "value" });

            Assert.Equal(1, exitCode);
            Assert.NotEmpty(console.WarningMessages);
            Assert.Contains("not a recognized setting", console.WarningMessages[^1], StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, pluginHost.SaveInvocations);
        }

        [Fact]
        public async Task Set_ReturnsErrorForInvalidConversion()
        {
            var console = new TestConsole();
            var configService = new FakeConfigService();
            var pluginHost = new FakePluginHost();
            var logger = NullLoggerFactory.Instance.CreateLogger<ConfigCommand<TestSettings>>();
            var command = new ConfigCommand<TestSettings>(console, configService, pluginHost, logger);

            var exitCode = await command.Command.InvokeAsync(new[] { "set", "SearchLimit", "not-a-number" });

            Assert.Equal(1, exitCode);
            Assert.NotEmpty(console.ErrorMessages);
            Assert.Contains("not valid", console.ErrorMessages[^1], StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task Get_MasksSensitiveValues()
        {
            var console = new TestConsole();
            var configService = new FakeConfigService();
            var pluginHost = new FakePluginHost
            {
                Settings = { Password = "secret" }
            };
            var logger = NullLoggerFactory.Instance.CreateLogger<ConfigCommand<TestSettings>>();
            var command = new ConfigCommand<TestSettings>(console, configService, pluginHost, logger);

            var exitCode = await command.Command.InvokeAsync(new[] { "get", "Password" });

            Assert.Equal(0, exitCode);
            Assert.Contains("Password: [masked]", console.OutputLines);
        }

        [Fact]
        public async Task Reset_ClearsConfigurationAndRestoresDefaults()
        {
            var console = new TestConsole { ConfirmResult = true };
            var configService = new FakeConfigService();
            var pluginHost = new FakePluginHost
            {
                Settings = { IncludeCompilations = true }
            };
            var logger = NullLoggerFactory.Instance.CreateLogger<ConfigCommand<TestSettings>>();
            var command = new ConfigCommand<TestSettings>(console, configService, pluginHost, logger);

            var exitCode = await command.Command.InvokeAsync(new[] { "reset" });

            Assert.Equal(0, exitCode);
            Assert.True(configService.ClearAllCalled);
            Assert.Equal(1, pluginHost.SaveInvocations);
            Assert.False(pluginHost.Settings.IncludeCompilations);
            Assert.Contains("Configuration reset successfully", console.SuccessMessages[^1]);
        }

        private sealed class TestSettings : BaseStreamingSettings
        {
        }

        private sealed class FakePluginHost : IPluginHost<TestSettings>
        {
            public TestSettings Settings { get; set; } = new();
            public int SaveInvocations { get; private set; }

            public Task<BaseStreamingIndexer<TestSettings>> GetIndexerAsync() => throw new NotImplementedException();

            public Task<BaseStreamingDownloadClient<TestSettings>> GetDownloadClientAsync() => throw new NotImplementedException();

            public Task<TestSettings> GetSettingsAsync() => Task.FromResult(Settings);

            public Task SaveSettingsAsync(TestSettings settings)
            {
                Settings = settings;
                SaveInvocations++;
                return Task.CompletedTask;
            }

            public Task<TestSettings> ReloadSettingsAsync()
            {
                return Task.FromResult(Settings);
            }
        }

        private sealed class FakeConfigService : IConfigService
        {
            private readonly Dictionary<string, object?> _store = new(StringComparer.OrdinalIgnoreCase);

            public bool ClearAllCalled { get; private set; }

            public Task SaveAsync<T>(string key, T value)
            {
                _store[key] = value;
                return Task.CompletedTask;
            }

            public Task<T> LoadAsync<T>(string key)
            {
                return Task.FromResult(_store.TryGetValue(key, out var value) && value is T typed ? typed : default!);
            }

            public Task<bool> ExistsAsync(string key) => Task.FromResult(_store.ContainsKey(key));

            public Task DeleteAsync(string key)
            {
                _store.Remove(key);
                return Task.CompletedTask;
            }

            public Task ClearAllAsync()
            {
                ClearAllCalled = true;
                _store.Clear();
                return Task.CompletedTask;
            }

            public string GetConfigDirectory() => ".";
        }

        private sealed class TestConsole : IConsoleUI
        {
            public List<string> OutputLines { get; } = new();
            public List<string> WarningMessages { get; } = new();
            public List<string> ErrorMessages { get; } = new();
            public List<string> SuccessMessages { get; } = new();
            public bool ConfirmResult { get; set; }
            public Dictionary<string, object>? LastStatus { get; private set; }

            public void WriteMarkup(string markup) => OutputLines.Add(markup);

            public void WriteMarkupLine(string markup) => OutputLines.Add(markup);

            public void Write(string text) => OutputLines.Add(text);

            public void WriteLine(string text = "") => OutputLines.Add(text);

            public void WriteError(string message)
            {
                ErrorMessages.Add(message);
                OutputLines.Add(message);
            }

            public void WriteWarning(string message)
            {
                WarningMessages.Add(message);
                OutputLines.Add(message);
            }

            public void WriteSuccess(string message)
            {
                SuccessMessages.Add(message);
                OutputLines.Add(message);
            }

            public string Ask(string prompt) => throw new NotSupportedException();

            public string AskPassword(string prompt) => throw new NotSupportedException();

            public bool Confirm(string prompt, bool defaultValue = false) => ConfirmResult;

            public T Select<T>(string prompt, IEnumerable<T> choices, Func<T, string>? displaySelector = null) => throw new NotSupportedException();

            public IEnumerable<T> MultiSelect<T>(string prompt, IEnumerable<T> choices, Func<T, string>? displaySelector = null) => throw new NotSupportedException();

            public void ShowTable<T>(IEnumerable<T> data, params (string header, Func<T, string> getValue)[] columns) => throw new NotSupportedException();

            public Task<T> ShowProgressAsync<T>(string taskDescription, Func<IProgress<ProgressInfo>, Task<T>> operation) => throw new NotSupportedException();

            public void ShowStatus(string title, Dictionary<string, object> data)
            {
                LastStatus = data;
            }

            public void Clear()
            {
            }
        }
    }
}

