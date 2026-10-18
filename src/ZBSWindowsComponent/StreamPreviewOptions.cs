using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

namespace ZBSWindowsComponent
{
    internal sealed class StreamPreviewOptions
    {
        public string Url { get; private set; }
        public string AuthHeader { get; private set; }
        public string BodyJson { get; private set; }
        public string Original { get; private set; }
        public string Context { get; private set; }
        public string Model { get; private set; }
        public int TimeoutSec { get; private set; }
        public bool AllowApply { get; private set; }

        public static StreamPreviewOptions FromRuntimeContext(
            IRuntimeContextInstance request,
            string original,
            string context,
            string model,
            int timeoutSec,
            bool allowApply)
        {
            if (request == null)
            {
                throw new RuntimeException("Параметры stream-запроса не заданы");
            }

            return new StreamPreviewOptions
            {
                Url = ReadString(request, "url"),
                AuthHeader = ReadString(request, "authHeader"),
                BodyJson = ReadString(request, "bodyJson"),
                Original = original ?? string.Empty,
                Context = context ?? string.Empty,
                Model = model ?? string.Empty,
                TimeoutSec = timeoutSec,
                AllowApply = allowApply
            };
        }

        private static string ReadString(IRuntimeContextInstance source, string propertyName)
        {
            int propertyIndex;
            try
            {
                propertyIndex = source.FindProperty(propertyName);
            }
            catch (PropertyAccessException)
            {
                propertyIndex = -1;
            }
            if (propertyIndex < 0)
            {
                throw new RuntimeException(
                    "В параметрах stream-запроса отсутствует поле " + propertyName);
            }

            var value = source.GetPropValue(propertyIndex);
            return value == null ? string.Empty : value.AsString();
        }
    }
}
