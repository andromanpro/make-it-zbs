using System;
using System.Text.RegularExpressions;

namespace ZBSWindowsComponent
{
    internal static class SecretRedactor
    {
        private const string Replacement = "[REDACTED]";
        private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

        private static readonly Regex[] Patterns =
        {
            new Regex(@"-----BEGIN [^-]*PRIVATE KEY-----[\s\S]*?-----END [^-]*PRIVATE KEY-----",
                RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),
            new Regex(@"\bBearer\s+[A-Za-z0-9._~+\-/=]{16,}",
                RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),
            new Regex(@"\bsk-(?:or-v1-)?[A-Za-z0-9_-]{16,}", RegexOptions.Compiled, MatchTimeout),
            new Regex(@"\b(?:(?:gh[pousr]|gitea)_|glpat[-_])[A-Za-z0-9_-]{16,}",
                RegexOptions.Compiled | RegexOptions.IgnoreCase, MatchTimeout),
            new Regex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b",
                RegexOptions.Compiled, MatchTimeout)
        };

        private static readonly Regex Assignment = new Regex(
            @"(?<prefix>(?<![\w-])(?<keyQuote>[""']?)(?:[a-z][a-z0-9]*[_-])*"
            + @"(?:api[_-]?key|token|secret|password|passwd)\k<keyQuote>[ \t]*[:=][ \t]*)"
            + @"(?:""(?<double>(?:\\.|[^""\\])*)""|'(?<single>(?:\\.|[^'\\])*)'|\[REDACTED\]|[^\s,;}\]]+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            MatchTimeout);

        public static string Redact(string text, out int replacementCount)
        {
            var result = text ?? string.Empty;
            var count = 0;

            try
            {
                result = Assignment.Replace(result, match =>
                {
                    var quote = string.Empty;
                    if (match.Groups["double"].Success)
                    {
                        quote = "\"";
                    }
                    else if (match.Groups["single"].Success)
                    {
                        quote = "'";
                    }
                    var replacement = match.Groups["prefix"].Value + quote + Replacement + quote;
                    if (!string.Equals(match.Value, replacement, StringComparison.Ordinal))
                    {
                        count++;
                    }
                    return replacement;
                });

                foreach (var pattern in Patterns)
                {
                    result = pattern.Replace(result, match =>
                    {
                        count++;
                        return Replacement;
                    });
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // A failed privacy filter must never fall back to the original input.
                result = Replacement;
                count++;
            }

            replacementCount = count;
            return result;
        }
    }
}
