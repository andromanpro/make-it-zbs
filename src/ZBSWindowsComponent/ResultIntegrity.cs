using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace ZBSWindowsComponent
{
    internal sealed class IntegrityReview
    {
        [JsonProperty("removed")]
        public string[] Removed { get; set; } = Array.Empty<string>();
        [JsonProperty("added")]
        public string[] Added { get; set; } = Array.Empty<string>();
        [JsonProperty("unavailable")]
        public bool Unavailable { get; set; }
        [JsonProperty("requiresReview")]
        public bool RequiresReview => Unavailable || Removed.Length > 0 || Added.Length > 0;
    }

    internal static class ResultIntegrity
    {
        private const int TextLimit = 1000000;
        // Preserve separators and signs: ambiguous date/decimal formats must not silently compare equal.
        private static readonly Regex Numbers = new Regex(@"[+\-\u2212]?[0-9]+(?:[.,:/\-][0-9]+)*%?",
            RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));

        internal static IntegrityReview Compare(string original, string result, string context)
        {
            if (context != "Text" && context != "Email" && context != "Word") return new IntegrityReview();
            original = original ?? string.Empty;
            result = result ?? string.Empty;
            if (original.Length > TextLimit || result.Length > TextLimit) return new IntegrityReview { Unavailable = true };
            try
            {
                var before = Extract(original);
                var after = Extract(result);
                return new IntegrityReview { Removed = Difference(before, after), Added = Difference(after, before) };
            }
            catch (RegexMatchTimeoutException)
            {
                return new IntegrityReview { Unavailable = true };
            }
        }

        private static Dictionary<string, int> Extract(string value)
        {
            var counts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Match match in Numbers.Matches(value))
            {
                counts.TryGetValue(match.Value, out var count);
                counts[match.Value] = count + 1;
            }
            return counts;
        }

        private static string[] Difference(Dictionary<string, int> left, Dictionary<string, int> right)
        {
            return left.Where(entry => !right.TryGetValue(entry.Key, out var count) || count < entry.Value)
                .Select(entry => entry.Key.Length > 64 ? entry.Key.Substring(0, 64) + "..." : entry.Key)
                .Take(8).ToArray();
        }
    }
}
