using System;
using System.Runtime.InteropServices;

namespace Lidarr.Plugin.Common.Security;

/// <summary>
/// Low-level helpers for zeroing sensitive byte buffers and PEM key strings
/// to reduce their heap-residency window after use.
/// </summary>
/// <remarks>
/// <para>
/// <strong>ZeroBytes</strong> — deterministic overwrite of a <c>Span&lt;byte&gt;</c>.
/// When the caller owns the buffer (e.g., a heap-allocated array, a stack-allocated
/// span, or an <c>ArrayPool</c>-rented segment), this ensures the sensitive bytes are
/// cleared before the memory is returned or collected.  Use this for ECDsa key
/// material, derived key bytes, decrypted payloads, etc.
/// </para>
/// <para>
/// <strong>ZeroPemKey</strong> — best-effort overwrite of the char data inside a
/// runtime-allocated PEM string, followed by nulling the caller's reference.
/// </para>
/// <para>
/// <strong>Important caveats for ZeroPemKey:</strong>
/// <list type="bullet">
///   <item>
///     <description>
///       .NET strings are immutable by contract.  <c>ZeroPemKey</c> uses
///       <c>MemoryMarshal.AsBytes</c> to reinterpret the string's char span as
///       bytes and overwrite them in place.  This is technically outside the CLR's
///       public API contract for strings, but works reliably on .NET 6–10 for
///       <em>non-interned</em> heap strings (i.e., strings constructed at runtime
///       from file I/O or network reads, which is where PEM data comes from).
///     </description>
///   </item>
///   <item>
///     <description>
///       <strong>Do NOT call on interned strings</strong> (compile-time literals,
///       or strings that have been passed to <see cref="string.Intern"/>).  Overwriting
///       an interned string corrupts shared state and will cause unpredictable failures.
///       PEM keys loaded at runtime from disk or the network are never interned.
///     </description>
///   </item>
///   <item>
///     <description>
///       The GC may have already relocated the string's backing buffer before this
///       method is called (during a compaction GC).  The overwrite always hits the
///       <em>current</em> location; stale copies at old addresses will be collected
///       normally but are not zeroed.  This is a best-effort measure that significantly
///       reduces — but does not eliminate — the heap-residency window.
///     </description>
///   </item>
///   <item>
///     <description>
///       For a cryptographic-strength guarantee, store key material in a
///       <c>byte[]</c> pinned with <c>GCHandle.Alloc(buf, GCHandleType.Pinned)</c>
///       and call <see cref="ZeroBytes"/> when done, or use
///       <c>System.Security.SecureString</c> for user-credential scenarios.
///     </description>
///   </item>
/// </list>
/// </para>
/// </remarks>
public static class SecureMemory
{
    /// <summary>
    /// Overwrites every byte in <paramref name="buffer"/> with zero.
    /// </summary>
    /// <param name="buffer">The span of bytes to zero.  May be empty.</param>
    public static void ZeroBytes(Span<byte> buffer)
    {
        buffer.Clear();
    }

    /// <summary>
    /// Best-effort overwrite of the char data inside a runtime PEM string, then
    /// sets the caller's reference to <see langword="null"/>.
    /// </summary>
    /// <param name="pem">
    /// Reference to the PEM string to zero.  Must not be an interned string —
    /// see class-level remarks for full caveats.  Safe to call when already
    /// <see langword="null"/>.
    /// </param>
    public static void ZeroPemKey(ref string? pem)
    {
        if (pem is null)
            return;

        // Skip empty strings entirely. On modern .NET, `new string(Array.Empty<char>())`
        // and other zero-length constructions return the interned `string.Empty`
        // singleton. Overwriting its char buffer would corrupt every reference to
        // `string.Empty` across the AppDomain — and there's nothing to overwrite
        // anyway. Just null the reference and return.
        if (pem.Length == 0)
        {
            pem = null;
            return;
        }

        // Reinterpret the string's internal char data as bytes and overwrite
        // each byte with 0.  MemoryMarshal.AsBytes works on any MemoryMarshal-
        // compatible span; string.AsSpan() returns a ReadOnlySpan<char> which
        // we reinterpret as a writable byte span via the mutable MemoryMarshal
        // path.  This is the only way to do this without unsafe/pinvoke.
        var chars = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(
            ref MemoryMarshal.GetReference(pem.AsSpan()), pem.Length));
        chars.Clear();

        pem = null;
    }
}
