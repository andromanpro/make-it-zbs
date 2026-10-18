using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture, NonParallelizable]
    public class VsCodeAdapterSetupTests
    {
        private string _root;
        private string _command;
        [SetUp] public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "zbs-editor-" + Guid.NewGuid().ToString("N"));
            _command = Path.Combine(_root, "bin", "code.cmd");
            Directory.CreateDirectory(Path.GetDirectoryName(_command));
            File.WriteAllText(Path.Combine(_root, "Code.exe"), "fixture");
            var cli = Path.Combine(_root, "1234567890", "resources", "app", "out", "cli.js");
            Directory.CreateDirectory(Path.GetDirectoryName(cli));
            File.WriteAllText(cli, "fixture");
            File.WriteAllText(_command, "@echo off\r\n\"%~dp0..\\Code.exe\" \"%~dp0..\\1234567890\\resources\\app\\out\\cli.js\" %*\r\n");
        }
        [TearDown] public void TearDown() => Directory.Delete(_root, true);

        [Test]
        public void ResolvesVersionedOfficialCliWithoutExecutingBatchText()
        {
            Assert.That(VsCodeAdapterSetup.TryResolveCommand(_command, out var executable, out var script), Is.True);
            Assert.That(executable, Is.EqualTo(Path.Combine(_root, "Code.exe")));
            Assert.That(script, Does.Contain("1234567890"));
            Assert.That(VsCodeAdapterSetup.TryResolveCommand(null, out _, out _), Is.False);
            File.WriteAllText(_command, "\"%~dp0..\\Code.exe\" \"%~dp0..\\..\\outside\\resources\\app\\out\\cli.js\" %*");
            Assert.That(VsCodeAdapterSetup.TryResolveCommand(_command, out _, out _), Is.False);
            File.WriteAllText(_command, "unrecognized batch content");
            Assert.That(VsCodeAdapterSetup.TryResolveCommand(_command, out _, out _), Is.False);
            File.WriteAllText(_command, new string('x', 16385));
            Assert.That(VsCodeAdapterSetup.TryResolveCommand(_command, out _, out _), Is.False);
        }

        [Test]
        public void EmbeddedPackageHasMatchingIdentityAndVersion()
        {
            using (var archive = new ZipArchive(new MemoryStream(VsCodeAdapterSetup.BundledPackage())))
            using (var reader = new StreamReader(archive.GetEntry("extension/package.json").Open()))
            {
                var package = JObject.Parse(reader.ReadToEnd());
                Assert.That(package.Value<string>("name"), Is.EqualTo("make-it-zbs-editor"));
                Assert.That(package.Value<string>("publisher"), Is.EqualTo("androman"));
                Assert.That(VsCodeAdapterSetup.BundledVersion(), Is.EqualTo(package.Value<string>("version")));
            }
        }

        [TestCase("", false, false)]
        [TestCase("other.extension@9.0.0", false, false)]
        [TestCase("androman.make-it-zbs-editor@0.0.9", true, false)]
        [TestCase("androman.make-it-zbs-editor@0.1.0", true, true)]
        [TestCase("androman.make-it-zbs-editor@9.0.0", true, true)]
        public void DetectsOnlyThisAdapterAndNeverDowngradesNewerVersions(string output, bool installed, bool current)
        {
            var setup = new VsCodeAdapterSetup(_command, request =>
            {
                Assert.That(request.ElectronNode, Is.True);
                Assert.That(request.Arguments, Does.Contain("--list-extensions"));
                return new ProcessResult { Output = output + "\r\n", ExitCode = 0 };
            });
            var status = setup.Check();
            Assert.That(status.EditorFound, Is.True);
            Assert.That(status.Installed, Is.EqualTo(installed));
            Assert.That(status.Current, Is.EqualTo(current));
            if (current) Assert.That(setup.Install().Current, Is.True);
        }

        [Test]
        public void MissingEditorDoesNotStartAnyProcess()
        {
            var setup = new VsCodeAdapterSetup("missing", _ => throw new AssertionException("Unexpected process"));
            Assert.That(setup.Check().EditorFound, Is.False);
            Assert.That(setup.Install().EditorFound, Is.False);
            var discovered = VsCodeAdapterSetup.FindCommand();
            Assert.That(discovered == null || File.Exists(discovered), Is.True);
        }

        [Test]
        public void InstallsEmbeddedVsixAndVerifiesBeforeReportingSuccessThenDeletesOnlyItsTemporaryFile()
        {
            var calls = 0;
            string temporary = null;
            var sentinel = Path.Combine(_root, "unrelated.vsix");
            File.WriteAllText(sentinel, "unrelated");
            var setup = new VsCodeAdapterSetup(_command, request =>
            {
                calls++;
                var args = request.Arguments.ToArray();
                if (calls == 2)
                {
                    var index = Array.IndexOf(args, "--install-extension");
                    temporary = args[index + 1];
                    Assert.That(File.ReadAllBytes(temporary), Is.EqualTo(VsCodeAdapterSetup.BundledPackage()));
                    Assert.That(args, Does.Contain("--force"));
                    Assert.That(args, Does.Contain("fixture-profile"));
                    return new ProcessResult { ExitCode = 0 };
                }
                return new ProcessResult { ExitCode = 0, Output = calls == 3 ? "androman.make-it-zbs-editor@" + VsCodeAdapterSetup.BundledVersion() : "" };
            }, "--profile", "fixture-profile");
            var result = setup.Install();
            Assert.That(result.Current, Is.True);
            Assert.That(result.ReloadRequired, Is.True);
            Assert.That(result.Error, Is.Empty);
            Assert.That(calls, Is.EqualTo(3));
            Assert.That(File.Exists(temporary), Is.False);
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("unrelated"));
        }

        [TestCase(true, "install-failed")]
        [TestCase(false, "verification-failed")]
        public void NeverReportsSuccessForFailedOrUnconfirmedInstallation(bool failure, string error)
        {
            var calls = 0;
            string temporary = null;
            var setup = new VsCodeAdapterSetup(_command, request =>
            {
                calls++;
                if (calls == 2)
                {
                    var args = request.Arguments.ToArray();
                    temporary = args[Array.IndexOf(args, "--install-extension") + 1];
                    return new ProcessResult { ExitCode = failure ? 1 : 0, Error = "password=do-not-display" };
                }
                return new ProcessResult { ExitCode = 0, Output = "" };
            });
            var result = setup.Install();
            Assert.That(result.Error, Is.EqualTo(error));
            Assert.That(result.Current, Is.False);
            Assert.That(result.ReloadRequired, Is.False);
            Assert.That(File.Exists(temporary), Is.False);
        }

        [TestCase(true)] [TestCase(false)]
        public void CheckFailuresAreVisibleWithoutRawCliOutput(bool timedOut)
        {
            var setup = new VsCodeAdapterSetup(_command, _ => new ProcessResult
            {
                TimedOut = timedOut, ExitCode = 1, Error = "password=do-not-display"
            });
            Assert.That(setup.Install().Error, Is.EqualTo("check-failed"));
            var throwing = new VsCodeAdapterSetup(_command, _ => throw new IOException("private path"));
            Assert.That(throwing.Check().Error, Is.EqualTo("check-failed"));
        }

        [Test]
        public void ConcurrentInstallIsRefusedAndDoesNotReplayLater()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var setup = new VsCodeAdapterSetup(_command, _ =>
                {
                    entered.Set(); release.Wait(3000);
                    return new ProcessResult { ExitCode = 1 };
                });
                var first = Task.Run(setup.Install);
                try
                {
                    Assert.That(entered.Wait(1000), Is.True);
                    Assert.That(setup.Install().Error, Is.EqualTo("busy"));
                }
                finally { release.Set(); first.GetAwaiter().GetResult(); }
            }
        }

        [Test]
        public void SettingsPayloadHasNoSecretsAndKeepsInstalledSeparateFromCurrent()
        {
            Assert.That(SettingsWebForm.BuildEditorAdapterPayload(null).Value<bool>("checked"), Is.False);
            var payload = SettingsWebForm.BuildEditorAdapterPayload(new VsCodeAdapterStatus
            {
                EditorFound = true, Installed = true, Current = false, Version = "0.0.9"
            });
            Assert.That(payload.Value<bool>("installed"), Is.True);
            Assert.That(payload.Value<bool>("current"), Is.False);
        }
    }
}
