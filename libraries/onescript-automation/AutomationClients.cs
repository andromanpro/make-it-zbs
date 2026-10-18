using System;
using System.Drawing;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using ScriptEngine.Machine.Contexts;
using MakeItZBS.Capture;
using ZBSWindowsComponent;

namespace OneScript.Automation
{
    [ContextClass("КлиентыАвтоматизации", "AutomationClients")]
    public sealed class AutomationClients : AutoContext<AutomationClients>
    {
        private readonly Func<OpenRouterHttpRequest, CancellationToken, OpenRouterHttpResult> _http;

        public AutomationClients() : this(OpenRouterHttpTransport.Execute) { }
        internal AutomationClients(Func<OpenRouterHttpRequest, CancellationToken, OpenRouterHttpResult> http)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
        }

        [ScriptConstructor]
        public static AutomationClients Constructor() => new AutomationClients();

        [ContextMethod("ЗахватитьОкно", "CaptureWindow")]
        public AutomationJob CaptureWindow(string outputPath, string backend = "auto")
        {
            outputPath = ValidatePngPath(outputPath);
            var selectedBackend = ParseBackend(backend);
            var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DpiAwarenessContextPerMonitorAwareV2);
            CaptureRequest request;
            try
            {
                var window = NativeMethods.GetForegroundWindow();
                if (window == IntPtr.Zero || !NativeMethods.GetWindowRect(window, out var bounds))
                    throw new InvalidOperationException("No capturable foreground window.");
                NativeMethods.GetWindowThreadProcessId(window, out var processId);
                request = new CaptureRequest
                {
                    WindowHandle = window.ToInt64(), ProcessId = processId, Backend = selectedBackend,
                    Bounds = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom)
                };
                CaptureEngine.Validate(request);
            }
            finally
            {
                if (previous != IntPtr.Zero) NativeMethods.SetThreadDpiAwarenessContext(previous);
            }
            return new AutomationJob(token => JsonConvert.SerializeObject(
                DesktopCaptureClient.Capture(request, outputPath, token)));
        }

        [ContextMethod("ЗапросCodex", "StartCodex")]
        public AutomationJob StartCodex(string instruction, string input, string model = "",
            string reasoning = "", string imagePath = "", int timeoutSeconds = 180)
        {
            if (timeoutSeconds < 10 || timeoutSeconds > 600) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            if (instruction == null) throw new ArgumentNullException(nameof(instruction));
            if (input == null) throw new ArgumentNullException(nameof(input));
            return new AutomationJob(token =>
            {
                var result = CodexCliClient.Execute(instruction, input, model, timeoutSeconds, imagePath, reasoning);
                token.ThrowIfCancellationRequested();
                return JsonConvert.SerializeObject(new { success = result.Success, content = result.Content, diagnostic = result.Diagnostic });
            });
        }

        [ContextMethod("ЗапросOpenRouter", "StartOpenRouter")]
        public AutomationJob StartOpenRouter(string url, string body, string authorization, int timeoutSeconds = 180)
        {
            OpenRouterHttpTransport.ValidateOpenRouterUrl(url);
            if (timeoutSeconds < 10 || timeoutSeconds > 600) throw new ArgumentOutOfRangeException(nameof(timeoutSeconds));
            return new AutomationJob(token =>
            {
                var result = _http(new OpenRouterHttpRequest(
                    url, "POST", body, authorization, "", "OneScript.Automation", timeoutSeconds), token);
                return JsonConvert.SerializeObject(new { statusCode = result.StatusCode, body = result.Body });
            });
        }

        [ContextMethod("ПрофильКлиента", "ResolveChatProfile")]
        public string ResolveChatProfile(string executable, string title)
        {
            return AiClientProfiles.ResolveProfileName(
                new Newtonsoft.Json.Linq.JObject { ["profiles"] = AiClientProfiles.CreateDefaults("continue", "", "") },
                executable, title);
        }

        internal static string ValidatePngPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)
                || !string.Equals(Path.GetExtension(path), ".png", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("An absolute PNG output path is required.", nameof(path));
            return Path.GetFullPath(path);
        }

        internal static CaptureBackend ParseBackend(string value)
        {
            switch (value)
            {
                case "auto": return CaptureBackend.Auto;
                case "dxgi": return CaptureBackend.Dxgi;
                case "gdi": return CaptureBackend.Gdi;
                default: throw new ArgumentException("Backend must be auto, dxgi or gdi.", nameof(value));
            }
        }
    }
}
