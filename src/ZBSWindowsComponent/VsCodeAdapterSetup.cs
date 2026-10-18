using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    internal sealed class VsCodeAdapterStatus
    {
        public bool EditorFound { get; set; }
        public bool Installed { get; set; }
        public bool Current { get; set; }
        public bool ReloadRequired { get; set; }
        public string Version { get; set; } = string.Empty;
        public string Error { get; set; } = string.Empty;
    }

    internal sealed class VsCodeAdapterSetup
    {
        private const string ExtensionId = "androman.make-it-zbs-editor";
        private static readonly SemaphoreSlim InstallGate = new SemaphoreSlim(1, 1);
        private readonly Func<OwnedProcessRequest, ProcessResult> _run;
        private readonly string _codeCommand;
        private readonly string[] _extraArguments;
        internal VsCodeAdapterSetup() : this(FindCommand(), new string[0]) { }
        internal VsCodeAdapterSetup(string codeCommand, string[] extraArguments)
            : this(codeCommand, request => OwnedProcessRunner.Run(request), extraArguments) { }
        internal VsCodeAdapterSetup(string codeCommand, Func<OwnedProcessRequest, ProcessResult> run, params string[] extraArguments)
        {
            _codeCommand = codeCommand;
            _run = run;
            _extraArguments = extraArguments;
        }

        internal VsCodeAdapterStatus Check()
        {
            var status = new VsCodeAdapterStatus { EditorFound = TryResolveCommand(_codeCommand, out var executable, out var script) };
            if (!status.EditorFound) return status;
            try
            {
                var result = Execute(executable, script, "--list-extensions", "--show-versions");
                if (result.TimedOut || result.ExitCode != 0) { status.Error = "check-failed"; return status; }
                var prefix = ExtensionId + "@";
                var installed = (result.Output ?? string.Empty).Split('\n')
                    .Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
                status.Installed = installed != null;
                status.Version = installed == null ? string.Empty : installed.Substring(prefix.Length);
                status.Current = Version.TryParse(status.Version, out var actual)
                    && Version.TryParse(BundledVersion(), out var bundled) && actual >= bundled;
            }
            catch (Exception) { status.Error = "check-failed"; }
            return status;
        }

        internal VsCodeAdapterStatus Install()
        {
            if (!InstallGate.Wait(0)) return new VsCodeAdapterStatus { EditorFound = true, Error = "busy" };
            string temporary = null;
            try
            {
                var status = Check();
                if (!status.EditorFound || status.Current || status.Error.Length > 0) return status;
                if (!TryResolveCommand(_codeCommand, out var executable, out var script)) return new VsCodeAdapterStatus();
                temporary = TemporaryFileStore.CreatePath("vscode-adapter", "vsix");
                File.WriteAllBytes(temporary, BundledPackage());
                var result = Execute(executable, script, "--install-extension", temporary, "--force");
                if (result.TimedOut || result.ExitCode != 0) return new VsCodeAdapterStatus { EditorFound = true, Error = "install-failed" };
                status = Check();
                status.ReloadRequired = status.Current;
                if (!status.Current) status.Error = "verification-failed";
                return status;
            }
            catch (Exception) { return new VsCodeAdapterStatus { EditorFound = true, Error = "install-failed" }; }
            finally
            {
                try { if (temporary != null) File.Delete(temporary); }
                catch (Exception ex) { Trace.TraceWarning("Editor adapter temporary cleanup: " + ex.GetType().Name); }
                InstallGate.Release();
            }
        }

        private ProcessResult Execute(string executable, string script, params string[] arguments) => _run(new OwnedProcessRequest
        {
            Executable = executable, Arguments = new[] { script }.Concat(arguments).Concat(_extraArguments).ToArray(),
            ElectronNode = true, TimeoutMilliseconds = 60000, WorkingDirectory = Path.GetDirectoryName(executable)
        });

        internal static byte[] BundledPackage()
        {
            using (var stream = typeof(VsCodeAdapterSetup).Assembly.GetManifestResourceStream("ZBSWindowsComponent.vscode.vsix"))
            using (var memory = new MemoryStream())
            {
                if (stream == null) throw new InvalidOperationException("Bundled editor adapter is missing.");
                stream.CopyTo(memory);
                return memory.ToArray();
            }
        }

        internal static string BundledVersion()
        {
            using (var memory = new MemoryStream(BundledPackage()))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
            using (var stream = archive.GetEntry("extension/package.json").Open())
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                return JObject.Parse(reader.ReadToEnd()).Value<string>("version");
        }

        internal static string FindCommand()
        {
            var roots = new[] { Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) };
            var candidates = new List<string> { Path.Combine(roots[0], "Programs", "Microsoft VS Code", "bin", "code.cmd") };
            candidates.AddRange(roots.Skip(1).Where(root => root.Length > 0).Select(root => Path.Combine(root, "Microsoft VS Code", "bin", "code.cmd")));
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';'))
            {
                try { if (directory.Length > 0) candidates.Add(Path.Combine(directory.Trim('"'), "code.cmd")); }
                catch (ArgumentException) { Trace.TraceWarning("Ignored invalid editor CLI PATH entry."); }
            }
            return candidates.FirstOrDefault(candidate => TryResolveCommand(candidate, out _, out _));
        }

        internal static bool TryResolveCommand(string command, out string executable, out string script)
        {
            executable = null; script = null;
            try
            {
                if (string.IsNullOrEmpty(command) || !File.Exists(command) || new FileInfo(command).Length > 16384) return false;
                var bin = Path.GetDirectoryName(Path.GetFullPath(command));
                var root = Path.GetDirectoryName(bin);
                var match = Regex.Match(File.ReadAllText(command), "\"%~dp0(?<exe>[^\"\r\n]+)\"[ \\t]+\"%~dp0(?<cli>[^\"\r\n]+)\"",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(50));
                if (!match.Success) return false;
                var code = Path.GetFullPath(Path.Combine(bin, match.Groups["exe"].Value));
                var cli = Path.GetFullPath(Path.Combine(bin, match.Groups["cli"].Value));
                if (!string.Equals(code, Path.Combine(root, "Code.exe"), StringComparison.OrdinalIgnoreCase)
                    || !cli.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || !cli.EndsWith(Path.Combine("resources", "app", "out", "cli.js"), StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(code) || !File.Exists(cli)) return false;
                executable = code; script = cli;
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
