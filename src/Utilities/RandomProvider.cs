using System;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static class RandomProvider
    {
        public static double NextDouble() => Random.Shared.NextDouble();

        public static int Next(int minValue, int maxValue) => Random.Shared.Next(minValue, maxValue);
    }
}
