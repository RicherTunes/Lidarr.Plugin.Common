using System.Globalization;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Shared, dependency-free string helpers.
    /// </summary>
    public static class StringUtilities
    {
        /// <summary>
        /// Truncates <paramref name="text"/> to at most <paramref name="maxLengthChars"/> UTF-16 code
        /// units WITHOUT ever splitting a grapheme cluster — and therefore never a surrogate pair or a
        /// base+combining-mark sequence. A naive <c>Substring(0, n)</c> can land inside an astral
        /// codepoint's surrogate pair and leave a lone surrogate: an invalid string that mojibakes on
        /// the wire and in storage. Returns the input unchanged when it already fits, and an empty
        /// string when <paramref name="maxLengthChars"/> is non-positive or too small for even the
        /// first grapheme (better an empty value than a broken one).
        /// </summary>
        public static string TruncateGraphemeSafe(string? text, int maxLengthChars)
        {
            if (string.IsNullOrEmpty(text) || text!.Length <= maxLengthChars)
            {
                return text ?? string.Empty;
            }

            if (maxLengthChars <= 0)
            {
                return string.Empty;
            }

            var enumerator = StringInfo.GetTextElementEnumerator(text);
            var cut = 0;
            while (enumerator.MoveNext())
            {
                var element = (string)enumerator.Current;
                if (cut + element.Length > maxLengthChars)
                {
                    break;
                }

                cut += element.Length;
            }

            return text.Substring(0, cut);
        }
    }
}
