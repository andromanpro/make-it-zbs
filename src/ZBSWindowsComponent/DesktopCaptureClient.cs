using System;
using System.Drawing;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using MakeItZBS.Capture;
using Newtonsoft.Json;

namespace ZBSWindowsComponent
{
    internal static class DesktopCaptureClient
    {
        private const string DiagnosticCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789:-";
        internal static CaptureMetadata Capture(CaptureRequest request, string outputPath,
            CancellationToken cancellation, Action validateSource = null,
            Func<OwnedProcessRequest, CancellationToken, ProcessResult> runner = null)
        {
            CaptureEngine.Validate(request);
            cancellation.ThrowIfCancellationRequested();
            validateSource?.Invoke();
            var temporary = TemporaryFileStore.CreatePath("capture-worker", "png");
            var started = DateTime.UtcNow;
            var elapsed = Stopwatch.StartNew();
            request.OutputPath = temporary;
            try
            {
                var executable = Path.Combine(Path.GetDirectoryName(typeof(DesktopCaptureClient).Assembly.Location),
                    "MakeItZBS.CaptureHost.exe");
                var execute = runner ?? OwnedProcessRunner.Run;
                var result = execute(new OwnedProcessRequest
                {
                    Executable = executable,
                    Input = JsonConvert.SerializeObject(request) + "\n",
                    TimeoutMilliseconds = 8000
                }, cancellation);
                var workerFallback = result.TimedOut && request.Backend == CaptureBackend.Auto;
                if (workerFallback)
                {
                    cancellation.ThrowIfCancellationRequested();
                    validateSource?.Invoke();
                    File.Delete(temporary);
                    var fallback = JsonConvert.DeserializeObject<CaptureRequest>(JsonConvert.SerializeObject(request));
                    fallback.Backend = CaptureBackend.Gdi;
                    result = execute(new OwnedProcessRequest
                    {
                        Executable = executable, Input = JsonConvert.SerializeObject(fallback) + "\n",
                        TimeoutMilliseconds = 5000
                    }, cancellation);
                }
                if (result.TimedOut) throw new TimeoutException("Screen capture worker timed out.");
                if (result.ExitCode != 0) throw new InvalidOperationException("Screen capture failed: " + SafeError(result.Error));
                var metadata = JsonConvert.DeserializeObject<CaptureMetadata>(result.Output);
                if (metadata == null || metadata.Version != 1 || metadata.Bounds != request.Bounds
                    || metadata.WindowHandle != request.WindowHandle || metadata.ProcessId != request.ProcessId
                    || (metadata.Backend != "dxgi" && metadata.Backend != "gdi")
                    || metadata.CapturedAtUtc < started.AddSeconds(-1) || metadata.CapturedAtUtc > DateTime.UtcNow.AddSeconds(1))
                    throw new InvalidDataException("Invalid capture worker response.");
                if ((workerFallback && metadata.Backend != "gdi") || (request.Backend != CaptureBackend.Auto
                    && metadata.Backend != request.Backend.ToString().ToLowerInvariant()))
                    throw new InvalidDataException("Capture worker used an unexpected backend.");
                if (workerFallback) metadata.FallbackReason = "dxgi-worker-timeout";
                using (var image = Image.FromFile(temporary))
                    if (image.Width != request.Bounds.Width || image.Height != request.Bounds.Height)
                        throw new InvalidDataException("Capture image dimensions do not match the request.");
                cancellation.ThrowIfCancellationRequested();
                validateSource?.Invoke();
                Publish(temporary, outputPath, cancellation, validateSource);
                metadata.ElapsedMilliseconds = elapsed.ElapsedMilliseconds;
                return metadata;
            }
            finally { File.Delete(temporary); }
        }

        private static void Publish(string source, string destination, CancellationToken cancellation, Action validateSource)
        {
            destination = Path.GetFullPath(destination);
            var directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);
            var pending = Path.Combine(directory, ".zbs-capture-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, pending);
                cancellation.ThrowIfCancellationRequested();
                validateSource?.Invoke();
                if (File.Exists(destination)) File.Replace(pending, destination, null);
                else File.Move(pending, destination);
            }
            finally { File.Delete(pending); }
        }

        private static string SafeError(string error)
        {
            var value = (error ?? string.Empty).Trim();
            return value.Length > 0 && value.Length <= 100 && value.All(character => DiagnosticCharacters.IndexOf(character) >= 0)
                ? value : "capture-worker-error";
        }
    }
}
