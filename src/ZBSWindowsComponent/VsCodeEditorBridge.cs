using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    internal sealed class VsCodeCapture
    {
        public JObject Endpoint { get; set; }
        public string CaptureId { get; set; }
        public string Uri { get; set; }
        public int Version { get; set; }
    }

    internal static class VsCodeEditorBridge
    {
        internal static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MakeItZBS", "EditorBridge");
        private const int ReplyLimit = 5 * 1024 * 1024;
        private static readonly Regex PipeName = new Regex(@"\Amake-it-zbs-vscode-[a-f0-9]{32}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        private static readonly Regex Token = new Regex(@"\A[a-f0-9]{64}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

        internal static ReplacementSnapshot Capture(int handle, int processId, string mode,
            CancellationToken cancellation, out bool available, string directory = null)
        {
            available = false;
            if (!System.IO.Directory.Exists(directory ?? DirectoryPath)) return null;
            ReplacementSnapshot captured = null;
            try
            {
                foreach (var file in System.IO.Directory.EnumerateFiles(directory ?? DirectoryPath, "vscode-*.json")
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(32))
                {
                    if (!TryReadEndpoint(file, out var endpoint)) continue;
                    var verified = false;
                    TryExchange(endpoint, processId, new JObject { ["cmd"] = "capture", ["mode"] = mode }, cancellation,
                        out var reply, () => verified = true);
                    if (verified) available = true;
                    if (reply == null) continue;
                    var snapshot = ParseCapture(reply, endpoint, handle, processId, mode);
                    if (snapshot == null) continue;
                    if (captured != null) return null; // Ambiguous focused windows never authorize an edit.
                    captured = snapshot;
                }
                return captured;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { available = true; return null; }
        }

        internal static bool TryReadEndpoint(string file, out JObject endpoint)
        {
            endpoint = null;
            try
            {
                if (new FileInfo(file).Length > 4096) return false;
                var value = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                var valid = value.Value<int?>("protocol") == 1 && value.Value<int?>("pid") > 0
                    && PipeName.IsMatch(value.Value<string>("pipe") ?? "")
                    && Token.IsMatch(value.Value<string>("token") ?? "");
                if (valid) endpoint = value;
                return valid;
            }
            catch (Exception) { return false; }
        }

        private static ReplacementSnapshot ParseCapture(JObject reply, JObject endpoint, int handle, int pid, string mode)
        {
            if (reply.Value<bool?>("ok") != true || !Guid.TryParse(reply.Value<string>("captureId"), out _)
                || string.IsNullOrEmpty(reply.Value<string>("uri")) || (reply.Value<int?>("version") ?? 0) < 1
                || (reply.Value<int?>("start") ?? -1) < 0 || (reply.Value<int?>("end") ?? 0) <= reply.Value<int?>("start")
                || (reply.Value<int?>("end") ?? 1000001) > 1000000 || reply.Value<string>("mode") != mode
                || !ValidFingerprint(reply.Value<string>("document")) || !ValidFingerprint(reply.Value<string>("selection"))) return null;
            return new ReplacementSnapshot
            {
                Handle = handle, ProcessId = pid, Mode = mode,
                Identity = reply.Value<string>("uri") + ":" + reply.Value<int>("version"),
                Document = reply.Value<string>("document"), Selection = reply.Value<string>("selection"),
                SelectionStart = reply.Value<int>("start"),
                Editor = new VsCodeCapture { Endpoint = endpoint, CaptureId = reply.Value<string>("captureId"),
                    Uri = reply.Value<string>("uri"), Version = reply.Value<int>("version") }
            };
        }

        private static bool ValidFingerprint(string value)
        {
            try { return value != null && Convert.FromBase64String(value).Length == 32; }
            catch (FormatException) { return false; }
        }

        internal static bool Apply(ReplacementSnapshot snapshot, string text, CancellationToken cancellation)
        {
            return TryExchange(snapshot.Editor.Endpoint, snapshot.ProcessId,
                new JObject { ["cmd"] = "apply", ["captureId"] = snapshot.Editor.CaptureId, ["text"] = text }, cancellation, out var reply)
                && reply["ok"]?.Type == JTokenType.Boolean && reply.Value<bool>("ok");
        }

        internal static bool TryExchange(JObject endpoint, int expectedPid, JObject message, CancellationToken cancellation,
            out JObject reply, Action onVerified = null)
        {
            reply = null;
            cancellation.ThrowIfCancellationRequested();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var pipe = new NamedPipeClientStream(".", endpoint.Value<string>("pipe"), PipeDirection.InOut, PipeOptions.Asynchronous))
            {
                deadline.CancelAfter(2000);
                try
                {
                    using (deadline.Token.Register(() => pipe.Dispose()))
                    {
                        pipe.Connect(200);
                        if (!VerifyServer(pipe, endpoint.Value<int>("pid"), expectedPid)) return false;
                        onVerified?.Invoke();
                        message["token"] = endpoint.Value<string>("token");
                        reply = ExchangeAsync(pipe, message, deadline.Token).GetAwaiter().GetResult();
                        cancellation.ThrowIfCancellationRequested();
                        return reply != null;
                    }
                }
                catch (Exception)
                {
                    cancellation.ThrowIfCancellationRequested();
                    return false;
                }
            }
        }

        private static async Task<JObject> ExchangeAsync(Stream pipe, JObject message, CancellationToken cancellation)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToString(Newtonsoft.Json.Formatting.None) + "\n");
            if (bytes.Length > ReplyLimit) return null;
            await pipe.WriteAsync(bytes, 0, bytes.Length, cancellation).ConfigureAwait(false);
            using (var reply = new MemoryStream())
            {
                var buffer = new byte[4096];
                while (reply.Length <= ReplyLimit)
                {
                    var count = await pipe.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false);
                    if (count == 0) return null;
                    var newline = Array.IndexOf(buffer, (byte)10, 0, count);
                    await reply.WriteAsync(buffer, 0, newline < 0 ? count : newline, cancellation).ConfigureAwait(false);
                    if (reply.Length > ReplyLimit) return null;
                    if (newline >= 0) return JObject.Parse(Encoding.UTF8.GetString(reply.ToArray()));
                }
                return null;
            }
        }

        private static bool VerifyServer(NamedPipeClientStream pipe, int endpointPid, int expectedPid)
        {
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var actual) || actual != endpointPid) return false;
            using (var server = Process.GetProcessById(endpointPid))
            using (var target = Process.GetProcessById(expectedPid))
                return string.Equals(server.MainModule.FileName, target.MainModule.FileName, StringComparison.OrdinalIgnoreCase);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    }
}
