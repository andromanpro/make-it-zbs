using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using System.Threading;
using System.Collections;

namespace MakeItZBS.ProcessHost
{
    internal static class Program
    {
        public static int Main(string[] args)
        {
            try
            {
                if (args.Length < 3) return 64;
                PrepareChildEnvironment();
                var electronNode = args.Length > 3 && args[3] == "--electron-node";
                if (electronNode)
                {
                    Environment.SetEnvironmentVariable("ELECTRON_RUN_AS_NODE", "1");
                    Environment.SetEnvironmentVariable("VSCODE_DEV", null);
                    Environment.SetEnvironmentVariable("VSCODE_IPC_HOOK_CLI", null);
                }
                var owner = Process.GetProcessById(int.Parse(args[0], CultureInfo.InvariantCulture));
                if (owner.StartTime.ToUniversalTime().Ticks != long.Parse(args[1], CultureInfo.InvariantCulture)) return 70;
                EnterPrivateJob();
                new Thread(() => { owner.WaitForExit(); Environment.Exit(71); }) { IsBackground = true }.Start();
                using (var child = Process.Start(new ProcessStartInfo
                {
                    FileName = args[2], Arguments = string.Join(" ", args.Skip(electronNode ? 4 : 3).Select(Quote)),
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
                }))
                {
                    var input = ForwardInput(child);
                    var output = child.StandardOutput.BaseStream.CopyToAsync(Console.OpenStandardOutput());
                    var error = child.StandardError.BaseStream.CopyToAsync(Console.OpenStandardError());
                    child.WaitForExit();
                    Task.WaitAll(output, error);
                    GC.KeepAlive(input);
                    return child.ExitCode;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("Process host: " + ex.GetType().Name);
                return 70;
            }
        }

        private static void PrepareChildEnvironment()
        {
            // Роман Андриянов, 12.10.2026: схлопываем HTTP_PROXY/http_proxy только в отдельном хосте CLI.
            var groups = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .GroupBy(entry => Convert.ToString(entry.Key), StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var group in groups)
            {
                var name = group.Key;
                var upper = name.ToUpperInvariant();
                var secret = upper.Contains("API_KEY") || upper.Contains("TOKEN")
                    || upper.Contains("SECRET") || upper.Contains("PASSWORD");
                if (!secret && group.Count() == 1) continue;
                var value = Environment.GetEnvironmentVariable(name);
                foreach (var entry in group) Environment.SetEnvironmentVariable(Convert.ToString(entry.Key), null);
                if (!secret) Environment.SetEnvironmentVariable(name, value);
            }
        }

        private static async Task ForwardInput(Process child)
        {
            try
            {
                await Console.OpenStandardInput().CopyToAsync(child.StandardInput.BaseStream).ConfigureAwait(false);
                child.StandardInput.Close();
            }
            catch (IOException)
            {
                // Early CLI failure can close stdin before consuming it; preserve its exit code and stderr.
            }
        }

        private static void EnterPrivateJob()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) throw new Win32Exception();
            var limits = new ExtendedLimitInformation();
            limits.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits))
                || !AssignProcessToJobObject(job, GetCurrentProcess())) throw new Win32Exception();
            // Keep this non-inheritable handle until process exit. Closing it earlier would kill this host too.
            // The host joins before launching the CLI, so even its earliest descendants belong to the job.
        }

        private static string Quote(string argument)
        {
            var result = new StringBuilder("\"");
            var slashes = 0;
            foreach (var character in argument)
            {
                if (character == '\\') { slashes++; continue; }
                result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes);
                result.Append(character);
                slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
            public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetInformationJobObject(IntPtr job, int informationClass, ref ExtendedLimitInformation information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();
    }
}
