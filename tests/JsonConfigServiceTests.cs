using System;
using System.IO;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.CLI.Services;
using Newtonsoft.Json;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Comprehensive tests for JsonConfigService covering all operations including:
    /// - Load operations (file exists, missing file, invalid JSON)
    /// - Save operations
    /// - Delete operations
    /// - Clear operations
    /// - Key sanitization
    /// - Error handling
    /// </summary>
    [Trait("Category", "Unit")]
    public class JsonConfigServiceTests : IDisposable
    {
        private readonly string _tempConfigRoot;

        /// <summary>
        /// Sample configuration class for testing
        /// </summary>
        private class TestConfig
        {
            public string? StringValue { get; set; }
            public int IntValue { get; set; }
            public bool BoolValue { get; set; }
            public NestedConfig? Nested { get; set; }
            public string? NullProperty { get; set; }
        }

        /// <summary>
        /// Nested configuration class for testing complex objects
        /// </summary>
        private class NestedConfig
        {
            public string? Name { get; set; }
            public decimal Value { get; set; }
        }

        public JsonConfigServiceTests()
        {
            // Create a unique temporary directory for each test to ensure isolation
            _tempConfigRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempConfigRoot);
        }

        public void Dispose()
        {
            // Clean up temporary directory after all tests complete
            try
            {
                if (Directory.Exists(_tempConfigRoot))
                {
                    Directory.Delete(_tempConfigRoot, recursive: true);
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        private JsonConfigService CreateService(string appName = "TestApp")
        {
            // Create service with a custom temp directory to avoid polluting user's AppData
            var service = new JsonConfigService(appName);

            // Use reflection to replace the config directory with our temp directory
            var field = typeof(JsonConfigService).GetField("_configDirectory",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            if (field != null)
            {
                var customPath = Path.Combine(_tempConfigRoot, appName);
                Directory.CreateDirectory(customPath);
                field.SetValue(service, customPath);
            }

            return service;
        }

        #region Save Operations

        [Fact]
        public async Task SaveAsync_Writes_Json_File()
        {
            // Arrange
            var service = CreateService();
            var config = new TestConfig
            {
                StringValue = "test-value",
                IntValue = 42,
                BoolValue = true
            };
            const string key = "test-config";

            // Act
            await service.SaveAsync(key, config);

            // Assert
            var configDir = service.GetConfigDirectory();
            var filePath = Path.Combine(configDir, "test-config.json");
            Assert.True(File.Exists(filePath));

            var json = await File.ReadAllTextAsync(filePath);
            Assert.Contains("test-value", json);
            Assert.Contains("42", json);
            Assert.Contains("true", json);
        }

        [Fact]
        public async Task SaveAsync_Writes_Indented_Json()
        {
            // Arrange
            var service = CreateService();
            var config = new TestConfig { StringValue = "test" };
            const string key = "formatted";

            // Act
            await service.SaveAsync(key, config);

            // Assert
            var filePath = Path.Combine(service.GetConfigDirectory(), "formatted.json");
            var json = await File.ReadAllTextAsync(filePath);

            // Check for indentation (newlines and spaces)
            Assert.Contains("\n", json);
            Assert.Contains("  ", json);
        }

        [Fact]
        public async Task SaveAsync_Ignores_Null_Properties()
        {
            // Arrange
            var service = CreateService();
            var config = new TestConfig
            {
                StringValue = "present",
                NullProperty = null
            };
            const string key = "null-test";

            // Act
            await service.SaveAsync(key, config);

            // Assert
            var filePath = Path.Combine(service.GetConfigDirectory(), "null-test.json");
            var json = await File.ReadAllTextAsync(filePath);

            Assert.Contains("StringValue", json);
            Assert.DoesNotContain("NullProperty", json);
        }

        [Fact]
        public async Task SaveAsync_Can_Save_Complex_Nested_Objects()
        {
            // Arrange
            var service = CreateService();
            var config = new TestConfig
            {
                StringValue = "parent",
                IntValue = 100,
                Nested = new NestedConfig
                {
                    Name = "nested-value",
                    Value = 99.99m
                }
            };
            const string key = "complex";

            // Act
            await service.SaveAsync(key, config);

            // Assert
            var filePath = Path.Combine(service.GetConfigDirectory(), "complex.json");
            var json = await File.ReadAllTextAsync(filePath);

            Assert.Contains("nested-value", json);
            Assert.Contains("99.99", json);
        }

        [Fact]
        public async Task SaveAsync_Overwrites_Existing_File()
        {
            // Arrange
            var service = CreateService();
            var originalConfig = new TestConfig { IntValue = 1 };
            const string key = "overwrite";
            await service.SaveAsync(key, originalConfig);

            // Act
            var updatedConfig = new TestConfig { IntValue = 999 };
            await service.SaveAsync(key, updatedConfig);

            // Assert
            var loaded = await service.LoadAsync<TestConfig>(key);
            Assert.Equal(999, loaded.IntValue);
        }

        #endregion

        #region Load Operations

        [Fact]
        public async Task LoadAsync_Returns_Default_When_File_Does_Not_Exist()
        {
            // Arrange
            var service = CreateService();
            const string key = "nonexistent";

            // Act
            var result = await service.LoadAsync<TestConfig>(key);

            // Assert
            Assert.Null(result);
        }

        [Fact]
        public async Task LoadAsync_Returns_Default_For_Value_Types_When_File_Does_Not_Exist()
        {
            // Arrange
            var service = CreateService();
            const string key = "nonexistent-int";

            // Act
            var result = await service.LoadAsync<int>(key);

            // Assert
            Assert.Equal(0, result);
        }

        [Fact]
        public async Task LoadAsync_Deserializes_Simple_Config()
        {
            // Arrange
            var service = CreateService();
            var original = new TestConfig
            {
                StringValue = "hello",
                IntValue = 123,
                BoolValue = false
            };
            const string key = "simple-load";
            await service.SaveAsync(key, original);

            // Act
            var loaded = await service.LoadAsync<TestConfig>(key);

            // Assert
            Assert.NotNull(loaded);
            Assert.Equal("hello", loaded.StringValue);
            Assert.Equal(123, loaded.IntValue);
            Assert.False(loaded.BoolValue);
        }

        [Fact]
        public async Task LoadAsync_Deserializes_Nested_Config()
        {
            // Arrange
            var service = CreateService();
            var original = new TestConfig
            {
                Nested = new NestedConfig
                {
                    Name = "nested",
                    Value = 12.34m
                }
            };
            const string key = "nested-load";
            await service.SaveAsync(key, original);

            // Act
            var loaded = await service.LoadAsync<TestConfig>(key);

            // Assert
            Assert.NotNull(loaded);
            Assert.NotNull(loaded.Nested);
            Assert.Equal("nested", loaded.Nested.Name);
            Assert.Equal(12.34m, loaded.Nested.Value);
        }

        [Fact]
        public async Task LoadAsync_Throws_On_Invalid_Json()
        {
            // Arrange
            var service = CreateService();
            const string key = "invalid-json";
            var configDir = service.GetConfigDirectory();
            var filePath = Path.Combine(configDir, "invalid-json.json");
            await File.WriteAllTextAsync(filePath, "{ this is not valid json }");

            // Act & Assert
            await Assert.ThrowsAsync<JsonException>(async () =>
            {
                await service.LoadAsync<TestConfig>(key);
            });
        }

        [Fact]
        public async Task LoadAsync_Throws_On_Mismatched_Type()
        {
            // Arrange
            var service = CreateService();
            const string key = "type-mismatch";
            var configDir = service.GetConfigDirectory();
            var filePath = Path.Combine(configDir, "type-mismatch.json");
            await File.WriteAllTextAsync(filePath, "\"just-a-string\"");

            // Act & Assert
            await Assert.ThrowsAsync<JsonSerializationException>(async () =>
            {
                await service.LoadAsync<TestConfig>(key);
            });
        }

        [Fact]
        public async Task LoadAsync_Can_Load_Primitive_Types()
        {
            // Arrange
            var service = CreateService();
            const string key = "primitive-int";
            await service.SaveAsync(key, 42);

            // Act
            var result = await service.LoadAsync<int>(key);

            // Assert
            Assert.Equal(42, result);
        }

        [Fact]
        public async Task LoadAsync_Can_Load_String_Types()
        {
            // Arrange
            var service = CreateService();
            const string key = "primitive-string";
            await service.SaveAsync(key, "test-string-value");

            // Act
            var result = await service.LoadAsync<string>(key);

            // Assert
            Assert.Equal("test-string-value", result);
        }

        #endregion

        #region Exists Operations

        [Fact]
        public async Task ExistsAsync_Returns_False_When_File_Does_Not_Exist()
        {
            // Arrange
            var service = CreateService();
            const string key = "does-not-exist";

            // Act
            var exists = await service.ExistsAsync(key);

            // Assert
            Assert.False(exists);
        }

        [Fact]
        public async Task ExistsAsync_Returns_True_When_File_Exists()
        {
            // Arrange
            var service = CreateService();
            var config = new TestConfig { IntValue = 1 };
            const string key = "exists-check";
            await service.SaveAsync(key, config);

            // Act
            var exists = await service.ExistsAsync(key);

            // Assert
            Assert.True(exists);
        }

        [Fact]
        public async Task ExistsAsync_Returns_False_After_Delete()
        {
            // Arrange
            var service = CreateService();
            var config = new TestConfig { IntValue = 1 };
            const string key = "delete-exists";
            await service.SaveAsync(key, config);
            Assert.True(await service.ExistsAsync(key));

            // Act
            await service.DeleteAsync(key);
            var existsAfterDelete = await service.ExistsAsync(key);

            // Assert
            Assert.False(existsAfterDelete);
        }

        #endregion

        #region Delete Operations

        [Fact]
        public async Task DeleteAsync_Removes_Config_File()
        {
            // Arrange
            var service = CreateService();
            var config = new TestConfig { IntValue = 1 };
            const string key = "delete-me";
            await service.SaveAsync(key, config);
            var filePath = Path.Combine(service.GetConfigDirectory(), "delete-me.json");
            Assert.True(File.Exists(filePath));

            // Act
            await service.DeleteAsync(key);

            // Assert
            Assert.False(File.Exists(filePath));
        }

        [Fact]
        public async Task DeleteAsync_Does_Not_Throw_When_File_Does_Not_Exist()
        {
            // Arrange
            var service = CreateService();
            const string key = "never-existed";

            // Act & Assert - Should not throw
            await service.DeleteAsync(key);
        }

        [Fact]
        public async Task DeleteAsync_Only_Removes_Specified_Key()
        {
            // Arrange
            var service = CreateService();
            await service.SaveAsync("keep-1", new TestConfig { IntValue = 1 });
            await service.SaveAsync("delete-2", new TestConfig { IntValue = 2 });
            await service.SaveAsync("keep-3", new TestConfig { IntValue = 3 });

            // Act
            await service.DeleteAsync("delete-2");

            // Assert
            Assert.True(await service.ExistsAsync("keep-1"));
            Assert.False(await service.ExistsAsync("delete-2"));
            Assert.True(await service.ExistsAsync("keep-3"));
        }

        #endregion

        #region ClearAll Operations

        [Fact]
        public async Task ClearAllAsync_Removes_All_Config_Files()
        {
            // Arrange
            var service = CreateService();
            await service.SaveAsync("config1", new TestConfig { IntValue = 1 });
            await service.SaveAsync("config2", new TestConfig { IntValue = 2 });
            await service.SaveAsync("config3", new TestConfig { IntValue = 3 });

            // Act
            await service.ClearAllAsync();

            // Assert
            Assert.False(await service.ExistsAsync("config1"));
            Assert.False(await service.ExistsAsync("config2"));
            Assert.False(await service.ExistsAsync("config3"));
        }

        [Fact]
        public async Task ClearAllAsync_Recreates_Config_Directory()
        {
            // Arrange
            var service = CreateService();
            var originalDir = service.GetConfigDirectory();
            await service.SaveAsync("temp", new TestConfig());

            // Act
            await service.ClearAllAsync();

            // Assert
            Assert.True(Directory.Exists(originalDir));
        }

        [Fact]
        public async Task ClearAllAsync_Allows_Saving_After_Clear()
        {
            // Arrange
            var service = CreateService();
            await service.SaveAsync("old", new TestConfig { IntValue = 1 });
            await service.ClearAllAsync();

            // Act
            await service.SaveAsync("new", new TestConfig { IntValue = 2 });

            // Assert
            var loaded = await service.LoadAsync<TestConfig>("new");
            Assert.NotNull(loaded);
            Assert.Equal(2, loaded.IntValue);
        }

        #endregion

        #region Key Sanitization

        [Fact]
        public async Task SaveAsync_Sanitizes_Key_With_Invalid_File_Name_Chars()
        {
            // Arrange
            var service = CreateService();
            var invalidKey = "config/with:invalid*chars?|<>\"";
            var config = new TestConfig { IntValue = 42 };

            // Act
            await service.SaveAsync(invalidKey, config);

            // Assert
            var files = Directory.GetFiles(service.GetConfigDirectory());
            Assert.Single(files);
            Assert.True(File.Exists(files[0]));

            // Key should be sanitized with underscores replacing invalid chars
            var fileName = Path.GetFileName(files[0]);
            Assert.EndsWith(".json", fileName);
        }

        [Fact]
        public async Task LoadAsync_Works_With_Sanitized_Key()
        {
            // Arrange
            var service = CreateService();
            var invalidKey = "config/with:invalid*chars";
            var config = new TestConfig { IntValue = 123 };

            // Act
            await service.SaveAsync(invalidKey, config);
            var loaded = await service.LoadAsync<TestConfig>(invalidKey);

            // Assert
            Assert.NotNull(loaded);
            Assert.Equal(123, loaded.IntValue);
        }

        [Fact]
        public async Task DeleteAsync_Works_With_Sanitized_Key()
        {
            // Arrange
            var service = CreateService();
            var invalidKey = "config|with<>invalid";
            await service.SaveAsync(invalidKey, new TestConfig());

            // Act
            await service.DeleteAsync(invalidKey);

            // Assert
            Assert.False(await service.ExistsAsync(invalidKey));
        }

        #endregion

        #region GetConfigDirectory

        [Fact]
        public void GetConfigDirectory_Returns_Valid_Path()
        {
            // Arrange
            var service = CreateService();

            // Act
            var dir = service.GetConfigDirectory();

            // Assert
            Assert.NotNull(dir);
            Assert.True(Path.IsPathRooted(dir));
        }

        [Fact]
        public void GetConfigDirectory_Creates_Directory_On_Initialization()
        {
            // Arrange & Act
            var service = CreateService();
            var dir = service.GetConfigDirectory();

            // Assert
            Assert.True(Directory.Exists(dir));
        }

        #endregion

        #region Round-Trip Tests

        [Fact]
        public async Task Round_Trip_Preserves_All_Property_Values()
        {
            // Arrange
            var service = CreateService();
            var original = new TestConfig
            {
                StringValue = "preserve-me",
                IntValue = 999,
                BoolValue = true,
                Nested = new NestedConfig
                {
                    Name = "nested-name",
                    Value = 123.45m
                }
            };
            const string key = "round-trip";

            // Act
            await service.SaveAsync(key, original);
            var loaded = await service.LoadAsync<TestConfig>(key);

            // Assert
            Assert.NotNull(loaded);
            Assert.Equal(original.StringValue, loaded.StringValue);
            Assert.Equal(original.IntValue, loaded.IntValue);
            Assert.Equal(original.BoolValue, loaded.BoolValue);
            Assert.NotNull(loaded.Nested);
            Assert.Equal(original.Nested.Name, loaded.Nested.Name);
            Assert.Equal(original.Nested.Value, loaded.Nested.Value);
        }

        [Fact]
        public async Task Multiple_Keys_Can_Be_Stored_Independently()
        {
            // Arrange
            var service = CreateService();

            // Act
            await service.SaveAsync("key1", new TestConfig { IntValue = 1, StringValue = "first" });
            await service.SaveAsync("key2", new TestConfig { IntValue = 2, StringValue = "second" });
            await service.SaveAsync("key3", new TestConfig { IntValue = 3, StringValue = "third" });

            var loaded1 = await service.LoadAsync<TestConfig>("key1");
            var loaded2 = await service.LoadAsync<TestConfig>("key2");
            var loaded3 = await service.LoadAsync<TestConfig>("key3");

            // Assert
            Assert.Equal(1, loaded1.IntValue);
            Assert.Equal("first", loaded1.StringValue);

            Assert.Equal(2, loaded2.IntValue);
            Assert.Equal("second", loaded2.StringValue);

            Assert.Equal(3, loaded3.IntValue);
            Assert.Equal("third", loaded3.StringValue);
        }

        #endregion

        #region Edge Cases

        [Fact]
        public async Task SaveAsync_With_Empty_String_Key()
        {
            // Arrange
            var service = CreateService();
            var config = new TestConfig { IntValue = 1 };

            // Act
            await service.SaveAsync("", config);

            // Assert
            Assert.True(await service.ExistsAsync(""));
            var loaded = await service.LoadAsync<TestConfig>("");
            Assert.Equal(1, loaded.IntValue);
        }

        [Fact]
        public async Task SaveAsync_With_Null_String_Value()
        {
            // Arrange
            var service = CreateService();
            const string key = "null-string-value";

            // Act
            await service.SaveAsync(key, (string?)null);

            // Assert
            var loaded = await service.LoadAsync<string>(key);
            Assert.Null(loaded);
        }

        [Fact]
        public async Task SaveAsync_With_Collection_Types()
        {
            // Arrange
            var service = CreateService();
            var list = new[] { "item1", "item2", "item3" };
            const string key = "array-test";

            // Act
            await service.SaveAsync(key, list);

            // Assert
            var loaded = await service.LoadAsync<string[]>(key);
            Assert.NotNull(loaded);
            Assert.Equal(3, loaded.Length);
            Assert.Contains("item1", loaded);
            Assert.Contains("item2", loaded);
            Assert.Contains("item3", loaded);
        }

        [Fact]
        public async Task SaveAsync_With_DateTime()
        {
            // Arrange
            var service = CreateService();
            var dateTime = new DateTime(2024, 1, 15, 12, 30, 45, DateTimeKind.Utc);
            const string key = "datetime-test";

            // Act
            await service.SaveAsync(key, dateTime);

            // Assert
            var loaded = await service.LoadAsync<DateTime>(key);
            Assert.Equal(dateTime, loaded);
        }

        [Fact]
        public async Task LoadAsync_Returns_Null_For_Nullable_Type_When_Missing()
        {
            // Arrange
            var service = CreateService();
            const string key = "missing-nullable";

            // Act
            var result = await service.LoadAsync<int?>(key);

            // Assert
            Assert.Null(result);
        }

        #endregion

        #region Custom App Name

        [Fact]
        public void Constructor_With_Custom_App_Name_Creates_Separate_Directory()
        {
            // Arrange & Act
            var service1 = CreateService("App1");
            var service2 = CreateService("App2");

            // Assert
            Assert.NotEqual(service1.GetConfigDirectory(), service2.GetConfigDirectory());
        }

        [Fact]
        public async Task Different_Apps_Do_Not_Share_Config()
        {
            // Arrange
            var service1 = CreateService("App1");
            var service2 = CreateService("App2");
            const string key = "shared-key";

            // Act
            await service1.SaveAsync(key, new TestConfig { IntValue = 111 });
            await service2.SaveAsync(key, new TestConfig { IntValue = 222 });

            var loaded1 = await service1.LoadAsync<TestConfig>(key);
            var loaded2 = await service2.LoadAsync<TestConfig>(key);

            // Assert
            Assert.Equal(111, loaded1.IntValue);
            Assert.Equal(222, loaded2.IntValue);
        }

        #endregion
    }
}
