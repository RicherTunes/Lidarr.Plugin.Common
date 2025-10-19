using System;
using System.IO;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class PathValidationTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Should_Reject_Null_Or_Whitespace(string? path)
        {
            Assert.False(PathValidation.IsReasonablePath(path));
        }

        [Fact]
        public void Should_Reject_Path_With_Invalid_Chars()
        {
            var invalidChars = Path.GetInvalidPathChars();
            Assert.NotEmpty(invalidChars);

            // Create a path that includes at least one invalid char
            var pathWithInvalid = $"/root/foo{invalidChars[0]}bar";
            Assert.False(PathValidation.IsReasonablePath(pathWithInvalid));
        }

        [Fact]
        public void Should_Reject_Relative_Paths()
        {
            // Relative path lacks a non-empty root on all platforms
            Assert.False(PathValidation.IsReasonablePath("relative/path/to/file"));
            Assert.False(PathValidation.IsReasonablePath("..\\up\\one"));
        }

        [Fact]
        public void Should_Accept_Rooted_Paths_For_Current_OS()
        {
            if (OperatingSystem.IsWindows())
            {
                // Drive-qualified absolute path
                Assert.True(PathValidation.IsReasonablePath("C:\\Music\\Artist\\Album"));
                // UNC share path
                Assert.True(PathValidation.IsReasonablePath("\\\\server\\share\\folder"));
            }
            else
            {
                Assert.True(PathValidation.IsReasonablePath("/home/user/music/album"));
                Assert.True(PathValidation.IsReasonablePath("/tmp"));
            }
        }
    }
}

