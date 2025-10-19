using System.Runtime.InteropServices;

namespace Lidarr.Plugin.Common.Interop
{
    internal static class PosixInterop
    {
#if !NET7_0_OR_GREATER
        [DllImport("libc", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int chmod(string pathname, uint mode);
        public static int Chmod(string path, uint mode)
        {
            try { return chmod(path, mode); } catch { return -1; }
        }
#else
        public static int Chmod(string path, uint mode) => 0;
#endif
    }
}

