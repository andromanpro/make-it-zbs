using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using ScriptEngine.Machine;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    [NonParallelizable]
    public class CodexCliProcessTests
    {
        private string _root;
        private string _imagePath;
        private string _originalCodexBin;
        private string _originalCodexHome;
        private string _originalMode;
        private string _originalFakeSecret;

        [SetUp]
        public void SetUp()
        {
            _originalCodexBin = Environment.GetEnvironmentVariable("CODEX_BIN");
            _originalCodexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            _originalMode = Environment.GetEnvironmentVariable("ZBS_FAKE_MODE");
            _originalFakeSecret = Environment.GetEnvironmentVariable("ZBS_FAKE_API_KEY");
            _root = Path.Combine(Path.GetTempPath(), "zbs-fake-codex-" + Guid.NewGuid().ToString("N"));
            _imagePath = Path.Combine(_root, "reference.png");
            Directory.CreateDirectory(_root);
            using (var bitmap = new Bitmap(8, 5))
            {
                bitmap.Save(_imagePath, ImageFormat.Png);
            }

            Environment.SetEnvironmentVariable(
                "CODEX_BIN", Path.Combine(TestContext.CurrentContext.TestDirectory, "FakeCodex.exe"));
            Environment.SetEnvironmentVariable("CODEX_HOME", _root);
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", null);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("CODEX_BIN", _originalCodexBin);
            Environment.SetEnvironmentVariable("CODEX_HOME", _originalCodexHome);
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", _originalMode);
            Environment.SetEnvironmentVariable("ZBS_FAKE_API_KEY", _originalFakeSecret);
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, true);
            }
        }

        [Test]
        public void ElectronCliEnvironmentIsConfinedToPrivateHost()
        {
            var original = Environment.GetEnvironmentVariable("VSCODE_DEV");
            try
            {
                Environment.SetEnvironmentVariable("VSCODE_DEV", "parent-fixture");
                var result = OwnedProcessRunner.Run(new OwnedProcessRequest
                {
                    Executable = Environment.GetEnvironmentVariable("CODEX_BIN"),
                    Arguments = new[] { "electron-environment" }, ElectronNode = true,
                    WorkingDirectory = _root
                });
                Assert.That(result.ExitCode, Is.Zero);
                Assert.That(result.Output.Trim(), Is.EqualTo("1||"));
                Assert.That(Environment.GetEnvironmentVariable("VSCODE_DEV"), Is.EqualTo("parent-fixture"));
            }
            finally { Environment.SetEnvironmentVariable("VSCODE_DEV", original); }
        }

        [Test]
        public void StatusUsesLoginVersionAndModelCatalogCommands()
        {
            var status = CodexCliClient.CheckStatus();
            var withoutModels = CodexCliClient.CheckStatus(false);
            var runtimeStatus = (IRuntimeContextInstance)new CodexCliClient().GetStatus();

            Assert.Multiple(() =>
            {
                Assert.That(status.Installed, Is.True);
                Assert.That(status.Authenticated, Is.True);
                Assert.That(status.UsesChatGptSubscription, Is.True);
                Assert.That(status.Version, Is.EqualTo("codex-cli test-1.0"));
                Assert.That(status.Models, Has.Count.EqualTo(1));
                Assert.That(status.Models[0].Slug, Is.EqualTo("gpt-test"));
                Assert.That(withoutModels.Models, Is.Empty);
                Assert.That(ReadBool(runtimeStatus, "Установлен"), Is.True);
                Assert.That(ReadString(runtimeStatus, "Версия"), Is.EqualTo("codex-cli test-1.0"));
            });
        }

        [TestCase("api-auth", false)]
        [TestCase("unauthenticated", false)]
        [TestCase("catalog-empty", true)]
        [TestCase("catalog-fallback", true)]
        public void StatusHandlesAuthenticationAndCatalogFallbacks(string mode, bool authenticated)
        {
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", mode);
            var status = CodexCliClient.CheckStatus();

            Assert.That(status.Authenticated, Is.EqualTo(authenticated || mode == "api-auth"));
            Assert.That(status.UsesChatGptSubscription, Is.EqualTo(authenticated));
            Assert.That(status.Models.Count, Is.EqualTo(mode == "catalog-empty" || mode == "unauthenticated" ? 0 : 1));
        }

        [Test]
        public void TextExecutionPassesModelImageAndReasoningWithoutSecrets()
        {
            Environment.SetEnvironmentVariable("ZBS_FAKE_API_KEY", "must-not-reach-child");
            var result = CodexCliClient.Execute(
                "Improve", "USER DATA", "gpt-test", 10, _imagePath, "high");
            var runtimeResult = (IRuntimeContextInstance)new CodexCliClient().Run(
                "System", "Runtime input", "gpt-test", 10, string.Empty, "low");

            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.True);
                Assert.That(result.Content, Does.Contain("SECRET_FILTERED"));
                Assert.That(result.Content, Does.Not.Contain("must-not-reach-child"));
                Assert.That(result.Content, Does.Contain("--image|" + _imagePath));
                Assert.That(result.Content, Does.Contain("model_reasoning_effort=\"high\""));
                Assert.That(result.Content, Does.Contain("REQUEST AND INPUT:\nUSER DATA"));
                Assert.That(ReadBool(runtimeResult, "Успех"), Is.True);
                Assert.That(ReadString(runtimeResult, "Контент"), Does.Contain("Runtime input"));
            });
        }

        [Test]
        public void CliLaunchDoesNotRewriteTheParentEnvironment()
        {
            var before = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value, StringComparer.Ordinal);
            var result = CodexCliClient.Execute("system", "input", string.Empty, 10);
            Assert.That(result.Success, Is.True, result.Diagnostic);
            var after = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string)entry.Value, StringComparer.Ordinal);
            Assert.That(before.Count == after.Count && before.All(entry =>
                after.TryGetValue(entry.Key, out var value) && value == entry.Value), Is.True,
                "Launching the CLI changed the parent environment.");
        }

        [TestCase("fail", "simulated Codex failure")]
        [TestCase("empty", "without a final response")]
        public void TextExecutionReturnsActionableFailures(string mode, string expected)
        {
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", mode);
            var result = CodexCliClient.Execute("system", "input", string.Empty, 10);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Diagnostic, Does.Contain(expected));
        }

        [Test]
        public void TextExecutionStopsAStalledCli()
        {
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", "sleep");
            var result = CodexCliClient.Execute("system", "input", string.Empty, 1);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Diagnostic, Does.Contain("timed out after 1 seconds"));
        }

        [Test]
        public void TimeoutIncludesWritingALargePromptToAnUnresponsiveCli()
        {
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", "stall-input");
            var elapsed = Stopwatch.StartNew();
            var result = CodexCliClient.Execute("system", new string('x', 1024 * 1024), string.Empty, 1);

            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.False);
                Assert.That(result.Diagnostic, Does.Contain("timed out after 1 seconds"));
                Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)));
            });
        }

        [Test]
        public void EarlyExitPreservesTheCliDiagnosticInsteadOfABrokenPipeError()
        {
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", "fail-before-input");
            var result = CodexCliClient.Execute("system", new string('x', 1024 * 1024), string.Empty, 5);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Diagnostic, Does.Contain("rejected before reading input"));
        }

        [Test]
        public void FailureDiagnosticsDoNotExposeCredentials()
        {
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", "fail-secret");
            var result = CodexCliClient.Execute("system", "input", string.Empty, 5);

            Assert.That(result.Success, Is.False);
            Assert.That(result.Diagnostic, Does.Contain("[REDACTED]"));
            Assert.That(result.Diagnostic, Does.Not.Contain("synthetic-credential"));
        }

        [Test]
        public void TimeoutIncludesPipesHeldOpenByADescendantAfterTheCliExits()
        {
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", "hold-output");
            try
            {
                var elapsed = Stopwatch.StartNew();
                var result = CodexCliClient.Execute("system", "input", string.Empty, 1);
                Assert.That(result.Success, Is.False);
                Assert.That(result.Diagnostic, Does.Contain("timed out after 1 seconds"));
                Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)));
            }
            finally
            {
                var pidPath = Path.Combine(_root, "child.pid");
                if (File.Exists(pidPath))
                {
                    StopFakeChild(int.Parse(File.ReadAllText(pidPath), System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        private static void StopFakeChild(int pid)
        {
            try
            {
                using (var child = Process.GetProcessById(pid))
                {
                    if (!child.HasExited && child.ProcessName == "FakeCodex")
                    {
                        child.Kill();
                        Assert.That(child.WaitForExit(2000), Is.True);
                    }
                }
            }
            catch (ArgumentException)
            {
                // The fixture process may have finished before cleanup.
            }
        }

        [Test]
        public void ImageExecutionCopiesOnlyTheGeneratedPng()
        {
            var outputPath = Path.Combine(_root, "output", "realistic.png");
            var result = CodexCliClient.ExecuteImageEdit(
                "Photorealistic", "Preserve composition", _imagePath, outputPath,
                "gpt-test", 10, "xhigh");
            var runtimeOutput = Path.Combine(_root, "output", "runtime.png");
            var runtimeResult = (IRuntimeContextInstance)new CodexCliClient().GenerateImage(
                "Photorealistic", "Preserve composition", _imagePath, runtimeOutput,
                "gpt-test", 30, "medium");

            Assert.Multiple(() =>
            {
                Assert.That(result.Success, Is.True);
                Assert.That(result.OutputPath, Is.EqualTo(Path.GetFullPath(outputPath)));
                Assert.That(File.Exists(outputPath), Is.True);
                using (var image = Image.FromFile(outputPath))
                {
                    Assert.That(image.Size, Is.EqualTo(new Size(10, 6)));
                }
                Assert.That(ReadBool(runtimeResult, "Успех"), Is.True);
                Assert.That(ReadString(runtimeResult, "Путь"), Is.EqualTo(Path.GetFullPath(runtimeOutput)));
                Assert.That(File.ReadAllText(Path.Combine(_root, "last-invocation.txt")),
                    Does.Contain("$imagegen"));
            });
        }

        [Test]
        public void CancelTerminatesOwnedDescendantsButLeavesAnotherSessionAlone()
        {
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", "spawn-stall");
            using (var unrelated = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(TestContext.CurrentContext.TestDirectory, "FakeCodex.exe"),
                Arguments = "hold-output", UseShellExecute = false, CreateNoWindow = true
            }))
            {
                Operations.Begin();
                var pidPath = Path.Combine(_root, "child.pid");
                var cancel = Task.Run(() =>
                {
                    var ready = SpinWait.SpinUntil(() => File.Exists(pidPath), 3000);
                    Operations.Cancel();
                    return ready;
                });
                try
                {
                    var elapsed = Stopwatch.StartNew();
                    var result = CodexCliClient.Execute("system", "input", string.Empty, 30);
                    Assert.That(cancel.Result, Is.True, "The child must exist before testing cancellation.");
                    Assert.That(result.Success, Is.False);
                    Assert.That(result.Content, Is.Null.Or.Empty);
                    Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(4)));
                    Assert.That(unrelated.HasExited, Is.False, "Another CLI session must survive.");
                    var pid = int.Parse(File.ReadAllText(pidPath), System.Globalization.CultureInfo.InvariantCulture);
                    try
                    {
                        using (var child = Process.GetProcessById(pid))
                            Assert.That(child.WaitForExit(1500), Is.True, "Owned descendant remained alive.");
                    }
                    catch (ArgumentException) { /* The owned process has already been reaped. */ }
                }
                finally
                {
                    cancel.GetAwaiter().GetResult();
                    Operations.End();
                    StopFakeChild(unrelated.Id);
                    if (File.Exists(pidPath)) StopFakeChild(int.Parse(File.ReadAllText(pidPath), System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        [Test]
        public void ImageExecutionRejectsInvalidInputsAndCliFailures()
        {
            var missing = CodexCliClient.ExecuteImageEdit(
                "system", "input", Path.Combine(_root, "missing.png"),
                Path.Combine(_root, "output.png"), string.Empty, 10, string.Empty);
            var wrongOutput = CodexCliClient.ExecuteImageEdit(
                "system", "input", _imagePath, Path.Combine(_root, "output.jpg"),
                string.Empty, 10, string.Empty);
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", "fail");
            var failed = CodexCliClient.ExecuteImageEdit(
                "system", "input", _imagePath, Path.Combine(_root, "output.png"),
                string.Empty, 10, string.Empty);
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", "empty");
            var empty = CodexCliClient.ExecuteImageEdit(
                "system", "input", _imagePath, Path.Combine(_root, "empty.png"),
                string.Empty, 10, string.Empty);
            Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", "sleep");
            var timedOut = CodexCliClient.ExecuteImageEdit(
                "system", "input", _imagePath, Path.Combine(_root, "timeout.png"),
                string.Empty, 1, string.Empty);

            Assert.Multiple(() =>
            {
                Assert.That(missing.Diagnostic, Does.Contain("reference image was not found"));
                Assert.That(wrongOutput.Diagnostic, Does.Contain("must end with .png"));
                Assert.That(failed.Diagnostic, Does.Contain("simulated Codex failure"));
                Assert.That(empty.Diagnostic, Does.Contain("without a fresh generated PNG"));
                Assert.That(timedOut.Diagnostic, Does.Contain("timed out after 1 seconds"));
            });
        }

        [Test]
        public void BrokenConfiguredExecutableReturnsStructuredDiagnostics()
        {
            var brokenExecutable = Path.Combine(_root, "broken-codex.exe");
            File.WriteAllText(brokenExecutable, "not a Windows executable");
            Environment.SetEnvironmentVariable("CODEX_BIN", brokenExecutable);

            var status = CodexCliClient.CheckStatus();
            var textResult = CodexCliClient.Execute("system", "input", string.Empty, 10);
            var imageResult = CodexCliClient.ExecuteImageEdit(
                "system", "input", _imagePath, Path.Combine(_root, "output.png"),
                string.Empty, 10, string.Empty);

            Assert.Multiple(() =>
            {
                Assert.That(status.Installed, Is.True);
                Assert.That(status.Authenticated, Is.False);
                Assert.That(status.Diagnostic, Is.Not.Empty);
                Assert.That(textResult.Success, Is.False);
                Assert.That(textResult.Diagnostic, Is.Not.Empty);
                Assert.That(imageResult.Success, Is.False);
                Assert.That(imageResult.Diagnostic, Is.Not.Empty);
            });
        }

        [Test]
        public void MissingConfiguredExecutableDoesNotFallBackToAnotherInstallation()
        {
            Environment.SetEnvironmentVariable(
                "CODEX_BIN", Path.Combine(_root, "missing-codex.exe"));

            var status = CodexCliClient.CheckStatus();
            var textResult = CodexCliClient.Execute("system", "input", string.Empty, 10);
            var imageResult = CodexCliClient.ExecuteImageEdit(
                "system", "input", _imagePath, Path.Combine(_root, "output.png"),
                string.Empty, 10, string.Empty);

            Assert.Multiple(() =>
            {
                Assert.That(CodexCliClient.FindExecutable(), Is.Empty);
                Assert.That(status.Installed, Is.False);
                Assert.That(status.Diagnostic, Does.Contain("not found"));
                Assert.That(textResult.Success, Is.False);
                Assert.That(textResult.Diagnostic, Does.Contain("not installed"));
                Assert.That(imageResult.Success, Is.False);
                Assert.That(imageResult.Diagnostic, Does.Contain("not installed"));
            });
        }

        private static string ReadString(IRuntimeContextInstance source, string name)
        {
            return source.GetPropValue(source.FindProperty(name)).AsString();
        }

        private static bool ReadBool(IRuntimeContextInstance source, string name)
        {
            return source.GetPropValue(source.FindProperty(name)).AsBoolean();
        }
    }
}
