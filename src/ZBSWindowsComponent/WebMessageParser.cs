using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    internal static class WebMessageParser
    {
        internal static JObject ReadObject(string webMessageAsJson)
        {
            var token = JToken.Parse(webMessageAsJson);
            if (token.Type == JTokenType.String)
            {
                var raw = token.Value<string>();
                if (string.IsNullOrEmpty(raw))
                {
                    return new JObject();
                }

                token = JToken.Parse(raw);
            }

            return token as JObject ?? new JObject();
        }

        internal static string ReadCommand(string webMessageAsJson)
        {
            return ReadCommand(ReadObject(webMessageAsJson));
        }

        internal static string ReadCommand(JObject message)
        {
            var command = message?["cmd"];
            return command == null ? string.Empty : command.ToString();
        }
    }
}
