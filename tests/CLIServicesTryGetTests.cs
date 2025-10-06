using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.CLI.Models;
using Lidarr.Plugin.Common.CLI.Services;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class CLIServicesTryGetTests
    {
        [Fact]
        public async Task MemoryQueueService_TryGetItemAsync_ReturnsNull_WhenMissing()
        {
            var svc = new MemoryQueueService();
            await svc.InitializeAsync();

            var missing = await svc.TryGetItemAsync("none");
            Assert.Null(missing);

            await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetItemAsync("none"));
        }

        [Fact]
        public async Task MemoryQueueService_TryGetItemAsync_ReturnsItem_WhenPresent()
        {
            var svc = new MemoryQueueService();
            await svc.InitializeAsync();

            var item = new CliDownloadItem { Id = string.Empty, Title = "track" };
            var id = await svc.EnqueueAsync(item);

            var hit = await svc.TryGetItemAsync(id);
            Assert.NotNull(hit);
            Assert.Equal(id, hit!.Id);
        }

        [Fact]
        public async Task FileStateService_TryGetAsync_ReturnsNull_WhenMissing_And_GetThrows()
        {
            var appName = "CliStateTests-" + Guid.NewGuid().ToString("N");
            var svc = new FileStateService(appName);
            await svc.InitializeAsync();

            var val = await svc.TryGetAsync<string>("missing");
            Assert.Null(val);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetAsync<string>("missing"));

            CleanupAppState(appName);
        }

        [Fact]
        public async Task FileStateService_TryGetAsync_ReturnsValue_WhenPresent()
        {
            var appName = "CliStateTests-" + Guid.NewGuid().ToString("N");
            var svc = new FileStateService(appName);
            await svc.InitializeAsync();

            await svc.SetAsync("greeting", "hello");
            var val = await svc.TryGetAsync<string>("greeting");
            Assert.Equal("hello", val);

            CleanupAppState(appName);
        }

        private static void CleanupAppState(string appName)
        {
            try
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
            catch { }
        }
    }
}

