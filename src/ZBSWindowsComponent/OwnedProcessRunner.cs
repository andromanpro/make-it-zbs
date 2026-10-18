using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ZBSWindowsComponent
{
    internal sealed class OwnedProcessRequest
    {
        public string Executable { get; set; }
        public IEnumerable<string> Arguments { get; set; } = new string[0];
        public string Input { get; set; }
        public int TimeoutMilliseconds { get; set; } = 30000;
        public string WorkingDirectory { get; set; }
        public bool ElectronNode { get; set; }
    }

    internal sealed class ProcessResult
    {
        public int ExitCode { get; set; }
        public bool TimedOut { get; set; }
        public string Output { get; set; }
        public string Error { get; set; }
    }

    internal static class OwnedProcessRunner
    {
        internal static ProcessResult Run(OwnedProcessRequest request, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            var hostPath = Path.Combine(Path.GetDirectoryName(typeof(OwnedProcessRunner).Assembly.Location), "MakeItZBS.ProcessHost.exe");
            if (!File.Exists(hostPath)) throw new FileNotFoundException("The private CLI process host is missing.");
            var startInfo = new ProcessStartInfo
            {
                FileName = hostPath,
                Arguments = string.Join(" ", HostArguments(request).Select(QuoteArgument)),
                WorkingDirectory = request.WorkingDirectory ?? Path.GetDirectoryName(request.Executable),
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = request.Input != null,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
            };
            using (var process = new Process { StartInfo = startInfo })
            {
                var elapsed = Stopwatch.StartNew();
                process.Start();
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var input = request.Input == null ? Task.FromResult(string.Empty) : WriteInputAsync(process.StandardInput, request.Input);
                var io = Task.WhenAll(input, output, error);
                while (!process.HasExited && !cancellation.IsCancellationRequested && Remaining(elapsed, request.TimeoutMilliseconds) > 0)
                    process.WaitForExit(25);
                if (cancellation.IsCancellationRequested || !process.HasExited
                    || !io.Wait(Remaining(elapsed, request.TimeoutMilliseconds), cancellation))
                {
                    if (!process.HasExited) { TryKill(process); process.WaitForExit(1000); }
                    ObserveFailure(io);
                    cancellation.ThrowIfCancellationRequested();
                    return new ProcessResult { TimedOut = true, ExitCode = -1, Output = Completed(output), Error = Completed(error) };
                }
                cancellation.ThrowIfCancellationRequested();
                var inputError = input.GetAwaiter().GetResult();
                return new ProcessResult
                {
                    ExitCode = process.ExitCode == 0 && inputError.Length > 0 ? -1 : process.ExitCode,
                    Output = output.GetAwaiter().GetResult(),
                    Error = process.ExitCode == 0 && inputError.Length > 0 ? inputError : error.GetAwaiter().GetResult()
                };
            }
        }

        private static IEnumerable<string> HostArguments(OwnedProcessRequest request)
        {
            using (var owner = Process.GetCurrentProcess())
            {
                var args = new List<string> { owner.Id.ToString(CultureInfo.InvariantCulture),
                    owner.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture), request.Executable };
                if (request.ElectronNode) args.Add("--electron-node");
                args.AddRange(request.Arguments);
                return args;
            }
        }

        private static async Task<string> WriteInputAsync(StreamWriter writer, string input)
        {
            try
            {
                using (writer)
                {
                    var bytes = Encoding.UTF8.GetBytes(input);
                    await writer.BaseStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }
                return string.Empty;
            }
            catch (IOException ex) { return "Could not send CLI input: " + ex.Message; }
            catch (ObjectDisposedException) { return "CLI input closed before it was sent."; }
        }

        private static int Remaining(Stopwatch elapsed, int timeout) => (int)Math.Max(0, timeout - elapsed.ElapsedMilliseconds);
        private static string Completed(Task<string> task) => task.Status == TaskStatus.RanToCompletion ? task.GetAwaiter().GetResult() : string.Empty;
        private static void ObserveFailure(Task task) => task.ContinueWith(failed =>
            Trace.TraceWarning("CLI pipe cleanup: " + failed.Exception.GetBaseException().GetType().Name),
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        private static void TryKill(Process process)
        {
            try { process.Kill(); }
            catch (Exception ex) { Trace.TraceWarning("CLI termination: " + ex.GetType().Name); }
        }

        internal static string QuoteArgument(string value)
        {
            value = value ?? string.Empty;
            if (value.Length > 0 && value.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return value;
            var result = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var character in value)
            {
                if (character == '\\') { backslashes++; continue; }
                result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
                result.Append(character);
                backslashes = 0;
            }
            return result.Append('\\', backslashes * 2).Append('"').ToString();
        }
    }
}
