using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ScriptEngine.HostedScript.Library;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class InteractionTests
    {
        [Test]
        public void StreamPreviewOptionsReadRuntimeStructure()
        {
            var request = new StructureImpl();
            request.Insert("url", ValueFactory.Create("https://example.test/stream"));
            request.Insert("authHeader", ValueFactory.Create("Bearer token"));
            request.Insert("bodyJson", ValueFactory.Create("{}"));

            var options = StreamPreviewOptions.FromRuntimeContext(
                request, null, "Code", null, 15, true);

            Assert.Multiple(() =>
            {
                Assert.That(options.Url, Is.EqualTo("https://example.test/stream"));
                Assert.That(options.AuthHeader, Is.EqualTo("Bearer token"));
                Assert.That(options.BodyJson, Is.EqualTo("{}"));
                Assert.That(options.Original, Is.Empty);
                Assert.That(options.Context, Is.EqualTo("Code"));
                Assert.That(options.Model, Is.Empty);
                Assert.That(options.TimeoutSec, Is.EqualTo(15));
                Assert.That(options.AllowApply, Is.True);
            });
        }

        [Test]
        public void StreamPreviewOptionsRejectMissingRequestOrProperty()
        {
            Assert.That(() => StreamPreviewOptions.FromRuntimeContext(
                null, "", "", "", 0, false), Throws.TypeOf<RuntimeException>());

            var incomplete = new StructureImpl();
            incomplete.Insert("url", ValueFactory.Create("https://example.test"));
            Assert.That(() => StreamPreviewOptions.FromRuntimeContext(
                incomplete, "", "", "", 0, false), Throws.TypeOf<RuntimeException>());
        }

        [Test]
        public void SettingsPayloadUsesDefaultsAndClonesProfiles()
        {
            var defaults = SettingsWebForm.BuildAiChatPayload(new JObject());
            Assert.That(defaults["profiles"]["codex"], Is.Not.Null);

            var settings = JObject.Parse(@"{
                'aiChat': {'profiles': {'codex': {
                    'label': 'Codex', 'enabled': true, 'processes': ['codex.exe'],
                    'hotkey': 'Ctrl+F12', 'mode': 'continue', 'continueText': 'Next',
                    'sendMode': 'submit', 'analysisSource': 'localText'
                }}}
            }");
            var payload = SettingsWebForm.BuildAiChatPayload(settings);
            payload["profiles"]["codex"]["label"] = "Changed";
            Assert.That(settings["aiChat"]["profiles"]["codex"]["label"].Value<string>(),
                Is.EqualTo("Codex"));
        }

        [Test]
        public void SettingsPayloadAppliesOnlyValidProfiles()
        {
            var settings = new JObject();
            SettingsWebForm.ApplyAiChat(settings, null);
            SettingsWebForm.ApplyAiChat(settings, new JObject());
            Assert.That(settings["aiChat"], Is.Null);

            var longName = new string('x', 65);
            var payload = new JObject
            {
                ["profiles"] = new JObject
                {
                    ["invalid"] = "not-an-object",
                    [longName] = new JObject(),
                    ["codex"] = new JObject
                    {
                        ["label"] = "Codex custom",
                        ["enabled"] = false,
                        ["processes"] = "not-an-array",
                        ["hotkey"] = "Alt+F11",
                        ["mode"] = "nextStep",
                        ["continueText"] = "Continue",
                        ["sendMode"] = "draft",
                        ["analysisSource"] = "screenshot"
                    }
                }
            };

            SettingsWebForm.ApplyAiChat(settings, payload);
            var profile = (JObject)settings["aiChat"]["profiles"]["codex"];
            Assert.Multiple(() =>
            {
                Assert.That(profile.Value<string>("label"), Is.EqualTo("Codex custom"));
                Assert.That(profile.Value<bool>("enabled"), Is.False);
                Assert.That(profile["processes"], Is.Empty);
                Assert.That(profile.Value<string>("mode"), Is.EqualTo("nextStep"));
                Assert.That(profile.Value<string>("sendMode"), Is.EqualTo("draft"));
                Assert.That(profile.Value<string>("analysisSource"), Is.EqualTo("screenshot"));
                Assert.That(settings["aiChat"]["profiles"]["invalid"], Is.Null);
                Assert.That(settings["aiChat"]["profiles"][longName], Is.Null);
            });
        }

        [Test]
        public void SettingsPayloadClonesProcessListsAndUsesProfileDefaults()
        {
            var settings = JObject.Parse(@"{
                'aiChat': {'profiles': {'custom': {'label': 'Old'}}}
            }");
            var processes = new JArray("custom.exe");
            var payload = new JObject
            {
                ["profiles"] = new JObject
                {
                    ["custom"] = new JObject { ["processes"] = processes }
                }
            };

            SettingsWebForm.ApplyAiChat(settings, payload);
            processes.Add("mutated.exe");
            var profile = (JObject)settings["aiChat"]["profiles"]["custom"];
            Assert.Multiple(() =>
            {
                Assert.That(profile.Value<string>("label"), Is.EqualTo("custom"));
                Assert.That(profile.Value<bool>("enabled"), Is.True);
                Assert.That(profile["processes"], Is.EqualTo(new JArray("custom.exe")));
                Assert.That(profile.Value<string>("hotkey"), Is.EqualTo("Ctrl+F12"));
                Assert.That(profile.Value<string>("mode"), Is.EqualTo("continue"));
                Assert.That(profile.Value<string>("sendMode"), Is.EqualTo("submit"));
                Assert.That(profile.Value<string>("analysisSource"), Is.EqualTo("localText"));
            });
        }

        [Test]
        public void SettingsPayloadBuildsGeneralModelsAndPrompts()
        {
            var settings = JObject.Parse(@"{
                'general': {'hotkey': 'Alt+F11', 'language': 'EN', 'background': false}
            }");
            var general = SettingsWebForm.BuildGeneralPayload(settings);

            var models = JObject.Parse(@"{
                'contexts': {
                    'invalid': 'skip',
                    'Browser': {
                        'provider': 'CODEX', 'model': 'vision/model',
                        'codexModel': 'gpt-sol', 'codexReasoningEffort': 'HIGH',
                        'temperature': 0.25, 'maxTokens': 4096
                    },
                    'Text': {}
                }
            }");
            var contexts = SettingsWebForm.BuildContextsPayload(models);

            var prompts = SettingsWebForm.BuildPromptsPayload(JObject.Parse(@"{
                'invalid': 'skip',
                'Text': {'system': 'System prompt', 'user': 'User prompt'},
                'Empty': {}
            }"));

            Assert.Multiple(() =>
            {
                Assert.That(general.Value<string>("hotkey"), Is.EqualTo("Alt+F11"));
                Assert.That(general.Value<string>("language"), Is.EqualTo("en"));
                Assert.That(general.Value<bool>("background"), Is.False);
                Assert.That(general["autostart"].Type, Is.EqualTo(Newtonsoft.Json.Linq.JTokenType.Boolean));
                Assert.That(contexts["invalid"], Is.Null);
                Assert.That(contexts["Browser"].Value<string>("provider"), Is.EqualTo("codex"));
                Assert.That(contexts["Browser"].Value<string>("codexModel"), Is.EqualTo("gpt-sol"));
                Assert.That(contexts["Browser"].Value<string>("codexReasoningEffort"), Is.EqualTo("high"));
                Assert.That(contexts["Browser"].Value<decimal>("temperature"), Is.EqualTo(0.25m));
                Assert.That(contexts["Browser"].Value<int>("maxTokens"), Is.EqualTo(4096));
                Assert.That(contexts["Text"].Value<string>("provider"), Is.EqualTo("openrouter"));
                Assert.That(contexts["Text"].Value<int>("maxTokens"), Is.EqualTo(2048));
                Assert.That(prompts["invalid"], Is.Null);
                Assert.That(prompts["Text"].Value<string>("system"), Is.EqualTo("System prompt"));
                Assert.That(prompts["Text"].Value<string>("user"), Is.EqualTo("User prompt"));
                Assert.That(prompts["Empty"].Value<string>("system"), Is.Empty);
            });
        }

        [Test]
        public void SettingsPayloadAppliesGeneralModelsAndPrompts()
        {
            var settings = new JObject();
            SettingsWebForm.ApplyGeneral(settings, new JObject
            {
                ["language"] = "EN",
                ["background"] = false
            }, "Ctrl+F10");

            var models = new JObject();
            SettingsWebForm.ApplyContexts(models, null);
            SettingsWebForm.ApplyContexts(models, new JObject
            {
                ["invalid"] = "skip",
                ["Code"] = new JObject
                {
                    ["provider"] = "CODEX",
                    ["model"] = "  openrouter/model  ",
                    ["codexModel"] = "  gpt-sol  ",
                    ["codexReasoningEffort"] = "XHIGH",
                    ["temperature"] = 0.1m,
                    ["maxTokens"] = 8192
                },
                ["Text"] = new JObject()
            });

            var prompts = new JObject();
            SettingsWebForm.ApplyPrompts(prompts, null);
            SettingsWebForm.ApplyPrompts(prompts, new JObject
            {
                ["invalid"] = "skip",
                ["Code"] = new JObject
                {
                    ["system"] = "Review code",
                    ["user"] = "{text}"
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(settings["general"].Value<string>("hotkey"), Is.EqualTo("Ctrl+F10"));
                Assert.That(settings["general"].Value<string>("language"), Is.EqualTo("en"));
                Assert.That(settings["general"].Value<bool>("background"), Is.False);
                Assert.That(models["contexts"]["invalid"], Is.Null);
                Assert.That(models["contexts"]["Code"].Value<string>("provider"), Is.EqualTo("codex"));
                Assert.That(models["contexts"]["Code"].Value<string>("model"), Is.EqualTo("openrouter/model"));
                Assert.That(models["contexts"]["Code"].Value<string>("codexModel"), Is.EqualTo("gpt-sol"));
                Assert.That(models["contexts"]["Code"].Value<string>("codexReasoningEffort"), Is.EqualTo("xhigh"));
                Assert.That(models["contexts"]["Text"].Value<decimal>("temperature"), Is.EqualTo(0.5m));
                Assert.That(models["contexts"]["Text"].Value<int>("maxTokens"), Is.EqualTo(2048));
                Assert.That(prompts["invalid"], Is.Null);
                Assert.That(prompts["Code"].Value<string>("system"), Is.EqualTo("Review code"));
                Assert.That(prompts["Code"].Value<string>("user"), Is.EqualTo("{text}"));
            });
        }

        [TestCase(null, "/settings.html", false)]
        [TestCase("not a uri", "/settings.html", false)]
        [TestCase("http://zbs.ui/settings.html", "/settings.html", false)]
        [TestCase("https://example.test/settings.html", "/settings.html", false)]
        [TestCase("https://zbs.ui/preview.html", "/settings.html", false)]
        [TestCase("HTTPS://ZBS.UI/settings.html", "/settings.html", true)]
        public void SettingsWebMessageSourceAllowsOnlyExpectedVirtualHost(
            string source, string expectedPath, bool expected)
        {
            Assert.That(SettingsWebForm.IsTrustedWebMessageSource(source, expectedPath),
                Is.EqualTo(expected));
            Assert.That(SettingsWebForm.ShouldHandleWebMessage(false, source, expectedPath),
                Is.EqualTo(expected));
            Assert.That(SettingsWebForm.ShouldHandleWebMessage(true, source, expectedPath),
                Is.False);
        }

        [TestCase("")]
        [TestCase(": keep-alive")]
        [TestCase("event: message")]
        public void SseParserIgnoresNonDataLines(string line)
        {
            Assert.That(PreviewForm.TryGetSsePayload(line, out var payload), Is.False);
            Assert.That(payload, Is.Empty);
        }

        [Test]
        public void SseParserReadsTrimmedPayload()
        {
            Assert.That(PreviewForm.TryGetSsePayload("data:  {\"ok\":true}  ", out var payload),
                Is.True);
            Assert.That(payload, Is.EqualTo("{\"ok\":true}"));
            Assert.That(PreviewForm.TryGetSsePayload("data:   ", out payload), Is.False);
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void StreamPreviewBuildsRequestAndProcessesSseWithoutShownWindow()
        {
            var options = CreateStreamOptions("Bearer test-token");
            using (var form = PreviewForm.ДляСтрима(options))
            {
                var request = form.CreateStreamRequest();
                Assert.Multiple(() =>
                {
                    Assert.That(request.Method, Is.EqualTo("POST"));
                    Assert.That(request.Accept, Is.EqualTo("text/event-stream"));
                    Assert.That(request.ContentType, Is.EqualTo("application/json"));
                    Assert.That(request.Headers["X-Title"], Is.EqualTo("MakeItZBS"));
                    Assert.That(request.Headers[HttpRequestHeader.Authorization],
                        Is.EqualTo("Bearer test-token"));
                });
                Assert.DoesNotThrow(() => PreviewForm.TryAbort(request));

                var body = string.Join("\n", new[]
                {
                    ": keep-alive",
                    "event: message",
                    "data: {\"choices\":[{\"delta\":{\"reasoning\":\"think\"}}]}",
                    "data: {\"choices\":[{\"delta\":{\"content\":\"answer\"}}]}",
                    "data: [DONE]",
                    string.Empty
                });
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(body)))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    Assert.That(form.ReadEventStreamAsync(reader, CancellationToken.None).Result,
                        Is.True);
                }

                form.PostStreamDone();
                Assert.That(form.StreamedContent, Is.EqualTo("answer"));
            }

            using (var noAuthForm = PreviewForm.ДляСтрима(CreateStreamOptions(string.Empty)))
            {
                Assert.That(noAuthForm.CreateStreamRequest().Headers[HttpRequestHeader.Authorization],
                    Is.Null);
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void StreamPreviewHandlesEofCancellationDeltasAndErrors()
        {
            using (var form = PreviewForm.ДляСтрима(CreateStreamOptions(string.Empty)))
            {
                using (var stream = new MemoryStream(Array.Empty<byte>()))
                using (var reader = new StreamReader(stream))
                {
                    Assert.That(form.ReadEventStreamAsync(reader, CancellationToken.None).Result,
                        Is.False);
                }

                var cancellation = new CancellationToken(true);
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes("data: ignored")))
                using (var reader = new StreamReader(stream))
                {
                    Assert.That(form.ReadEventStreamAsync(reader, cancellation).Result, Is.False);
                }

                form.ReadSseDelta("{}");
                form.ReadSseDelta("{\"choices\":[]}");
                form.ReadSseDelta("{\"choices\":[{\"delta\":{\"content\":42}}]}");
                form.HandleUnexpectedStreamEnd(true, CancellationToken.None);
                form.HandleUnexpectedStreamEnd(false, cancellation);
                Assert.That(form.StreamError, Is.Empty);

                form.HandleUnexpectedStreamEnd(false, CancellationToken.None);
                Assert.That(form.StreamError, Does.Contain("SSE-поток завершился"));
            }

            using (var timeoutForm = PreviewForm.ДляСтрима(CreateStreamOptions(string.Empty)))
            {
                timeoutForm.HandleStreamCancellation();
                Assert.That(timeoutForm.StreamError, Does.Contain("таймауту"));
            }

            using (var partialForm = PreviewForm.ДляСтрима(CreateStreamOptions(string.Empty)))
            {
                partialForm.ReadSseDelta(
                    "{\"choices\":[{\"delta\":{\"reasoning\":\"partial\"}}]}");
                partialForm.HandleStreamException(new InvalidOperationException("broken\nstream"));
                Assert.That(partialForm.StreamError, Does.StartWith("стрим прерван после частичного ответа"));
                Assert.That(partialForm.StreamError, Does.Contain("broken stream"));
            }

            Assert.Multiple(() =>
            {
                Assert.That(PreviewForm.Truncate(null), Is.Empty);
                Assert.That(PreviewForm.Truncate("a\r\nb"), Is.EqualTo("a  b"));
                Assert.That(PreviewForm.Truncate(new string('x', 2001)), Does.EndWith("..."));
            });
        }

        [TestCase("openAuthorWebsite", "https://androman.pro")]
        [TestCase("openAuthorTelegram", "https://t.me/andromanpro")]
        [TestCase("openExternalUrl", "")]
        [TestCase(null, "")]
        public void SettingsAuthorLinksAreLimitedToKnownCommands(string command, string expected)
        {
            Assert.That(SettingsWebForm.ResolveAuthorUrl(command), Is.EqualTo(expected));
        }

        [TestCase(200, "data: {\"choices\":[{\"delta\":{\"content\":\"network\"}}]}\n\ndata: [DONE]\n\n", "network")]
        [TestCase(200, "data: {\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n", "SSE-поток завершился")]
        [TestCase(500, "upstream failed", "HTTP 500: upstream failed")]
        [Apartment(ApartmentState.STA)]
        public void StreamPreviewRunsHttpRequestEndToEnd(
            int statusCode, string responseBody, string expectedResult)
        {
            using (var server = new LocalHttpServer(statusCode, responseBody))
            using (var form = PreviewForm.ДляСтрима(
                CreateStreamOptions("Bearer integration", server.Url)))
            {
                form.StreamLoopAsync(CancellationToken.None).GetAwaiter().GetResult();
                var request = server.WaitForRequest();
                Assert.That(request, Does.Contain("POST /stream HTTP/1.1"));
                Assert.That(request, Does.Contain("Authorization: Bearer integration"));
                Assert.That(request, Does.EndWith("{}"));

                if (statusCode == 200 && responseBody.Contains("[DONE]"))
                {
                    Assert.That(form.StreamedContent, Is.EqualTo(expectedResult));
                    Assert.That(form.StreamError, Is.Empty);
                }
                else
                {
                    Assert.That(form.StreamError, Does.Contain(expectedResult));
                }
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void StreamPreviewCancellationAbortsPendingNetworkRead()
        {
            using (var server = new LocalHttpServer(200, string.Empty, true))
            using (var form = PreviewForm.ДляСтрима(
                CreateStreamOptions(string.Empty, server.Url)))
            using (var cancellation = new CancellationTokenSource())
            {
                var streamTask = form.StreamLoopAsync(cancellation.Token);
                var request = server.WaitUntilResponseStarted();
                cancellation.Cancel();

                var completed = Task.WhenAny(streamTask, Task.Delay(TimeSpan.FromSeconds(5)))
                    .GetAwaiter().GetResult();
                Assert.That(completed, Is.SameAs(streamTask),
                    "Отмена должна прервать зависшее чтение SSE");
                streamTask.GetAwaiter().GetResult();

                Assert.That(request, Does.Contain("POST /stream HTTP/1.1"));
                Assert.That(form.StreamError, Is.Not.Empty);
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void StreamPreviewReportsMalformedSsePayload()
        {
            const string body = "data: not-json\n\n";
            using (var server = new LocalHttpServer(200, body))
            using (var form = PreviewForm.ДляСтрима(
                CreateStreamOptions(string.Empty, server.Url)))
            {
                form.StreamLoopAsync(CancellationToken.None).GetAwaiter().GetResult();
                server.WaitForRequest();

                Assert.That(form.StreamedContent, Is.Empty);
                Assert.That(form.StreamError, Is.Not.Empty);
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void StreamPreviewStartsOnlyOneBackgroundTask()
        {
            const string body = "data: [DONE]\n\n";
            using (var server = new LocalHttpServer(200, body))
            using (var form = PreviewForm.ДляСтрима(
                CreateStreamOptions(string.Empty, server.Url)))
            {
                var first = form.StartStreamLoop();
                var second = form.StartStreamLoop();
                Assert.That(second, Is.SameAs(first));
                first.GetAwaiter().GetResult();
                server.WaitForRequest();
            }

            var disposed = PreviewForm.ДляСтрима(CreateStreamOptions(string.Empty));
            disposed.Dispose();
            Assert.DoesNotThrow(() =>
                disposed.HandleStreamException(new InvalidOperationException("ignored")));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void StreamPreviewHandlesResponseLessAndDisposedFailures()
        {
            using (var form = PreviewForm.ДляСтрима(CreateStreamOptions(string.Empty)))
            {
                form.HandleWebExceptionAsync(new WebException("offline"))
                    .GetAwaiter().GetResult();
                Assert.That(form.StreamError, Is.EqualTo("offline"));
                Assert.That(PreviewForm.ReadResponseBodyAsync(new EmptyWebResponse())
                    .GetAwaiter().GetResult(), Is.Empty);
                Assert.DoesNotThrow(() => form.PostStreamEvent("error", null));
            }

            var disposed = PreviewForm.ДляСтрима(CreateStreamOptions(string.Empty));
            disposed.Dispose();
            Assert.DoesNotThrow(() => disposed.HandleWebExceptionAsync(
                new WebException("ignored")).GetAwaiter().GetResult());
            Assert.That(disposed.StreamError, Is.Empty);
        }

        [Test]
        public void SettingsFormContextLookupHandlesMissingAndPresentContexts()
        {
            JObject context;
            Assert.That(SettingsForm.TryGetContextObject(null, null, out context), Is.False);
            Assert.That(context, Is.Null);
            Assert.That(SettingsForm.TryGetContextObject(new JObject(), "Code", out context),
                Is.False);

            var models = JObject.Parse("{'contexts': {'Code': {'model': 'test'}}}");
            Assert.That(SettingsForm.TryGetContextObject(models, "Missing", out context), Is.False);
            Assert.That(SettingsForm.TryGetContextObject(models, "Code", out context), Is.True);
            Assert.That(context.Value<string>("model"), Is.EqualTo("test"));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void SettingsWebFormStartsColdUntilPrewarmIsRequested()
        {
            using (var form = new SettingsWebForm())
            {
                Assert.Multiple(() =>
                {
                    Assert.That(form.Outcome, Is.EqualTo("cancelled"));
                    Assert.That(form.IsPrewarmed, Is.False);
                    Assert.That(form.IsBroken, Is.False);
                    Assert.That(form.IsHandleCreated, Is.False);
                    Assert.That(form.ShowInTaskbar, Is.True);
                    Assert.That(form.AutoScaleMode, Is.EqualTo(AutoScaleMode.Dpi));
                    Assert.That(form.AutoScaleDimensions, Is.EqualTo(new SizeF(96F, 96F)));
                });
            }
        }

        [TestCase(96, 900, 720)]
        [TestCase(120, 1125, 900)]
        [TestCase(144, 1350, 1080)]
        [TestCase(168, 1575, 1260)]
        public void LogicalFormSizeUsesCurrentMonitorDpi(
            int dpi, int expectedWidth, int expectedHeight)
        {
            Assert.That(
                DpiAwareness.ScaleLogicalSize(new Size(900, 720), dpi),
                Is.EqualTo(new Size(expectedWidth, expectedHeight)));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void SettingsPrewarmUsesDpiOfItsActualMonitor()
        {
            DpiAwareness.TryEnablePerMonitorV2ForCurrentThread();

            using (var form = new SettingsWebForm())
            {
                form.BeginPrewarm(false);
                var dpi = DpiAwareness.GetWindowDpi(form);

                Assert.Multiple(() =>
                {
                    Assert.That(form.IsHandleCreated, Is.True);
                    Assert.That(dpi, Is.GreaterThanOrEqualTo(96));
                    Assert.That(form.Size, Is.EqualTo(
                        DpiAwareness.ScaleLogicalSize(new Size(900, 720), dpi)));
                    Assert.That(form.MinimumSize, Is.EqualTo(
                        DpiAwareness.ScaleLogicalSize(new Size(720, 520), dpi)));
                    Assert.That(form.Visible, Is.False);
                });
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void DpiSizingSupportsColdFormAndDefaultDpi()
        {
            using (var form = new Form())
            {
                DpiAwareness.ConfigureForm(form);
                var coldDpi = DpiAwareness.GetWindowDpi(form);
                DpiAwareness.ApplyInitialFormSize(
                    form, new Size(320, 240), Size.Empty);

                Assert.Multiple(() =>
                {
                    Assert.That(coldDpi, Is.GreaterThanOrEqualTo(96));
                    Assert.That(form.AutoScaleMode, Is.EqualTo(AutoScaleMode.Dpi));
                    Assert.That(form.Size, Is.EqualTo(
                        DpiAwareness.ScaleLogicalSize(new Size(320, 240), coldDpi)));
                    Assert.That(form.MinimumSize, Is.EqualTo(Size.Empty));
                    Assert.That(
                        DpiAwareness.ScaleLogicalSize(new Size(10, 20), 0),
                        Is.EqualTo(new Size(10, 20)));
                });
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void SettingsWebFormBuildsCompletePayloadFromProjectConfiguration()
        {
            using (var form = new SettingsWebForm())
            {
                var payload = form.BuildLoadPayload();

                Assert.Multiple(() =>
                {
                    Assert.That(payload.Value<bool>("configError"), Is.False,
                        payload.Value<string>("configErrorMessage"));
                    Assert.That(payload["general"], Is.TypeOf<JObject>());
                    Assert.That(payload["aiChat"]?["profiles"], Is.TypeOf<JObject>());
                    Assert.That(payload["contexts"], Is.TypeOf<JObject>());
                    Assert.That(payload["prompts"], Is.TypeOf<JObject>());
                    Assert.That(payload["catalog"], Is.TypeOf<JArray>());
                    Assert.That(payload["codexStatus"], Is.TypeOf<JObject>());
                    Assert.That(payload["general"]?.Value<string>("language"),
                        Is.AnyOf(UiText.Russian, UiText.English));
                });
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void PreviewFormsStartCancelledAndReleaseLoadedImageWithoutShowing()
        {
            var imagePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".png");
            using (var bitmap = new Bitmap(2, 2))
            {
                bitmap.Save(imagePath, ImageFormat.Png);
            }

            try
            {
                using (var preview = new PreviewForm("before", "after", "Text", "model", 0, false))
                {
                    preview.ApplyInitialDpiLayout();
                    var dpi = DpiAwareness.GetWindowDpi(preview);

                    Assert.That(preview.Outcome, Is.EqualTo("cancelled"));
                    Assert.That(preview.AutoScaleMode, Is.EqualTo(AutoScaleMode.Dpi));
                    Assert.That(preview.Size, Is.EqualTo(
                        DpiAwareness.ScaleLogicalSize(new Size(900, 650), dpi)));
                    Assert.That(preview.MinimumSize, Is.EqualTo(
                        DpiAwareness.ScaleLogicalSize(new Size(720, 460), dpi)));
                }

                using (var imagePreview = new ImagePreviewForm(imagePath, "Image", "model", 0))
                {
                    imagePreview.ApplyInitialDpiLayout();
                    var dpi = DpiAwareness.GetWindowDpi(imagePreview);

                    Assert.That(imagePreview.Outcome, Is.EqualTo("cancelled"));
                    Assert.That(imagePreview.AutoScaleMode, Is.EqualTo(AutoScaleMode.Dpi));
                    Assert.That(imagePreview.Size, Is.EqualTo(
                        DpiAwareness.ScaleLogicalSize(new Size(900, 680), dpi)));
                    Assert.That(imagePreview.MinimumSize, Is.EqualTo(
                        DpiAwareness.ScaleLogicalSize(new Size(480, 360), dpi)));
                    imagePreview.Close();
                    Assert.That(imagePreview.Outcome, Is.EqualTo("cancelled"));
                }

                Assert.That(() => File.Delete(imagePath), Throws.Nothing);
            }
            finally
            {
                if (File.Exists(imagePath))
                {
                    File.Delete(imagePath);
                }
            }
        }

        [Test]
        public void HotkeyPollerUsesReadyMessageLoopWithoutSettingsUi()
        {
            var manager = new HotkeyManager(() => null);
            Assert.That(manager.IsMessageLoopReady, Is.True);

            using (var poller = new HotkeyPoller(manager))
            {
                poller.OnHotkeyPressed("Ctrl+F12");
                Assert.That(poller.GetEventCount(), Is.EqualTo(1));
                Assert.That(poller.WaitForPressedKey(100), Is.EqualTo("Ctrl+F12"));
                poller.ClearQueue();

                poller.OnHotkeyPressed("Shift+F10");
                Assert.That(poller.WaitForPress(100), Is.True);
                poller.ClearQueue();

                poller.OnHotkeyPressed("Alt+F11");
                poller.ClearQueue();
                Assert.Multiple(() =>
                {
                    Assert.That(poller.GetEventCount(), Is.Zero);
                    Assert.That(poller.WaitForPress(0), Is.False);
                    Assert.That(poller.WaitForPressedKey(0), Is.Empty);
                });
            }

            Assert.That(manager.IsRunning, Is.False);

            var streamResult = (IRuntimeContextInstance)manager.ShowPreviewStreaming(null);
            Assert.Multiple(() =>
            {
                Assert.That(manager.ShowPreview("", "", "Text", "model", 0, false), Is.EqualTo("cancelled"));
                Assert.That(manager.ShowImagePreview("missing.png", "Image", "model", 0), Is.EqualTo("cancelled"));
                Assert.That(ReadString(streamResult, "Исход"), Is.EqualTo("cancelled"));
                Assert.That(ReadString(streamResult, "ОшибкаСтрима"), Is.EqualTo("окно не готово"));
            });
        }

        [Test]
        public void WindowInfoSafelyRejectsInvalidTargetsAndEmptyText()
        {
            var windowInfo = WindowInfo.Constructor();
            var capture = (IRuntimeContextInstance)windowInfo.GetAiChatText(0, 123, 10);
            Assert.That(ReadBool(capture, "Успех"), Is.False);
            Assert.That(ReadString(capture, "Диагностика"), Does.Contain("не существует"));

            foreach (var result in new[]
            {
                windowInfo.SendAiChatMessage(0, 0, ""),
                windowInfo.InsertAiChatDraft(0, 0, "")
            })
            {
                var operation = (IRuntimeContextInstance)result;
                Assert.That(ReadBool(operation, "Успех"), Is.False);
                Assert.That(ReadString(operation, "Диагностика"), Does.Contain("пустой текст"));
            }

            var legacy = (IRuntimeContextInstance)windowInfo.InsertTextIntoWindow(0, "text", "selection");
            Assert.That(ReadBool(legacy, "Успех"), Is.False);
            Assert.That(ReadString(legacy, "Диагностика"), Does.Contain("CaptureReplacement"));
            Assert.That(windowInfo.CaptureReplacement(0, 123, "selection", "text"), Is.Empty);
            windowInfo.ReleaseReplacement();
            var unbound = (IRuntimeContextInstance)windowInfo.ReplaceCapturedText("forged-token", "new text");
            Assert.That(ReadBool(unbound, "Успех"), Is.False);
            Assert.That(ReadString(unbound, "Диагностика"), Does.Contain("устарела"));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void WindowInfoRejectsAiChatWindowOwnedByAnotherProcess()
        {
            using (var target = new Form())
            {
                var handle = target.Handle.ToInt32();
                var unexpectedProcessId = Process.GetCurrentProcess().Id + 1;
                var windowInfo = WindowInfo.Constructor();

                var capture = (IRuntimeContextInstance)windowInfo.GetAiChatText(
                    handle, unexpectedProcessId, 1000);

                Assert.That(ReadBool(capture, "Успех"), Is.False);
                Assert.That(ReadString(capture, "Диагностика"),
                    Does.Contain("другому процессу"));
            }
        }

        [Test]
        [Category("Desktop")]
        public void WindowInfoCollectsVisibleAiChatTextAndRedactsSecrets()
        {
            const string secret = "DEMO_UIA_SECRET";
            var visibleText = "Next step token=" + secret;
            using (var host = new UiTextWindow(visibleText))
            {
                Assert.That(host.HasAutomationText(visibleText), Is.True,
                    "This desktop test requires an interactive UI Automation session.");

                var windowInfo = WindowInfo.Constructor();
                var capture = (IRuntimeContextInstance)windowInfo.GetAiChatText(
                    host.Handle.ToInt32(), Process.GetCurrentProcess().Id, 1000);

                Assert.Multiple(() =>
                {
                    Assert.That(ReadBool(capture, "Успех"), Is.True,
                        ReadString(capture, "Диагностика"));
                    Assert.That(ReadString(capture, "Текст"), Does.Contain("[REDACTED]"));
                    Assert.That(ReadString(capture, "Текст"), Does.Not.Contain(secret));
                    Assert.That(ReadNumber(capture, "СкрытоСекретов"), Is.EqualTo(1));
                });
            }
        }

        [Test]
        public void WindowInfoOneCAndUiaDiagnosticsReturnStructuredFallbacks()
        {
            var windowInfo = WindowInfo.Constructor();
            var capture = (IRuntimeContextInstance)windowInfo.GetOneCModuleText();
            Assert.That(ReadString(capture, "Диагностика"), Is.Not.Null);
            Assert.That(windowInfo.GetOneCModuleFullText(), Is.Not.Null);
            Assert.That(windowInfo.DiagnoseUia(), Does.Contain("UIA DIAGNOSE"));
        }

        [Test]
        public void WindowInfoReadsAndCropsPngWithBoundaryClamping()
        {
            var root = Path.Combine(
                Path.GetTempPath(), "zbs-window-image-" + Guid.NewGuid().ToString("N"));
            var sourcePath = Path.Combine(root, "source.png");
            var cropPath = Path.Combine(root, "crop", "result.png");
            var malformedPath = Path.Combine(root, "malformed.png");
            Directory.CreateDirectory(root);

            try
            {
                using (var bitmap = new Bitmap(20, 10))
                {
                    bitmap.SetPixel(0, 4, Color.Crimson);
                    bitmap.Save(sourcePath, ImageFormat.Png);
                }
                File.WriteAllText(malformedPath, "not an image");

                var windowInfo = WindowInfo.Constructor();
                var sourceSize = (IRuntimeContextInstance)windowInfo.GetPngSize(sourcePath);
                var cropped = windowInfo.CropPng(sourcePath, -5, 4, 999, 999, cropPath);
                var cropSize = (IRuntimeContextInstance)windowInfo.GetPngSize(cropPath);
                var missingSize = (IRuntimeContextInstance)windowInfo.GetPngSize(
                    Path.Combine(root, "missing.png"));
                var malformedSize = (IRuntimeContextInstance)windowInfo.GetPngSize(malformedPath);

                Assert.Multiple(() =>
                {
                    Assert.That(ReadNumber(sourceSize, "Ширина"), Is.EqualTo(20));
                    Assert.That(ReadNumber(sourceSize, "Высота"), Is.EqualTo(10));
                    Assert.That(cropped, Is.True);
                    Assert.That(ReadNumber(cropSize, "Ширина"), Is.EqualTo(20));
                    Assert.That(ReadNumber(cropSize, "Высота"), Is.EqualTo(6));
                    Assert.That(ReadNumber(missingSize, "Ширина"), Is.Zero);
                    Assert.That(ReadString(missingSize, "Диагностика"), Does.Contain("файл не найден"));
                    Assert.That(ReadNumber(malformedSize, "Высота"), Is.Zero);
                    Assert.That(ReadString(malformedSize, "Диагностика"), Does.Contain("РазмерPNG"));
                    Assert.That(windowInfo.CropPng(
                        Path.Combine(root, "missing.png"), 0, 0, 1, 1, cropPath), Is.False);
                    Assert.That(windowInfo.CropPng(
                        malformedPath, 0, 0, 1, 1, cropPath), Is.False);
                    Assert.That(windowInfo.GetLastCaptureDiagnostic(), Does.Contain("ОбрезатьPNG"));
                });
            }
            finally
            {
                if (Directory.Exists(root))
                {
                    Directory.Delete(root, true);
                }
            }
        }

        [Test]
        public void WindowInfoDiscoveryHandlesDesktopAndMissingWindows()
        {
            var windowInfo = WindowInfo.Constructor();
            var impossibleTitle = "make-it-zbs-missing-" + Guid.NewGuid().ToString("N");
            var active = windowInfo.GetActiveWindow();
            var partial = windowInfo.FindWindowByTitle(impossibleTitle);
            var exact = windowInfo.FindWindowByExactTitle(impossibleTitle);
            var windows = windowInfo.GetAllWindows();

            Assert.Multiple(() =>
            {
                Assert.That(active, Is.Not.Null);
                Assert.That(partial.DataType, Is.EqualTo(DataType.Undefined));
                Assert.That(exact.DataType, Is.EqualTo(DataType.Undefined));
                Assert.That(windows, Is.Not.Null);
                Assert.That(windowInfo.IsWindowActive(0), Is.False);
                Assert.That(windowInfo.ActivateWindow(0), Is.False);
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

        private static int ReadNumber(IRuntimeContextInstance source, string name)
        {
            return (int)source.GetPropValue(source.FindProperty(name)).AsNumber();
        }

        private sealed class UiTextWindow : IDisposable
        {
            private readonly ManualResetEventSlim _ready = new ManualResetEventSlim();
            private readonly Thread _thread;
            private Form _form;

            public UiTextWindow(string text)
            {
                _thread = new Thread(() =>
                {
                    using (var form = new Form
                    {
                        ShowInTaskbar = false,
                        StartPosition = FormStartPosition.Manual,
                        Location = new Point(-32000, -32000),
                        Size = new Size(320, 120),
                        Text = "Make it ZBS UIA test"
                    })
                    {
                        form.Controls.Add(new Label
                        {
                            AutoSize = true,
                            Text = text
                        });
                        _form = form;
                        form.Shown += (sender, args) => _ready.Set();
                        Application.Run(form);
                    }
                });
                _thread.SetApartmentState(ApartmentState.STA);
                _thread.IsBackground = true;
                _thread.Start();
                if (!_ready.Wait(TimeSpan.FromSeconds(10)))
                {
                    throw new TimeoutException("UIA test window did not start.");
                }
            }

            public IntPtr Handle => _form.Handle;

            public bool HasAutomationText(string expectedText)
            {
                try
                {
                    var root = AutomationElement.FromHandle(Handle);
                    return root?.FindFirst(
                        TreeScope.Descendants,
                        new PropertyCondition(
                            AutomationElement.NameProperty,
                            expectedText)) != null;
                }
                catch (ElementNotAvailableException)
                {
                    return false;
                }
            }

            public void Dispose()
            {
                if (_form != null && !_form.IsDisposed)
                {
                    _form.BeginInvoke(new Action(_form.Close));
                }
                _thread.Join(TimeSpan.FromSeconds(10));
                _ready.Dispose();
            }
        }

        private static StreamPreviewOptions CreateStreamOptions(
            string authHeader, string url = "https://example.test/stream")
        {
            var request = new StructureImpl();
            request.Insert("url", ValueFactory.Create(url));
            request.Insert("authHeader", ValueFactory.Create(authHeader));
            request.Insert("bodyJson", ValueFactory.Create("{}"));
            return StreamPreviewOptions.FromRuntimeContext(
                request, "original", "context", "model", 15, true);
        }

        private static async Task<string> ReadHttpRequestAsync(NetworkStream stream)
        {
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
            {
                var request = new StringBuilder();
                var contentLength = 0;
                var expectsContinue = false;
                string line;
                while (!string.IsNullOrEmpty(
                    line = await reader.ReadLineAsync().ConfigureAwait(false)))
                {
                    request.Append(line).Append("\r\n");
                    if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    {
                        contentLength = int.Parse(line.Substring(line.IndexOf(':') + 1).Trim());
                    }
                    else if (line.StartsWith("Expect: 100-continue", StringComparison.OrdinalIgnoreCase))
                    {
                        expectsContinue = true;
                    }
                }

                request.Append("\r\n");
                if (expectsContinue)
                {
                    var interim = Encoding.ASCII.GetBytes("HTTP/1.1 100 Continue\r\n\r\n");
                    await stream.WriteAsync(interim, 0, interim.Length).ConfigureAwait(false);
                }

                var body = new char[contentLength];
                var offset = 0;
                while (offset < body.Length)
                {
                    var read = await reader.ReadAsync(body, offset, body.Length - offset)
                        .ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    offset += read;
                }

                request.Append(body, 0, offset);
                return request.ToString();
            }
        }

        private sealed class LocalHttpServer : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly Task<string> _requestTask;
            private readonly bool _holdResponseOpen;
            private readonly TaskCompletionSource<bool> _releaseResponse =
                new TaskCompletionSource<bool>();
            private readonly TaskCompletionSource<string> _responseStarted =
                new TaskCompletionSource<string>();

            public LocalHttpServer(
                int statusCode, string responseBody, bool holdResponseOpen = false)
            {
                _holdResponseOpen = holdResponseOpen;
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                var endpoint = (IPEndPoint)_listener.LocalEndpoint;
                Url = "http://127.0.0.1:" + endpoint.Port + "/stream";
                _requestTask = Task.Run(
                    () => ServeOnceAsync(statusCode, responseBody ?? string.Empty));
            }

            public string Url { get; }

            public string WaitForRequest()
            {
                return _requestTask.GetAwaiter().GetResult();
            }

            public string WaitUntilResponseStarted()
            {
                var completed = Task.WhenAny(
                    _responseStarted.Task, Task.Delay(TimeSpan.FromSeconds(5)))
                    .GetAwaiter().GetResult();
                if (!ReferenceEquals(completed, _responseStarted.Task))
                {
                    throw new TimeoutException("Локальный SSE-сервер не начал ответ");
                }

                return _responseStarted.Task.GetAwaiter().GetResult();
            }

            public void Dispose()
            {
                _releaseResponse.TrySetResult(true);
                _listener.Stop();
            }

            private async Task<string> ServeOnceAsync(int statusCode, string responseBody)
            {
                using (var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false))
                using (var stream = client.GetStream())
                {
                    var request = await ReadHttpRequestAsync(stream).ConfigureAwait(false);

                    if (_holdResponseOpen)
                    {
                        var streamingHeaders = Encoding.ASCII.GetBytes(
                            "HTTP/1.1 200 OK\r\n"
                            + "Content-Type: text/event-stream; charset=utf-8\r\n"
                            + "Connection: close\r\n\r\n");
                        await stream.WriteAsync(streamingHeaders, 0, streamingHeaders.Length)
                            .ConfigureAwait(false);
                        await stream.FlushAsync().ConfigureAwait(false);
                        _responseStarted.TrySetResult(request);
                        await _releaseResponse.Task.ConfigureAwait(false);
                        return request;
                    }

                    var responseBytes = Encoding.UTF8.GetBytes(responseBody);
                    var reason = statusCode == 200 ? "OK" : "Internal Server Error";
                    var headers = Encoding.ASCII.GetBytes(
                        "HTTP/1.1 " + statusCode + " " + reason + "\r\n"
                        + "Content-Type: text/event-stream; charset=utf-8\r\n"
                        + "Content-Length: " + responseBytes.Length + "\r\n"
                        + "Connection: close\r\n\r\n");
                    await stream.WriteAsync(headers, 0, headers.Length).ConfigureAwait(false);
                    await stream.WriteAsync(responseBytes, 0, responseBytes.Length)
                        .ConfigureAwait(false);
                    return request;
                }
            }
        }

        private sealed class EmptyWebResponse : WebResponse
        {
            public override Stream GetResponseStream()
            {
                return null;
            }
        }
    }
}
