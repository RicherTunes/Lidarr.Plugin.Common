namespace Lidarr.Plugin.Common.Services.Caching
{
    public static class ArrCachingHeaders
    {
        public const string RevalidatedHeader = "XArrCache";
        public const string LegacyRevalidatedHeader = "X-Arr-Cache";
        public const string RevalidatedValue = "revalidated";
    }
}