using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace FakeCodex
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.SequenceEqual(new[] { "electron-environment" }))
            {
                Console.WriteLine(Environment.GetEnvironmentVariable("ELECTRON_RUN_AS_NODE") + "|"
                    + Environment.GetEnvironmentVariable("VSCODE_DEV") + "|"
                    + Environment.GetEnvironmentVariable("VSCODE_IPC_HOOK_CLI"));
                return 0;
            }
            if (args.SequenceEqual(new[] { "hold-output" }))
            {
                Thread.Sleep(5000);
                return 0;
            }

            var mode = Environment.GetEnvironmentVariable("ZBS_FAKE_MODE") ?? string.Empty;
            if (args.SequenceEqual(new[] { "login", "status" }))
            {
                return WriteLoginStatus(mode);
            }

            if (args.SequenceEqual(new[] { "--version" }))
            {
                Console.WriteLine("codex-cli test-1.0");
                return 0;
            }

            if (args.Length >= 2 && args[0] == "debug" && args[1] == "models")
            {
                return WriteModelCatalog(args, mode);
            }

            return args.Length > 0 && args[0] == "exec"
                ? RunExec(args, mode)
                : 64;
        }

        private static int WriteLoginStatus(string mode)
        {
            if (mode == "unauthenticated")
            {
                Console.Error.WriteLine("Not logged in");
                return 1;
            }

            Console.WriteLine(mode == "api-auth"
                ? "Logged in using API billing"
                : "Logged in using ChatGPT");
            return 0;
        }

        private static int WriteModelCatalog(string[] args, string mode)
        {
            if (mode == "catalog-fallback" && !args.Contains("--bundled"))
            {
                Console.Error.WriteLine("primary catalog unavailable");
                return 2;
            }

            Console.WriteLine(mode == "catalog-empty"
                ? "{\"models\":[]}"
                : "{\"models\":[{\"slug\":\"gpt-test\",\"display_name\":\"GPT Test\","
                    + "\"default_reasoning_level\":\"medium\",\"visibility\":\"list\","
                    + "\"supported_reasoning_levels\":[{\"effort\":\"low\"},{\"effort\":\"high\"}]}]}");
            return 0;
        }

        private static int RunExec(string[] args, string mode)
        {
            if (mode == "hold-output" || mode == "spawn-stall")
            {
                using (var child = Process.Start(new ProcessStartInfo
                {
                    FileName = typeof(Program).Assembly.Location,
                    Arguments = "hold-output",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true
                }))
                {
                    File.WriteAllText(Path.Combine(Environment.GetEnvironmentVariable("CODEX_HOME"), "child.pid"),
                        child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }
                if (mode == "spawn-stall") Thread.Sleep(30000);
                return 0;
            }

            if (mode == "stall-input")
            {
                Thread.Sleep(5000);
            }
            if (mode == "fail-before-input")
            {
                Console.Error.WriteLine("rejected before reading input");
                return 2;
            }

            var prompt = Console.In.ReadToEnd();
            if (mode == "sleep")
            {
                Thread.Sleep(5000);
            }
            if (mode == "fail")
            {
                Console.Error.WriteLine("simulated Codex failure");
                return 2;
            }
            if (mode == "fail-secret")
            {
                Console.Error.WriteLine("request failed: password=synthetic-credential==");
                return 2;
            }

            var outputPath = ReadOption(args, "-o");
            if (string.IsNullOrEmpty(outputPath) || mode == "empty")
            {
                return 0;
            }

            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") ?? Path.GetTempPath();
            Directory.CreateDirectory(codexHome);
            File.WriteAllText(
                Path.Combine(codexHome, "last-invocation.txt"),
                string.Join("|", args) + Environment.NewLine + prompt,
                Encoding.UTF8);

            var content = prompt.StartsWith("$imagegen", StringComparison.Ordinal)
                ? CreateGeneratedImage(codexHome)
                : BuildTextResponse(args, prompt);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            File.WriteAllText(outputPath, content, Encoding.UTF8);
            Console.WriteLine(content);
            return 0;
        }

        private static string BuildTextResponse(string[] args, string prompt)
        {
            var leakedSecret = Environment.GetEnvironmentVariable("ZBS_FAKE_API_KEY");
            return (string.IsNullOrEmpty(leakedSecret) ? "SECRET_FILTERED" : leakedSecret)
                + "\nARGS=" + string.Join("|", args)
                + "\nPROMPT=" + prompt;
        }

        private static string CreateGeneratedImage(string codexHome)
        {
            var directory = Path.Combine(codexHome, "generated_images", "session");
            var path = Path.Combine(directory, "generated.png");
            Directory.CreateDirectory(directory);
            using (var bitmap = new Bitmap(10, 6))
            {
                bitmap.SetPixel(0, 0, Color.Crimson);
                bitmap.Save(path, ImageFormat.Png);
            }
            return Path.GetFullPath(path);
        }

        private static string ReadOption(string[] args, string name)
        {
            var index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : string.Empty;
        }
    }
}
