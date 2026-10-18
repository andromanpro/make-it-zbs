using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using MakeItZBS.Capture;
using OneScript.Automation;
using ZBSWindowsComponent;

namespace OneScript.Automation.Tests
{
    [TestFixture, NonParallelizable]
    public sealed class AutomationTests
    {
        private string _root;
        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "automation-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }
        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        private static void Complete(AutomationJob job)
        {
            Assert.That(SpinWait.SpinUntil(() => job.IsCompleted, 10000), Is.True, "Owned job must finish.");
        }

        [TestCase("relative.png"), TestCase(""), TestCase("C:\\capture.jpg")]
        public void CaptureRejectsInvalidPathsBeforeDesktopAccess(string path)
        {
            Assert.Throws<ArgumentException>(() => new AutomationClients().CaptureWindow(path));
        }

        [TestCase("AUTO"), TestCase("unknown"), TestCase("")]
        public void CaptureRejectsUnknownBackends(string backend)
        {
            Assert.Throws<ArgumentException>(() => new AutomationClients().CaptureWindow(Path.Combine(_root, "capture.png"), backend));
        }

        [TestCase("codex.exe", "", "codex")]
        [TestCase("claude.exe", "", "claude")]
        [TestCase("opencode.exe", "", "opencode")]
        [TestCase("notepad.exe", "Codex", "")]
        public void ProfilesDoNotMatchAnUnrelatedApplicationByTitle(string executable, string title, string expected)
        {
            Assert.That(new AutomationClients().ResolveChatProfile(executable, title), Is.EqualTo(expected));
        }

        [TestCase("http://openrouter.ai/api/v1/chat/completions")]
        [TestCase("https://example.invalid/")]
        [TestCase("https://openrouter.ai.example.invalid/")]
        public void HttpRejectsForeignHostsBeforeCreatingAJob(string url)
        {
            Assert.Throws<ScriptEngine.Machine.RuntimeException>(() => new AutomationClients().StartOpenRouter(url, "{}", ""));
        }

        [Test]
        public void JobRejectsAnotherAdmissionAndReturnsOnlyItsCompletedResult()
        {
            using (var entered = new ManualResetEventSlim())
            using (var proceed = new ManualResetEventSlim())
            using (var job = new AutomationJob(token => { entered.Set(); proceed.Wait(token); return "result"; }))
            {
                Assert.That(entered.Wait(5000), Is.True);
                Assert.Throws<InvalidOperationException>(() => job.ResultJson());
                Assert.Throws<InvalidOperationException>(() => new AutomationJob(token => "unexpected"));
                proceed.Set();
                Complete(job);
                Assert.That(job.ResultJson(), Is.EqualTo("result"));
            }
        }

        [Test]
        public void CancellationAndLateCancellationBothPreventResultUse()
        {
            using (var entered = new ManualResetEventSlim())
            using (var job = new AutomationJob(token => { entered.Set(); token.WaitHandle.WaitOne(); return "late"; }))
            {
                Assert.That(entered.Wait(5000), Is.True);
                job.Cancel();
                Complete(job);
                Assert.That(job.IsCancelled, Is.True);
                Assert.Throws<OperationCanceledException>(() => job.ResultJson());
            }
            using (var completed = new AutomationJob(token => "already completed"))
            {
                Complete(completed);
                completed.Cancel();
                Assert.Throws<OperationCanceledException>(() => completed.ResultJson());
            }
        }

        [Test]
        public void FailureAndIdempotentCloseReleaseTheAdmission()
        {
            var failed = new AutomationJob(token => throw new InvalidDataException("fixture failure"));
            Complete(failed);
            Assert.Throws<InvalidDataException>(() => failed.ResultJson());
            failed.Dispose();
            failed.Dispose();
            Assert.Throws<ObjectDisposedException>(() => failed.ResultJson());
            using (var next = new AutomationJob(token => "next"))
            {
                failed.Cancel();
                Complete(next);
                Assert.That(next.ResultJson(), Is.EqualTo("next"));
            }
        }

        [TestCase(0), TestCase(601)]
        public void ClientTimeoutsAreValidatedBeforeAdmission(int timeout)
        {
            var clients = new AutomationClients();
            Assert.Throws<ArgumentOutOfRangeException>(() => clients.StartCodex("", "", timeoutSeconds: timeout));
            Assert.Throws<ArgumentOutOfRangeException>(() => clients.StartOpenRouter("https://openrouter.ai/api/v1/chat/completions", "{}", "", timeout));
        }

