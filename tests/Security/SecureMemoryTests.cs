using System;
using System.Runtime.InteropServices;
using System.Text;

using Lidarr.Plugin.Common.Security;

using Xunit;

namespace Lidarr.Plugin.Common.Tests.Security;

/// <summary>
/// Tests for <see cref="SecureMemory"/> — APL-006.
/// <para>
/// Important GC / interning caveats documented here so callers are not
/// misled into expecting cryptographic-strength guarantees:
/// <list type="bullet">
///   <item>
///     <term>ZeroBytes</term>
///     <description>
///       Deterministically overwrites the supplied <c>Span&lt;byte&gt;</c>.
///       If the buffer is rented from <c>ArrayPool</c> or heap-allocated,
///       the overwrite happens before the memory is returned/GC'd — the
///       window of exposure is reliably closed.
///     </description>
///   </item>
///   <item>
///     <term>ZeroPemKey — string mutation</term>
///     <description>
///       Strings in .NET are immutable by contract; CLR semantics do not
///       expose a public API to overwrite their underlying char buffer.
///       <c>ZeroPemKey</c> uses <c>MemoryMarshal.AsBytes</c> on the string
///       span — this is technically undefined behaviour with respect to
///       the CLR's string interning contract, but in practice works on
///       .NET 6–10 for non-interned strings (i.e., strings built at
///       runtime, not compile-time literals).  <em>It must NOT be called
///       on interned strings</em> (the overwrite would corrupt shared
///       state).  Interned literals used as PEM keys are essentially
///       impossible in production because PEM data is read from files /
///       network; the restriction is documented, not enforced at runtime.
///     </description>
///   </item>
///   <item>
///     <term>ZeroPemKey — GC relocation</term>
///     <description>
///       The GC may have already copied the string's backing buffer to
///       another heap address before <c>ZeroPemKey</c> is called (e.g.,
///       during a compaction).  Zeroing via <c>MemoryMarshal</c> touches
///       the <em>current</em> location; old copies may persist until
///       collected.  Callers must treat this as a best-effort measure that
///       significantly reduces — but does not eliminate — the residency
///       window.  Use a <c>char[]</c> / <c>byte[]</c> buffer pinned with
///       <c>GCHandle.Alloc(buf, GCHandleType.Pinned)</c> if a
///       cryptographic-strength guarantee is required.
///     </description>
///   </item>
/// </list>
/// </para>
/// </summary>
public sealed class SecureMemoryTests
{
    // ------------------------------------------------------------------ //
    // ZeroBytes
    // ------------------------------------------------------------------ //

    [Fact]
    public void ZeroBytes_OverwritesAllElementsToZero()
    {
        var buf = new byte[] { 1, 2, 3, 4, 5 };
        SecureMemory.ZeroBytes(buf);
        Assert.All(buf, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ZeroBytes_EmptySpan_DoesNotThrow()
    {
        // Should be a no-op.
        var ex = Record.Exception(() => SecureMemory.ZeroBytes(Span<byte>.Empty));
        Assert.Null(ex);
    }

    [Fact]
    public void ZeroBytes_LargeBuffer_OverwritesEntireSpan()
    {
        var buf = new byte[4096];
        new Random(42).NextBytes(buf);
        Assert.Contains(buf, b => b != 0); // Sanity: buffer has non-zero data.

        SecureMemory.ZeroBytes(buf);

        Assert.All(buf, b => Assert.Equal(0, b));
    }

    [Fact]
    public void ZeroBytes_SliceOfArray_OnlyOverwritesSlice()
    {
        var buf = new byte[] { 1, 2, 3, 4, 5 };
        // Zero only the middle three bytes.
        SecureMemory.ZeroBytes(buf.AsSpan(1, 3));
        Assert.Equal(1, buf[0]);
        Assert.Equal(0, buf[1]);
        Assert.Equal(0, buf[2]);
        Assert.Equal(0, buf[3]);
        Assert.Equal(5, buf[4]);
    }

    [Fact]
    public void ZeroBytes_CalledTwice_IsIdempotent()
    {
        var buf = new byte[] { 9, 8, 7 };
        SecureMemory.ZeroBytes(buf);
        SecureMemory.ZeroBytes(buf);
        Assert.All(buf, b => Assert.Equal(0, b));
    }

    // ------------------------------------------------------------------ //
    // ZeroPemKey
    // ------------------------------------------------------------------ //

    [Fact]
    public void ZeroPemKey_SetsReferenceToNull()
    {
        // Build a non-interned string at runtime.
        var pem = new StringBuilder("-----BEGIN EC PRIVATE KEY-----\nABC123\n-----END EC PRIVATE KEY-----").ToString();
        SecureMemory.ZeroPemKey(ref pem);
        Assert.Null(pem);
    }

    [Fact]
    public void ZeroPemKey_OnNullReference_DoesNotThrow()
    {
        string? pem = null;
        var ex = Record.Exception(() => SecureMemory.ZeroPemKey(ref pem!));
        Assert.Null(ex);
    }

    [Fact]
    public void ZeroPemKey_OnEmptyString_SetsReferenceToNull()
    {
        // Build a non-interned empty-like string at runtime.
        var pem = new string(Array.Empty<char>());
        SecureMemory.ZeroPemKey(ref pem);
        Assert.Null(pem);
    }

    [Fact]
    public void ZeroPemKey_AfterCall_OriginalVariableIsNull()
    {
        // Construct at runtime to avoid interning.
        var key = new string(new char[] { 's', 'e', 'c', 'r', 'e', 't' });
        var capture = key; // keep a reference so the object stays alive.
        SecureMemory.ZeroPemKey(ref key);
        Assert.Null(key);
        // The capture variable is still accessible (we hold the reference),
        // which documents the GC-copy caveat: we cannot guarantee the old
        // buffer through 'capture' is zeroed. That is by design / documented.
        GC.KeepAlive(capture);
    }
}
