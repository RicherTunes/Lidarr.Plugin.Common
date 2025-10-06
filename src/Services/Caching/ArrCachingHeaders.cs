namespace Lidarr.Plugin.Common.Services.Caching
{
    /// <summary>
    /// Standard header names and values used by Arr plugins when synthesizing 200 OK responses
    /// from 304 Not Modified (cache revalidation path).
    /// </summary>
    public static class ArrCachingHeaders
    {
        public const string RevalidatedHeader = "XArrCache";
        public const string LegacyRevalidatedHeader = "X-Arr-Cache";
        public const string RevalidatedValue = "revalidated";
    }
}
