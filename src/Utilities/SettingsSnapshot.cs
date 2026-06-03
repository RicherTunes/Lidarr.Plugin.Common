using System;
using System.Reflection;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Creates a defensive snapshot of a settings object by reflection-copying <b>every</b> read-write
    /// property into a fresh instance.
    ///
    /// <para>
    /// Download clients snapshot their settings synchronously, before the background download's first
    /// <c>await</c>, so the deferred work sees a stable view even if the host repoints the live settings
    /// mid-download. Hand-written field-by-field copies are fragile: across several plugins a copy silently
    /// dropped a field (amazon's <c>EnableDrm</c>, tidal's <c>SaveSyncedLyrics</c>/<c>UseLRCLIB</c>), so a
    /// feature the user enabled was ignored in the background download. Copying by reflection makes the
    /// snapshot structurally unable to drop a field — a newly-added setting is captured automatically.
    /// </para>
    ///
    /// <para>
    /// Call with the concrete settings type, e.g. <c>SettingsSnapshot.Copy(this)</c>. A snapshot wants ALL
    /// settings captured, so copy-everything is exactly the right semantic here. Only public read-write
    /// instance properties are copied (computed/get-only properties are skipped); reference-typed values are
    /// copied by reference, which is correct for the immutable strings/primitives settings use.
    /// </para>
    /// </summary>
    public static class SettingsSnapshot
    {
        /// <summary>
        /// Returns a new <typeparamref name="T"/> with every public read-write instance property copied from
        /// <paramref name="source"/>. <typeparamref name="T"/> should be the concrete settings type.
        /// </summary>
        public static T Copy<T>(T source)
            where T : new()
        {
            if (source is null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var copy = new T();
            foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0)
                {
                    property.SetValue(copy, property.GetValue(source));
                }
            }

            return copy;
        }
    }
}
