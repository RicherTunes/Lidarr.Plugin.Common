using System;
using System.Linq;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Detects preview/sample URLs and content patterns.
    /// </summary>
    public static class PreviewDetectionUtility
    {
        private static readonly string[] PreviewUrlPatterns = new[]
        {
            "_preview_", "_preview.", "preview_", "preview.",
            "_sample_", "_sample.", "sample_", "sample.",
            "/preview/", "/sample/",
            "preview=true", "sample=true", "preview=1", "sample=1",
            "_demo_", "_demo.", "demo_",
            "_30sec_", "_30s_", "_clip_", "_short_",
            "duration=30", "clip_",
            "_excerpt_", "excerpt_",
            "_teaser_", "teaser_",
            "_snippet_", "snippet_"
        };

        private static readonly int[] PreviewDurationLimits = new[] { 30, 60, 90 };

        public static bool IsPreviewOrSampleUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var u = url.ToLowerInvariant();
            return PreviewUrlPatterns.Any(p => u.Contains(p));
        }

        public static bool IsPreviewDuration(int durationSeconds)
        {
            return durationSeconds > 0 && PreviewDurationLimits.Contains(durationSeconds);
        }

        public static bool IsLikelyPreview(string url, int? durationSeconds, string restrictionMessage)
        {
            if (!string.IsNullOrWhiteSpace(url) && IsPreviewOrSampleUrl(url)) return true;
            if (durationSeconds.HasValue && IsPreviewDuration(durationSeconds.Value)) return true;
            if (!string.IsNullOrWhiteSpace(restrictionMessage))
            {
                var msg = restrictionMessage.ToLowerInvariant();
                if (msg.Contains("preview") || msg.Contains("sample") || msg.Contains("excerpt") || msg.Contains("clip"))
                    return true;
            }
            return false;
        }

        public static string GetPreviewMessage(string trackTitle)
        {
            return $"Track '{trackTitle}' is only available as a preview/sample. Full version requires different subscription or is restricted.";
        }
    }
}

