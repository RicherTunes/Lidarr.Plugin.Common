using System.IO;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// F-10: SafeDirectoryCleanup.DeleteTreeUnderRoot recursively deletes a directory tree ONLY when it is a
    /// strict descendant of a configured root, so a failed-download cleanup that is handed a hostile or
    /// mis-derived path can never delete the root itself or anything outside it.
    /// </summary>
    public sealed class SafeDirectoryCleanupTests
    {
        private static string NewTempRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "sdc-" + System.Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return root;
        }

        [Fact]
        public void DeleteTreeUnderRoot_DescendantTree_DeletesIt()
        {
            var root = NewTempRoot();
            try
            {
                var target = Path.Combine(root, "artist", "album");
                Directory.CreateDirectory(target);
                File.WriteAllText(Path.Combine(target, "01.flac"), "x");

                var result = SafeDirectoryCleanup.DeleteTreeUnderRoot(target, root);

                Assert.True(result.Deleted);
                Assert.False(Directory.Exists(target), "the descendant tree must be gone");
                Assert.True(Directory.Exists(root), "the root must remain");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void DeleteTreeUnderRoot_PathEqualsRoot_Refuses()
        {
            var root = NewTempRoot();
            try
            {
                var result = SafeDirectoryCleanup.DeleteTreeUnderRoot(root, root);

                Assert.False(result.Deleted);
                Assert.False(string.IsNullOrEmpty(result.Reason));
                Assert.True(Directory.Exists(root), "must never delete the root itself");
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }

        [Fact]
        public void DeleteTreeUnderRoot_TraversalEscapingRoot_Refuses()
        {
            var root = NewTempRoot();
            var sibling = NewTempRoot();
            try
            {
                Directory.CreateDirectory(Path.Combine(sibling, "keep"));
                // A path that textually starts under root but resolves to the sibling via "..".
                var escape = Path.Combine(root, "..", Path.GetFileName(sibling), "keep");

                var result = SafeDirectoryCleanup.DeleteTreeUnderRoot(escape, root);

                Assert.False(result.Deleted);
                Assert.True(Directory.Exists(Path.Combine(sibling, "keep")), "must not delete outside the root");
            }
            finally { try { Directory.Delete(root, true); } catch { } try { Directory.Delete(sibling, true); } catch { } }
        }

        [Fact]
        public void DeleteTreeUnderRoot_OutsideRoot_Refuses()
        {
            var root = NewTempRoot();
            var outside = NewTempRoot();
            try
            {
                var result = SafeDirectoryCleanup.DeleteTreeUnderRoot(outside, root);

                Assert.False(result.Deleted);
                Assert.True(Directory.Exists(outside), "an unrelated directory must not be deleted");
            }
            finally { try { Directory.Delete(root, true); } catch { } try { Directory.Delete(outside, true); } catch { } }
        }

        [Theory]
        [InlineData("", "C:/root")]
        [InlineData("C:/root/x", "")]
        [InlineData(null, null)]
        public void DeleteTreeUnderRoot_EmptyArgs_Refuses(string? path, string? root)
        {
            var result = SafeDirectoryCleanup.DeleteTreeUnderRoot(path, root);
            Assert.False(result.Deleted);
            Assert.False(string.IsNullOrEmpty(result.Reason));
        }

        [Fact]
        public void DeleteTreeUnderRoot_NonexistentDescendant_NotAnError()
        {
            var root = NewTempRoot();
            try
            {
                var missing = Path.Combine(root, "never-created");

                var result = SafeDirectoryCleanup.DeleteTreeUnderRoot(missing, root);

                Assert.False(result.Deleted);   // nothing deleted...
                Assert.Null(result.Reason);     // ...but not a policy violation either
            }
            finally { try { Directory.Delete(root, true); } catch { } }
        }
    }
}