        [TestCase("success"), TestCase("cancel"), TestCase("wrong-metadata"), TestCase("wrong-backend"), TestCase("timeout"), TestCase("error")]
        public void CaptureWorkerValidatesMetadataAndDoesNotPublishCancelledOrInvalidFiles(string mode)
        {
            var output = Path.Combine(_root, "capture.png");
            File.WriteAllText(output, "original");
            string temporary = null;
            var request = new CaptureRequest { Bounds = new Rectangle(10, 20, 8, 5), Backend = CaptureBackend.Gdi };
            using (var cancellation = new CancellationTokenSource())
            {
                Func<OwnedProcessRequest, CancellationToken, ProcessResult> runner = (process, token) =>
                {
                    var input = JsonConvert.DeserializeObject<CaptureRequest>(process.Input);
                    temporary = input.OutputPath;
                    using (var bitmap = new Bitmap(8, 5)) bitmap.Save(temporary, ImageFormat.Png);
                    if (mode == "cancel") cancellation.Cancel();
                    return new ProcessResult
                    {
                        TimedOut = mode == "timeout", ExitCode = mode == "error" ? 1 : 0,
                        Output = JsonConvert.SerializeObject(new CaptureMetadata
                        {
                            Bounds = mode == "wrong-metadata" ? Rectangle.Empty : input.Bounds,
                            Backend = mode == "wrong-backend" ? "dxgi" : "gdi", CapturedAtUtc = DateTime.UtcNow
                        })
                    };
                };
                if (mode == "success")
                {
                    var metadata = DesktopCaptureClient.Capture(request, output, cancellation.Token, runner: runner);
                    Assert.That(metadata.Backend, Is.EqualTo("gdi"));
                    using (var image = Image.FromFile(output)) Assert.That(image.Size, Is.EqualTo(new Size(8, 5)));
                }
                else
                {
                    Assert.That(() => DesktopCaptureClient.Capture(request, output, cancellation.Token, runner: runner), Throws.Exception);
                    Assert.That(File.ReadAllText(output), Is.EqualTo("original"));
                }
            }
            Assert.That(File.Exists(temporary), Is.False);
        }

        [TestCase(""), TestCase("Ctrl"), TestCase("Ctrl+Unknown")]
        public void HotkeysRejectInvalidSyntaxBeforeRegistration(string key)
        {
            Assert.Throws<ArgumentException>(() => ZBSWindowsComponent.Helpers.KeyConverter.Parse(key));
        }

        [TestCase("success"), TestCase("sleep"), TestCase("fail")]
        public void CodexUsesAnOwnedFixtureProcessAndCancellationDoesNotReturnLateContent(string mode)
        {
            var previousBin = Environment.GetEnvironmentVariable("CODEX_BIN");
            var previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            var previousMode = Environment.GetEnvironmentVariable("ZBS_FAKE_MODE");
            try
            {
                Environment.SetEnvironmentVariable("CODEX_BIN", Path.Combine(TestContext.CurrentContext.TestDirectory, "FakeCodex.exe"));
                Environment.SetEnvironmentVariable("CODEX_HOME", _root);
                Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", mode == "success" ? null : mode);
                using (var job = new AutomationClients().StartCodex("Improve", "synthetic text", "gpt-test", "high"))
                {
                    if (mode == "sleep")
                    {
                        Thread.Sleep(200);
                        job.Cancel();
                    }
                    Complete(job);
                    if (mode == "sleep") Assert.Throws<OperationCanceledException>(() => job.ResultJson());
                    else Assert.That(JObject.Parse(job.ResultJson()).Value<bool>("success"), Is.EqualTo(mode == "success"));
                }
            }
            finally
            {
                Environment.SetEnvironmentVariable("CODEX_BIN", previousBin);
                Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
                Environment.SetEnvironmentVariable("ZBS_FAKE_MODE", previousMode);
            }
        }

        [TestCase(200), TestCase(429), TestCase(500)]
        public void HttpPreservesStatusAndBodyWithoutHiddenRetries(int status)
        {
            var calls = 0;
            var clients = new AutomationClients((request, token) =>
            {
                calls++;
                Assert.That(request.Method, Is.EqualTo("POST"));
                Assert.That(request.TimeoutSeconds, Is.EqualTo(20));
                Assert.That(request.Authorization, Is.EqualTo("fixture-authorization"));
                return new OpenRouterHttpResult(status, "fixture response");
            });
            using (var job = clients.StartOpenRouter("https://openrouter.ai/api/v1/chat/completions", "{}", "fixture-authorization", 20))
            {
                Complete(job);
                var result = JObject.Parse(job.ResultJson());
                Assert.That(result.Value<int>("statusCode"), Is.EqualTo(status));
                Assert.That(result.Value<string>("body"), Is.EqualTo("fixture response"));
                Assert.That(job.ResultJson(), Does.Not.Contain("fixture-authorization"));
                Assert.That(calls, Is.EqualTo(1));
            }
        }
    }
}
