using System;
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

        // R2-05: a directory symlink/junction can be lexically in-bounds (Path.GetFullPath resolves "." and ".."
        // but NOT the link target) while pointing OUTSIDE the root. Recursively deleting through it must be
        // refused so cleanup can't be steered into deleting an unrelated tree. (Nested reparse points are already
        // not traversed by .NET's recursive delete; this closes the top-level case.)
        [Fact]
        public void DeleteTreeUnderRoot_ReparsePointTarget_Refuses_AndLinkTargetSurvives()
        {
            var root = NewTempRoot();
            var outside = NewTempRoot();
            try
            {
                var outsideFile = Path.Combine(outside, "precious.flac");
                File.WriteAllText(outsideFile, "do not delete");

                var link = Path.Combine(root, "link");
                try
                {
                    Directory.CreateSymbolicLink(link, outside);
                }
                catch (System.Exception ex) when (ex is System.IO.IOException or System.UnauthorizedAccessException or System.PlatformNotSupportedException)
                {
                    // Creating a directory symlink needs privilege on some platforms (e.g. Windows without
                    // Developer Mode). Where we can't create one, there's nothing to assert here.
                    return;
                }

                var result = SafeDirectoryCleanup.DeleteTreeUnderRoot(link, root);

                Assert.False(result.Deleted, "a reparse-point target must not be recursively deleted");
                Assert.False(string.IsNullOrEmpty(result.Reason));
                Assert.True(File.Exists(outsideFile), "the link's real target must be untouched");
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { }
                try { Directory.Delete(outside, true); } catch { }
            }
        }

        // LOOP-006: a reparse point NESTED inside an in-bounds tree must not be traversed by the recursive
        // delete. .NET's Directory.Delete(recursive: true) removes a reparse point as a link and does NOT recurse
        // into its target. This verifies that runtime guarantee — the basis for R2-05 scoping the explicit check
        // to the top-level target — and gates it: if a platform ever followed the nested link, the outside file
        // would vanish and this test would fail.
        [Fact]
        public void DeleteTreeUnderRoot_NestedReparsePoint_IsNotTraversed_TargetSurvives()
        {
            var container = NewTempRoot();   // the configured root
            var outside = NewTempRoot();     // an unrelated tree the nested link points at
            try
            {
                var outsideFile = Path.Combine(outside, "precious.flac");
                File.WriteAllText(outsideFile, "do not delete");

                var tree = Path.Combine(container, "artist", "album");   // strict descendant of container
                Directory.CreateDirectory(tree);
                File.WriteAllText(Path.Combine(tree, "01.flac"), "x");

                var nestedLink = Path.Combine(tree, "linkToOutside");
                if (!TryCreateDirReparsePoint(nestedLink, outside))
                {
                    // Couldn't create a reparse point in this environment — nothing to verify.
                    return;
                }

                // Must NOT throw a raw filesystem exception (a nested junction makes .NET's recursive delete throw
                // on Windows; DeleteTreeUnderRoot now reports a partial cleanup instead).
                var result = SafeDirectoryCleanup.DeleteTreeUnderRoot(tree, container);

                // The security invariant on every platform: the reparse point is never followed into its target.
                Assert.True(File.Exists(outsideFile),
                    "the NESTED reparse point must NOT be followed into its target — the outside file must survive");
                Assert.True(Directory.Exists(outside), "the outside directory must survive");
                _ = result; // Deleted (Linux: link removed, tree gone) or Refused (Windows: junction blocked) — both safe.
            }
            finally
            {
                try { Directory.Delete(container, true); } catch { }
                try { Directory.Delete(outside, true); } catch { }
            }
        }

        /// <summary>Creates a directory reparse point at <paramref name="link"/> pointing at <paramref name="target"/>.
        /// Uses a junction on Windows (<c>mklink /J</c> — no elevation required) and a symbolic link elsewhere.
        /// Returns false if neither could be created (privilege / platform).</summary>
        private static bool TryCreateDirReparsePoint(string link, string target)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    using var p = System.Diagnostics.Process.Start(psi);
                    p?.WaitForExit(5000);
                }
                catch { return false; }

                return Directory.Exists(link)
                       && (new DirectoryInfo(link).Attributes & FileAttributes.ReparsePoint) != 0;
            }

            try
            {
                Directory.CreateSymbolicLink(link, target);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }
}
