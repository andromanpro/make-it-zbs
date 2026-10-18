using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class VsCodeEditorBridgeTests
    {
        private string _directory;
        [SetUp] public void SetUp() { _directory = Path.Combine(Path.GetTempPath(), "zbs-bridge-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(_directory); }
        [TearDown] public void TearDown() => Directory.Delete(_directory, true);

        private JObject Descriptor(string name) => new JObject
        {
            ["protocol"] = 1, ["pipe"] = name, ["token"] = new string('a', 64), ["pid"] = Process.GetCurrentProcess().Id
        };

        private static JObject CapturedReply() => new JObject
        {
            ["ok"] = true, ["captureId"] = Guid.NewGuid().ToString(), ["uri"] = "file:///test.bsl",
            ["version"] = 2, ["start"] = 4, ["end"] = 7, ["mode"] = "selection",
            ["document"] = ReplacementSnapshot.Fingerprint("one two three"), ["selection"] = ReplacementSnapshot.Fingerprint("two")
        };

        private void WithServer(Func<JObject, string> reply, Action<JObject> run)
        {
            var name = "make-it-zbs-vscode-" + Guid.NewGuid().ToString("N");
            var endpoint = Descriptor(name);
            File.WriteAllText(Path.Combine(_directory, "vscode-test.json"), endpoint.ToString());
            using (var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
            {
                var pending = Task.Run(async () =>
                {
                    await server.WaitForConnectionAsync();
                    using (var reader = new StreamReader(server, Encoding.UTF8, false, 4096, true))
                    {
                        var line = await reader.ReadLineAsync();
                        if (line == null) return;
                        var request = JObject.Parse(line);
                        Assert.That(request.Value<string>("token"), Is.EqualTo(new string('a', 64)));
                        var response = reply(request);
                        if (response == null) { await Task.Delay(300); return; }
                        if (response.StartsWith("{")) response = JObject.Parse(response).ToString(Newtonsoft.Json.Formatting.None);
                        var bytes = Encoding.UTF8.GetBytes(response + "\n");
                        await server.WriteAsync(bytes, 0, bytes.Length);
                        await server.FlushAsync();
                    }
                });
                try { run(endpoint); }
                finally
                {
                    server.Dispose();
                    try { Assert.That(pending.Wait(3000), Is.True); }
                    catch (AggregateException ex) when (ex.InnerException is ObjectDisposedException || ex.InnerException is IOException) { }
                }
            }
        }

        [Test]
        public void CapturesHashesAndDocumentIdentityThroughAuthenticatedPipe()
        {
            WithServer(request => { Assert.That(request.Value<string>("cmd"), Is.EqualTo("capture")); return CapturedReply().ToString(); }, endpoint =>
            {
                var captured = VsCodeEditorBridge.Capture(123, endpoint.Value<int>("pid"), "selection", CancellationToken.None, out var available, _directory);
                Assert.That(available, Is.True);
                Assert.That(captured.Editor.Uri, Is.EqualTo("file:///test.bsl"));
                Assert.That(captured.Editor.Version, Is.EqualTo(2));
                var lease = new ReplacementLease();
                Assert.That(lease.Capture(captured, "wrong clipboard"), Is.Empty);
                Assert.That(lease.Capture(captured, "two"), Is.Not.Empty);
            });
        }

        [TestCase(true)] [TestCase(false)]
        public void AppliesOnlyWhenEditorConfirmsSuccess(bool success)
        {
            WithServer(request =>
            {
                Assert.That(request.Value<string>("cmd"), Is.EqualTo("apply"));
                Assert.That(request.Value<string>("text"), Is.EqualTo("новый текст"));
                Assert.That(request.Value<string>("captureId"), Is.EqualTo("lease"));
                return new JObject { ["ok"] = success }.ToString();
            }, endpoint => Assert.That(VsCodeEditorBridge.Apply(new ReplacementSnapshot
            {
                ProcessId = endpoint.Value<int>("pid"), Editor = new VsCodeCapture { Endpoint = endpoint, CaptureId = "lease" }
            }, "новый текст", CancellationToken.None), Is.EqualTo(success)));
        }

        [TestCase("{\"ok\":false}")] [TestCase("invalid-json")] [TestCase("{}")]
        public void RejectedOrMalformedRepliesNeverAuthorizeReplacement(string reply)
        {
            WithServer(_ => reply, endpoint =>
            {
                Assert.That(VsCodeEditorBridge.Capture(123, endpoint.Value<int>("pid"), "selection",
                    CancellationToken.None, out var available, _directory), Is.Null);
                Assert.That(available, Is.True, "A verified but broken adapter must not fall back to blind UIA.");
            });
        }

        [Test]
        public void CancellationInterruptsPipeReadAndDoesNotWaitForDeadline()
        {
            using (var cancel = new CancellationTokenSource())
                WithServer(_ => { cancel.Cancel(); return null; }, endpoint =>
                {
                    var elapsed = Stopwatch.StartNew();
                    Assert.Throws<OperationCanceledException>(() => VsCodeEditorBridge.TryExchange(endpoint, endpoint.Value<int>("pid"), new JObject(), cancel.Token, out _));
                    Assert.That(elapsed.ElapsedMilliseconds, Is.LessThan(1000));
                });
        }

        [Test]
        public void RejectsWrongServerPidWithoutSendingSecretOrDocument()
        {
            WithServer(_ => throw new AssertionException("Must not send a request"), endpoint =>
            {
                endpoint["pid"] = 1;
                Assert.That(VsCodeEditorBridge.TryExchange(endpoint, Process.GetCurrentProcess().Id, new JObject(), CancellationToken.None, out _), Is.False);
            });
        }

        [TestCase("{}")] [TestCase("bad-json")]
        [TestCase("{\"protocol\":1,\"pid\":1,\"pipe\":\"../unsafe\",\"token\":\"bad\"}")]
        public void InvalidDescriptorsAreNotConnected(string text)
        {
            var file = Path.Combine(_directory, "vscode-test.json");
            File.WriteAllText(file, text);
            Assert.That(VsCodeEditorBridge.TryReadEndpoint(file, out _), Is.False);
            Assert.That(VsCodeEditorBridge.Capture(1, 1, "selection", CancellationToken.None, out var available, _directory), Is.Null);
            Assert.That(available, Is.False);
        }

        [Test]
        public void OldCrashDescriptorsDoNotHideTheNewestRunningEditor()
        {
            for (var index = 0; index < 40; index++)
            {
                var stale = Path.Combine(_directory, "vscode-stale-" + index + ".json");
                File.WriteAllText(stale, "{}");
                File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-1));
            }
            WithServer(_ => CapturedReply().ToString(), endpoint => Assert.That(VsCodeEditorBridge.Capture(123,
                endpoint.Value<int>("pid"), "selection", CancellationToken.None, out _, _directory), Is.Not.Null));
        }

        [Test]
        public void AuthenticatedMalformedApplyReplyNeverMeansSuccess()
        {
            WithServer(_ => "{\"ok\":\"invalid\"}", endpoint => Assert.That(VsCodeEditorBridge.Apply(new ReplacementSnapshot
            {
                ProcessId = endpoint.Value<int>("pid"), Editor = new VsCodeCapture { Endpoint = endpoint, CaptureId = "lease" }
            }, "new", CancellationToken.None), Is.False));
        }

        [Test]
        public void MissingOrOversizeDescriptorIsIgnored()
        {
            Assert.That(VsCodeEditorBridge.TryReadEndpoint(Path.Combine(_directory, "missing"), out _), Is.False);
            var file = Path.Combine(_directory, "vscode-big.json");
            File.WriteAllText(file, new string('x', 4097));
            Assert.That(VsCodeEditorBridge.TryReadEndpoint(file, out _), Is.False);
            Assert.That(VsCodeEditorBridge.Capture(1, 1, "selection", CancellationToken.None, out _, Path.Combine(_directory, "missing")), Is.Null);
        }
    }
}
