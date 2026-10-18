using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    internal sealed class OperationFeedbackOptions
    {
        internal const string Key = "operationFeedback";
        internal string Mode { get; }
        internal bool ShowIcon => Mode == "both" || Mode == "icon";
        internal bool ShowPopup => Mode == "both" || Mode == "popup";
        internal OperationFeedbackOptions(string mode) { Mode = Normalize(mode); }
        internal static string Normalize(string mode)
        {
            switch (mode?.Trim().ToLowerInvariant())
            {
                case "icon": return "icon";
                case "popup": return "popup";
                case "none": return "none";
                default: return "both";
            }
        }
        internal static OperationFeedbackOptions FromSettings(JObject settings) =>
            new OperationFeedbackOptions(AiClientProfiles.ReadString(settings?["general"] as JObject, Key, "both"));
        internal static string FormatElapsed(TimeSpan elapsed)
        {
            var seconds = Math.Max(0, (long)elapsed.TotalSeconds);
            return seconds >= 3600
                ? string.Format(CultureInfo.InvariantCulture, "{0}:{1:00}:{2:00}", seconds / 3600, seconds / 60 % 60, seconds % 60)
                : string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}", seconds / 60, seconds % 60);
        }
    }
}
