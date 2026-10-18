using System;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Diagnostics;
using System.Threading;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;
using ScriptEngine.HostedScript.Library;

namespace ZBSWindowsComponent
{
    [ContextClass("ТранспортOpenRouterHTTP", "OpenRouterHttpTransport")]
    public sealed class OpenRouterHttpTransport : AutoContext<OpenRouterHttpTransport>
    {
        private const int DefaultTimeoutSeconds = 600;

        [ScriptConstructor]
        public static OpenRouterHttpTransport Constructor()
        {
            return new OpenRouterHttpTransport();
        }

        [ContextMethod("Выполнить", "Send")]
        public IValue Send(
            string url,
            string method,
            string body,
            string authorization,
            string siteUrl,
            string appName,
            int timeoutSeconds = DefaultTimeoutSeconds)
        {
            ValidateOpenRouterUrl(url);

            return ExecuteForScript(() => Execute(new OpenRouterHttpRequest(
                url,
                method,
                body,
                authorization,
                siteUrl,
                appName,
                ClampTimeout(timeoutSeconds)), Operations.Current?.Token ?? CancellationToken.None));
        }

        internal static IValue ExecuteForScript(Func<OpenRouterHttpResult> execute)
        {
            try
            {
                var result = execute();
                var value = new StructureImpl();
                value.Insert("statusCode", ValueFactory.Create(result.StatusCode));
                value.Insert("body", ValueFactory.Create(result.Body ?? string.Empty));
                return value;
            }
            catch (Exception ex)
            {
                throw new RuntimeException("Ошибка соединения с OpenRouter: " + SafeMessage(ex));
            }
        }

        internal static int ClampTimeout(int timeoutSeconds)
        {
            return Math.Max(10, Math.Min(timeoutSeconds, DefaultTimeoutSeconds));
        }

        internal static OpenRouterHttpResult Execute(OpenRouterHttpRequest options,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalizedMethod = NormalizeMethod(options.Method);
            var type = Type.GetTypeFromProgID("WinHttp.WinHttpRequest.5.1");
            if (type == null)
            {
                throw new InvalidOperationException("Системный компонент WinHTTP недоступен");
            }

            object request = null;
            try
            {
                request = Activator.CreateInstance(type);
                Invoke(type, request, "Open", normalizedMethod, options.Url, true);
                var timeoutMilliseconds = checked(options.TimeoutSeconds * 1000);
                Invoke(type, request, "SetTimeouts",
                    timeoutMilliseconds, timeoutMilliseconds,
                    timeoutMilliseconds, timeoutMilliseconds);
                SetHeader(type, request, "Accept", "application/json");
                SetHeader(type, request, "Content-Type", "application/json");
                SetHeader(type, request, "User-Agent", "MakeItZBS/0.5");
                SetHeader(type, request, "Authorization", options.Authorization);
                SetHeader(type, request, "HTTP-Referer", options.SiteUrl);
                SetHeader(type, request, "X-Title", options.AppName);

                if (normalizedMethod == "POST")
                {
                    Invoke(type, request, "Send", Encoding.UTF8.GetBytes(options.Body ?? string.Empty));
                }
                else
                {
                    Invoke(type, request, "Send");
                }

                var elapsed = Stopwatch.StartNew();
                while (!Convert.ToBoolean(type.InvokeMember("WaitForResponse", BindingFlags.InvokeMethod,
                    null, request, new object[] { 0 })))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (elapsed.Elapsed.TotalSeconds >= options.TimeoutSeconds) throw new TimeoutException("HTTP request timed out.");
                    cancellationToken.WaitHandle.WaitOne(20);
                }
                cancellationToken.ThrowIfCancellationRequested();

                var statusCode = Convert.ToInt32(GetProperty(type, request, "Status"));
                var responseBytes = GetProperty(type, request, "ResponseBody") as byte[];
                var responseBody = responseBytes == null
                    ? string.Empty
                    : Encoding.UTF8.GetString(responseBytes);
                return new OpenRouterHttpResult(statusCode, responseBody);
            }
            finally
            {
                if (request != null && Marshal.IsComObject(request))
                {
                    try { Invoke(type, request, "Abort"); }
                    catch (Exception ex) { Trace.TraceWarning("WinHTTP cleanup: " + ex.GetType().Name); }
                    Marshal.FinalReleaseComObject(request);
                }
            }
        }

        internal static string NormalizeMethod(string method)
        {
            var normalizedMethod = (method ?? string.Empty).Trim().ToUpperInvariant();
            if (normalizedMethod != "GET" && normalizedMethod != "POST")
            {
                throw new ArgumentException("Поддерживаются только GET и POST", nameof(method));
            }
            return normalizedMethod;
        }

        internal static void ValidateOpenRouterUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri)
                || uri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(uri.Host, "openrouter.ai", StringComparison.OrdinalIgnoreCase))
            {
                throw new RuntimeException("Разрешены только HTTPS-запросы к openrouter.ai");
            }
        }

        private static void SetHeader(
            Type type, object request, string name, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                Invoke(type, request, "SetRequestHeader", name, value);
            }
        }

        private static void Invoke(Type type, object target, string name, params object[] args)
        {
            type.InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);
        }

        private static object GetProperty(Type type, object target, string name)
        {
            return type.InvokeMember(name, BindingFlags.GetProperty, null, target, null);
        }

        private static string SafeMessage(Exception exception)
        {
            var source = exception is TargetInvocationException && exception.InnerException != null
                ? exception.InnerException
                : exception;
            int replacementCount;
            return SecretRedactor.Redact(source.Message, out replacementCount);
        }
    }

    internal sealed class OpenRouterHttpRequest
    {
        public OpenRouterHttpRequest(string url, string method, string body, string authorization,
            string siteUrl, string appName, int timeoutSeconds)
        {
            Url = url; Method = method; Body = body; Authorization = authorization;
            SiteUrl = siteUrl; AppName = appName; TimeoutSeconds = timeoutSeconds;
        }
        public string Url { get; }
        public string Method { get; }
        public string Body { get; }
        public string Authorization { get; }
        public string SiteUrl { get; }
        public string AppName { get; }
        public int TimeoutSeconds { get; }
    }

    internal sealed class OpenRouterHttpResult
    {
        public OpenRouterHttpResult(int statusCode, string body)
        {
            StatusCode = statusCode;
            Body = body;
        }

        public int StatusCode { get; }
        public string Body { get; }
    }
}
