using System;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static class RandomProvider
    {
        public static double NextDouble() => Random.Shared.NextDouble();

        public static int Next(int minValue, int maxValue) => Random.Shared.Next(minValue, maxValue);

        public static long NextInt64(long minValue, long maxValue) => Random.Shared.NextInt64(minValue, maxValue);
    }
}
