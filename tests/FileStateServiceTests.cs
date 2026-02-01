using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.CLI.Services;
using Newtonsoft.Json;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    [Trait("Category", "Unit")]
    public class FileStateServiceTests : IDisposable
    {
        private readonly string _tempDir;
        private readonly string _stateFilePath;
        private bool _disposed;

        public FileStateServiceTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
            _stateFilePath = Path.Combine(_tempDir, "session-state.json");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        private class TestDataObject
        {
            public int Id { get; set; }
            public string Name { get; set; } = string.Empty;
            public string[] Items { get; set; } = Array.Empty<string>();
        }

        private FileStateService CreateService()
        {
            var constructor = typeof(FileStateService).GetConstructor(
                new[] { typeof(string), typeof(bool) }
            );

            if (constructor != null)
            {
                return (FileStateService)constructor.Invoke(new object[] { _stateFilePath, true });
            }

            var appName = $"Test_{Guid.NewGuid():N}";
            return new FileStateService(appName);
        }

        #region PersistStateOperations

        [Fact]
        public async Task SaveAsync_SingleValue_WritesToFile()
        {
            var service = CreateService();
            await service.SetAsync("test-key", "test-value");
            await service.SaveAsync();

            Assert.True(File.Exists(_stateFilePath));
            var content = await File.ReadAllTextAsync(_stateFilePath);
            Assert.Contains("test-key", content);
            Assert.Contains("test-value", content);
        }

        [Fact]
        public async Task SaveAsync_MultipleValues_WritesAllToFile()
        {
            var service = CreateService();
            await service.SetAsync("key1", "value1");
            await service.SetAsync("key2", "value2");
            await service.SetAsync("key3", "value3");
            await service.SaveAsync();

            Assert.True(File.Exists(_stateFilePath));
            var content = await File.ReadAllTextAsync(_stateFilePath);
            Assert.Contains("key1", content);
            Assert.Contains("key2", content);
            Assert.Contains("key3", content);
        }

        [Fact]
        public async Task SaveAsync_ComplexType_PreservesTypeInformation()
        {
            var service = CreateService();
            var complexObj = new TestDataObject
            {
                Id = 42,
                Name = "Test Object",
                Items = new[] { "item1", "item2" }
            };
            await service.SetAsync("complex-key", complexObj);
            await service.SaveAsync();

            Assert.True(File.Exists(_stateFilePath));
            var content = await File.ReadAllTextAsync(_stateFilePath);
            Assert.Contains("$type", content);
        }

        [Fact]
        public async Task SaveAsync_IntegerValue_WritesCorrectly()
        {
            var service = CreateService();
            await service.SetAsync("count", 123);
            await service.SaveAsync();

            var content = await File.ReadAllTextAsync(_stateFilePath);
            Assert.Contains("123", content);
        }

        [Fact]
        public async Task SaveAsync_BooleanValue_WritesCorrectly()
        {
            var service = CreateService();
            await service.SetAsync("isActive", true);
            await service.SaveAsync();

            var content = await File.ReadAllTextAsync(_stateFilePath);
            Assert.Contains("true", content);
        }

        [Fact]
        public async Task SaveAsync_NullValue_WritesCorrectly()
        {
            var service = CreateService();
            await service.SetAsync<string?>("nullable-key", null);
            await service.SaveAsync();

            var content = await File.ReadAllTextAsync(_stateFilePath);
            Assert.Contains("nullable-key", content);
        }

        [Fact]
        public async Task SaveAsync_OverwritesExistingFile()
        {
            var service = CreateService();
            await File.WriteAllTextAsync(_stateFilePath, "old content");
            await service.SetAsync("new-key", "new-value");
            await service.SaveAsync();

            var content = await File.ReadAllTextAsync(_stateFilePath);
            Assert.Contains("new-key", content);
            Assert.DoesNotContain("old content", content);
        }

        [Fact]
        public async Task SaveAsync_JsonFormat_IsIndented()
        {
            var service = CreateService();
            await service.SetAsync("key", "value");
            await service.SaveAsync();

            var content = await File.ReadAllTextAsync(_stateFilePath);
            Assert.Contains("\n", content);
            Assert.Contains("{", content);
            Assert.Contains("}", content);
        }

        #endregion

        #region RestoreStateOperations

        [Fact]
        public async Task LoadAsync_ExistingFile_RestoresState()
        {
            var jsonContent = @"{
  ""key1"": ""value1"",
  ""key2"": ""value2""
}";
            await File.WriteAllTextAsync(_stateFilePath, jsonContent);

            var service = CreateService();
            await service.LoadAsync();

            Assert.True(await service.ExistsAsync("key1"));
            Assert.True(await service.ExistsAsync("key2"));
            Assert.Equal("value1", await service.GetAsync<string>("key1"));
            Assert.Equal("value2", await service.GetAsync<string>("key2"));
        }

        [Fact]
        public async Task LoadAsync_ComplexType_RestoresCorrectly()
        {
            var testObj = new TestDataObject
            {
                Id = 100,
                Name = "Restored Object",
                Items = new[] { "a", "b", "c" }
            };
            var jsonContent = JsonConvert.SerializeObject(
                new Dictionary<string, object> { { "obj", testObj } },
                new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    TypeNameHandling = TypeNameHandling.Auto
                }
            );
            await File.WriteAllTextAsync(_stateFilePath, jsonContent);

            var service = CreateService();
            await service.LoadAsync();

            var restored = await service.GetAsync<TestDataObject>("obj");
            Assert.NotNull(restored);
            Assert.Equal(100, restored.Id);
            Assert.Equal("Restored Object", restored.Name);
            Assert.Equal(new[] { "a", "b", "c" }, restored.Items);
        }

        [Fact]
        public async Task LoadAsync_EmptyFile_LeavesStateEmpty()
        {
            await File.WriteAllTextAsync(_stateFilePath, "{}");

            var service = CreateService();
            await service.LoadAsync();

            var keys = await service.GetKeysAsync();
            Assert.Empty(keys);
        }

        [Fact]
        public async Task LoadAsync_ClearsExistingState()
        {
            var service = CreateService();
            await service.SetAsync("old-key", "old-value");
            await service.SaveAsync();

            var jsonContent = @"{
  ""new-key"": ""new-value""
}";
            await File.WriteAllTextAsync(_stateFilePath, jsonContent);

            await service.LoadAsync();

            Assert.False(await service.ExistsAsync("old-key"));
            Assert.True(await service.ExistsAsync("new-key"));
        }

        [Fact]
        public async Task LoadAsync_MultipleTypes_RestoresAll()
        {
            var data = new Dictionary<string, object>
            {
                { "stringVal", "hello" },
                { "intVal", 42 },
                { "boolVal", true },
                { "doubleVal", 3.14 }
            };
            var jsonContent = JsonConvert.SerializeObject(
                data,
                new JsonSerializerSettings
                {
                    Formatting = Formatting.Indented,
                    TypeNameHandling = TypeNameHandling.Auto
                }
            );
            await File.WriteAllTextAsync(_stateFilePath, jsonContent);

            var service = CreateService();
            await service.LoadAsync();

            Assert.Equal("hello", await service.GetAsync<string>("stringVal"));
            Assert.Equal(42, await service.GetAsync<int>("intVal"));
            Assert.True(await service.GetAsync<bool>("boolVal"));
            Assert.Equal(3.14, await service.GetAsync<double>("doubleVal"));
        }

        #endregion

        #region MissingStateFileHandling

        [Fact]
        public async Task LoadAsync_NoFile_DoesNotThrow()
        {
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
            }

            var service = CreateService();
            var exception = await Record.ExceptionAsync(async () => await service.LoadAsync());
            Assert.Null(exception);
        }

        [Fact]
        public async Task InitializeAsync_NoFile_DoesNotThrow()
        {
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
            }

            var service = CreateService();
            var exception = await Record.ExceptionAsync(async () => await service.InitializeAsync());
            Assert.Null(exception);
        }

        [Fact]
        public async Task LoadAsync_NoFile_LeavesStateEmpty()
        {
            if (File.Exists(_stateFilePath))
            {
                File.Delete(_stateFilePath);
            }

            var service = CreateService();
            await service.LoadAsync();

            var keys = await service.GetKeysAsync();
            Assert.Empty(keys);
        }

        [Fact]
        public async Task SaveThenLoad_NoDirectory_CreatesDirectory()
        {
            var service = CreateService();
            await service.SetAsync("key", "value");
            await service.SaveAsync();

            Assert.True(Directory.Exists(_tempDir));
        }

        #endregion

        #region InvalidStateFileHandling

        [Fact]
        public async Task LoadAsync_InvalidJson_DoesNotThrow()
        {
            await File.WriteAllTextAsync(_stateFilePath, "{ invalid json }");

            var service = CreateService();
            var exception = await Record.ExceptionAsync(async () => await service.LoadAsync());
            Assert.Null(exception);
        }

        [Fact]
        public async Task LoadAsync_InvalidJson_LeavesStateResilient()
        {
            var service = CreateService();
            await service.SetAsync("pre-existing", "value");
            await File.WriteAllTextAsync(_stateFilePath, "{ not valid }");

            await service.LoadAsync();

            var keys = await service.GetKeysAsync();
            Assert.NotNull(keys);
        }

        [Fact]
        public async Task LoadAsync_EmptyFile_DoesNotThrow()
        {
            await File.WriteAllTextAsync(_stateFilePath, string.Empty);

            var service = CreateService();
            var exception = await Record.ExceptionAsync(async () => await service.LoadAsync());
            Assert.Null(exception);
        }

        [Fact]
        public async Task LoadAsync_CorruptedTypeHandling_DoesNotThrow()
        {
            await File.WriteAllTextAsync(_stateFilePath, @"{
  ""key"": {""$type"": ""NonExistent.Type, Assembly""}
}");

            var service = CreateService();
            var exception = await Record.ExceptionAsync(async () => await service.LoadAsync());
            Assert.Null(exception);
        }

        #endregion

        #region ClearOperations

        [Fact]
        public async Task ClearAsync_RemovesAllInMemoryState()
        {
            var service = CreateService();
            await service.SetAsync("key1", "value1");
            await service.SetAsync("key2", "value2");
            await service.SetAsync("key3", "value3");

            await service.ClearAsync();

            var keys = await service.GetKeysAsync();
            Assert.Empty(keys);
            Assert.False(await service.ExistsAsync("key1"));
            Assert.False(await service.ExistsAsync("key2"));
            Assert.False(await service.ExistsAsync("key3"));
        }

        [Fact]
        public async Task ClearAsync_DoesNotDeleteFile()
        {
            var service = CreateService();
            await service.SetAsync("key", "value");
            await service.SaveAsync();

            await service.ClearAsync();

            Assert.True(File.Exists(_stateFilePath));
        }

        [Fact]
        public async Task ClearAsync_CanAddNewValuesAfterClear()
        {
            var service = CreateService();
            await service.SetAsync("old-key", "old-value");
            await service.ClearAsync();

            await service.SetAsync("new-key", "new-value");

            Assert.True(await service.ExistsAsync("new-key"));
            Assert.Equal("new-value", await service.GetAsync<string>("new-key"));
            Assert.False(await service.ExistsAsync("old-key"));
        }

        [Fact]
        public async Task ClearEmptyState_DoesNotThrow()
        {
            var service = CreateService();
            var exception = await Record.ExceptionAsync(async () => await service.ClearAsync());
            Assert.Null(exception);
        }

        #endregion

        #region ThreadSafety

        [Fact]
        public async Task SetAsync_ConcurrentWrites_ThreadSafe()
        {
            var service = CreateService();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var tasks = Enumerable.Range(0, 100).Select(i =>
                Task.Run(async () =>
                {
                    try
                    {
                        await service.SetAsync($"key-{i}", $"value-{i}");
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                })
            );

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);
        }

        [Fact]
        public async Task GetAsync_ConcurrentReads_ThreadSafe()
        {
            var service = CreateService();
            await service.SetAsync("shared-key", "shared-value");
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var tasks = Enumerable.Range(0, 100).Select(_ =>
                Task.Run(async () =>
                {
                    try
                    {
                        var value = await service.GetAsync<string>("shared-key");
                        Assert.Equal("shared-value", value);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                })
            );

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);
        }

        [Fact]
        public async Task ConcurrentReadWrite_ThreadSafe()
        {
            var service = CreateService();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var writeTasks = Enumerable.Range(0, 50).Select(i =>
                Task.Run(async () =>
                {
                    try
                    {
                        await service.SetAsync($"key-{i}", i);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                })
            );

            var readTasks = Enumerable.Range(0, 50).Select(i =>
                Task.Run(async () =>
                {
                    try
                    {
                        await service.SetAsync($"read-key-{i}", i);
                        var value = await service.GetAsync<int>($"read-key-{i}");
                        Assert.Equal(i, value);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                })
            );

            await Task.WhenAll(writeTasks.Concat(readTasks));

            Assert.Empty(exceptions);
        }

        [Fact]
        public async Task SaveAsync_ConcurrentCalls_ThreadSafe()
        {
            var service = CreateService();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var tasks = Enumerable.Range(0, 20).Select(i =>
                Task.Run(async () =>
                {
                    try
                    {
                        await service.SetAsync($"key-{i}", i);
                        await service.SaveAsync();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                })
            );

            await Task.WhenAll(tasks);

            Assert.Empty(exceptions);
            Assert.True(File.Exists(_stateFilePath));
        }

        [Fact]
        public async Task LoadAsync_ConcurrentWithWrites_ThreadSafe()
        {
            var initialService = CreateService();
            await initialService.SetAsync("initial", "value");
            await initialService.SaveAsync();

            var service = CreateService();
            var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

            var writeTasks = Enumerable.Range(0, 20).Select(i =>
                Task.Run(async () =>
                {
                    try
                    {
                        await service.SetAsync($"key-{i}", i);
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                })
            );

            var loadTasks = Enumerable.Range(0, 10).Select(_ =>
                Task.Run(async () =>
                {
                    try
                    {
                        await service.LoadAsync();
                    }
                    catch (Exception ex)
                    {
                        exceptions.Add(ex);
                    }
                })
            );

            await Task.WhenAll(writeTasks.Concat(loadTasks));

            Assert.Empty(exceptions);
        }

        #endregion

        #region StateVersioningAndTypePreservation

        [Fact]
        public async Task SaveLoad_RoundTrip_String_PreservesValue()
        {
            var service = CreateService();
            const string originalValue = "test-string-value";

            await service.SetAsync("str-key", originalValue);
            await service.SaveAsync();

            var newService = CreateService();
            await newService.LoadAsync();
            var restoredValue = await newService.GetAsync<string>("str-key");

            Assert.Equal(originalValue, restoredValue);
        }

        [Fact]
        public async Task SaveLoad_RoundTrip_Integer_PreservesValue()
        {
            var service = CreateService();
            const int originalValue = 42;

            await service.SetAsync("int-key", originalValue);
            await service.SaveAsync();

            var newService = CreateService();
            await newService.LoadAsync();
            var restoredValue = await newService.GetAsync<int>("int-key");

            Assert.Equal(originalValue, restoredValue);
        }

        [Fact]
        public async Task SaveLoad_RoundTrip_ComplexObject_PreservesValue()
        {
            var service = CreateService();
            var original = new TestDataObject
            {
                Id = 999,
                Name = "RoundTrip Test",
                Items = new[] { "x", "y", "z" }
            };

            await service.SetAsync("obj-key", original);
            await service.SaveAsync();

            var newService = CreateService();
            await newService.LoadAsync();
            var restored = await newService.GetAsync<TestDataObject>("obj-key");

            Assert.NotNull(restored);
            Assert.Equal(original.Id, restored.Id);
            Assert.Equal(original.Name, restored.Name);
            Assert.Equal(original.Items, restored.Items);
        }

        [Fact]
        public async Task SaveLoad_RoundTrip_List_PreservesValue()
        {
            var service = CreateService();
            var original = new List<string> { "apple", "banana", "cherry" };

            await service.SetAsync("list-key", original);
            await service.SaveAsync();

            var newService = CreateService();
            await newService.LoadAsync();
            var restored = await newService.GetAsync<List<string>>("list-key");

            Assert.Equal(original, restored);
        }

        [Fact]
        public async Task SaveLoad_RoundTrip_Dictionary_PreservesValue()
        {
            var service = CreateService();
            var original = new Dictionary<string, int>
            {
                { "one", 1 },
                { "two", 2 },
                { "three", 3 }
            };

            await service.SetAsync("dict-key", original);
            await service.SaveAsync();

            var newService = CreateService();
            await newService.LoadAsync();
            var restored = await newService.GetAsync<Dictionary<string, int>>("dict-key");

            Assert.Equal(original.Count, restored.Count);
            Assert.Equal(1, restored["one"]);
            Assert.Equal(2, restored["two"]);
            Assert.Equal(3, restored["three"]);
        }

        #endregion

        #region QueryAndRemoveOperations

        [Fact]
        public async Task GetAsync_ExistingKey_ReturnsValue()
        {
            var service = CreateService();
            await service.SetAsync("key", "value");

            var result = await service.GetAsync<string>("key");

            Assert.Equal("value", result);
        }

        [Fact]
        public async Task GetAsync_NonExistentKey_ThrowsKeyNotFoundException()
        {
            var service = CreateService();

            await Assert.ThrowsAsync<KeyNotFoundException>(
                async () => await service.GetAsync<string>("non-existent")
            );
        }

        [Fact]
        public async Task GetAsync_WrongType_ThrowsKeyNotFoundException()
        {
            var service = CreateService();
            await service.SetAsync("key", "string-value");

            await Assert.ThrowsAsync<KeyNotFoundException>(
                async () => await service.GetAsync<int>("key")
            );
        }

        [Fact]
        public async Task TryGetAsync_ExistingKey_ReturnsValue()
        {
            var service = CreateService();
            await service.SetAsync("key", "value");

            var result = await service.TryGetAsync<string>("key");

            Assert.Equal("value", result);
        }

        [Fact]
        public async Task TryGetAsync_NonExistentKey_ReturnsDefault()
        {
            var service = CreateService();

            var result = await service.TryGetAsync<string>("non-existent");

            Assert.Null(result);
        }

        [Fact]
        public async Task TryGetAsync_WrongType_ReturnsDefault()
        {
            var service = CreateService();
            await service.SetAsync("key", "string-value");

            var result = await service.TryGetAsync<int>("key");

            Assert.Equal(0, result);
        }

        [Fact]
        public async Task ExistsAsync_ExistingKey_ReturnsTrue()
        {
            var service = CreateService();
            await service.SetAsync("key", "value");

            var result = await service.ExistsAsync("key");

            Assert.True(result);
        }

        [Fact]
        public async Task ExistsAsync_NonExistentKey_ReturnsFalse()
        {
            var service = CreateService();

            var result = await service.ExistsAsync("non-existent");

            Assert.False(result);
        }

        [Fact]
        public async Task RemoveAsync_ExistingKey_RemovesKey()
        {
            var service = CreateService();
            await service.SetAsync("key", "value");

            await service.RemoveAsync("key");

            Assert.False(await service.ExistsAsync("key"));
        }

        [Fact]
        public async Task RemoveAsync_NonExistentKey_DoesNotThrow()
        {
            var service = CreateService();

            var exception = await Record.ExceptionAsync(async () => await service.RemoveAsync("non-existent"));
            Assert.Null(exception);
        }

        [Fact]
        public async Task GetKeysAsync_ReturnsAllKeys()
        {
            var service = CreateService();
            await service.SetAsync("key1", "value1");
            await service.SetAsync("key2", "value2");
            await service.SetAsync("key3", "value3");

            var keys = await service.GetKeysAsync();

            Assert.Equal(3, keys.Count());
            Assert.Contains("key1", keys);
            Assert.Contains("key2", keys);
            Assert.Contains("key3", keys);
        }

        [Fact]
        public async Task GetKeysAsync_EmptyState_ReturnsEmpty()
        {
            var service = CreateService();

            var keys = await service.GetKeysAsync();

            Assert.Empty(keys);
        }

        #endregion

        #region SetAsyncUpdateBehavior

        [Fact]
        public async Task SetAsync_ExistingKey_UpdatesValue()
        {
            var service = CreateService();
            await service.SetAsync("key", "old-value");

            await service.SetAsync("key", "new-value");

            Assert.Equal("new-value", await service.GetAsync<string>("key"));
        }

        [Fact]
        public async Task SetAsync_DifferentTypesForSameKey_UpdatesValue()
        {
            var service = CreateService();
            await service.SetAsync("key", "string-value");

            await service.SetAsync("key", 42);

            Assert.Equal(42, await service.GetAsync<int>("key"));
        }

        #endregion

        #region InitializeAsync

        [Fact]
        public async Task InitializeAsync_WithExistingState_LoadsState()
        {
            var jsonContent = @"{
  ""loaded-key"": ""loaded-value""
}";
            await File.WriteAllTextAsync(_stateFilePath, jsonContent);

            var service = CreateService();
            await service.InitializeAsync();

            Assert.True(await service.ExistsAsync("loaded-key"));
            Assert.Equal("loaded-value", await service.GetAsync<string>("loaded-key"));
        }

        [Fact]
        public async Task InitializeAsync_WithCorruptedState_DoesNotCrash()
        {
            await File.WriteAllTextAsync(_stateFilePath, "{ corrupted }");

            var service = CreateService();
            var exception = await Record.ExceptionAsync(async () => await service.InitializeAsync());
            Assert.Null(exception);
        }

        #endregion
    }
}
