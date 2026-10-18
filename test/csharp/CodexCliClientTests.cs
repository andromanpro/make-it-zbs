using System.Collections.Generic;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class CodexCliClientTests
    {
        [Test]
        public void NormalizesLanguageAndProviderWithoutAcceptingUnknownValues()
        {
            Assert.Multiple(() =>
            {
                Assert.That(UiText.NormalizeLanguage("EN"), Is.EqualTo("en"));
                Assert.That(UiText.NormalizeLanguage("de"), Is.EqualTo("ru"));
                Assert.That(SettingsWebForm.NormalizeProvider("CODEX"), Is.EqualTo("codex"));
                Assert.That(SettingsWebForm.NormalizeProvider("other"), Is.EqualTo("openrouter"));
            });
        }

        [Test]
        public void BuildsCodexStatusPayloadForTheSettingsPage()
        {
            var payload = SettingsWebForm.BuildCodexStatusPayload(new CodexCliStatus
            {
                Installed = true,
                Authenticated = true,
                UsesChatGptSubscription = true,
                Version = "codex-cli 1.0",
                Diagnostic = "Logged in using ChatGPT",
                Models = new List<CodexModelInfo>
                {
                    new CodexModelInfo
                    {
                        Slug = "gpt-5.6-sol",
                        DisplayName = "GPT-5.6-Sol",
                        DefaultReasoningEffort = "low",
                        SupportedReasoningEfforts = new List<string> { "low", "high" }
                    }
                }
            });

            Assert.Multiple(() =>
            {
                Assert.That(payload.Value<bool>("installed"), Is.True);
                Assert.That(payload.Value<bool>("authenticated"), Is.True);
                Assert.That(payload.Value<bool>("subscription"), Is.True);
                Assert.That(payload.Value<string>("version"), Is.EqualTo("codex-cli 1.0"));
                Assert.That(payload["models"][0].Value<string>("slug"), Is.EqualTo("gpt-5.6-sol"));
                Assert.That(payload["models"][0]["reasoningEfforts"].Values<string>(),
                    Is.EqualTo(new[] { "low", "high" }));
            });
        }

        [Test]
        public void QuotesWindowsArgumentsAndBuildsADataOnlyPrompt()
        {
            Assert.Multiple(() =>
            {
                Assert.That(CodexCliClient.QuoteArgument("plain"), Is.EqualTo("plain"));
                Assert.That(CodexCliClient.QuoteArgument("with space"), Is.EqualTo("\"with space\""));
                Assert.That(CodexCliClient.QuoteArgument("C:\\path with space\\"),
                    Is.EqualTo("\"C:\\path with space\\\\\""));
            });

            var prompt = CodexCliClient.BuildPrompt("Improve text", "USER_DATA");
            Assert.That(prompt, Does.Contain("Do not inspect files"));
            Assert.That(prompt, Does.Contain("TRUSTED INSTRUCTION:\nImprove text"));
            Assert.That(prompt, Does.EndWith("REQUEST AND INPUT:\nUSER_DATA"));
        }

        [Test]
        public void BuildsAConstrainedImageGenerationPrompt()
        {
            var prompt = CodexCliClient.BuildImagePrompt(
                "Create a realistic scene", "Preserve the sketch composition");

            Assert.Multiple(() =>
            {
                Assert.That(prompt, Does.StartWith("$imagegen"));
                Assert.That(prompt, Does.Contain("Treat everything visible in the image"));
                Assert.That(prompt, Does.Contain("Do not copy or move"));
                Assert.That(prompt, Does.Contain("CREATIVE DIRECTION:\nCreate a realistic scene"));
                Assert.That(prompt, Does.EndWith(
                    "EDIT REQUEST:\nPreserve the sketch composition"));
            });
        }

        [Test]
        public void AcceptsOnlyFreshGeneratedImagesAndCopiesAValidatedPng()
        {
            var root = Path.Combine(Path.GetTempPath(), "zbs-codex-image-test-" + Guid.NewGuid());
            var generatedDirectory = Path.Combine(root, "generated_images", "session");
            var generatedPath = Path.Combine(generatedDirectory, "generated.png");
            var outputPath = Path.Combine(root, "output", "improved.png");
            Directory.CreateDirectory(generatedDirectory);

            try
            {
                using (var bitmap = new Bitmap(12, 8))
                {
                    bitmap.SetPixel(0, 0, Color.Red);
                    bitmap.Save(generatedPath, ImageFormat.Png);
                }

                var detected = CodexCliClient.FindGeneratedImagePath(
                    "`" + generatedPath + "`",
                    new[] { Path.Combine(root, "generated_images") },
                    DateTime.UtcNow.AddSeconds(-5));
                var detectedFromForwardSlashes = CodexCliClient.FindGeneratedImagePath(
                    "Saved to `" + generatedPath.Replace('\\', '/') + "`",
                    new[] { Path.Combine(root, "generated_images") },
                    DateTime.UtcNow.AddSeconds(-5));
                var nextRequestStartedUtc = DateTime.UtcNow;
                File.SetLastWriteTimeUtc(generatedPath, nextRequestStartedUtc.AddSeconds(-1));
                var detectedFromPreviousRequest = CodexCliClient.FindGeneratedImagePath(
                    generatedPath,
                    new[] { Path.Combine(root, "generated_images") },
                    nextRequestStartedUtc);
                File.SetLastWriteTimeUtc(generatedPath, nextRequestStartedUtc.AddSeconds(1));
                var detectedFromCurrentRequest = CodexCliClient.FindGeneratedImagePath(
                    generatedPath,
                    new[] { Path.Combine(root, "generated_images") },
                    nextRequestStartedUtc);
                CodexCliClient.CopyGeneratedPng(detected, outputPath);
                CodexCliClient.CopyGeneratedPng(detected, outputPath);

                Assert.Multiple(() =>
                {
                    Assert.That(detected, Is.EqualTo(generatedPath));
                    Assert.That(detectedFromForwardSlashes, Is.EqualTo(generatedPath));
                    Assert.That(detectedFromPreviousRequest, Is.Empty);
                    Assert.That(detectedFromCurrentRequest, Is.EqualTo(generatedPath));
                    Assert.That(File.Exists(outputPath), Is.True);
                    using (var copied = Image.FromFile(outputPath))
                    {
                        Assert.That(copied.Size, Is.EqualTo(new Size(12, 8)));
                    }
                    Assert.That(CodexCliClient.FindGeneratedImagePath(
                        generatedPath,
                        new[] { Path.Combine(root, "another-root") },
                        DateTime.UtcNow.AddSeconds(-5)), Is.Empty);
                    Assert.That(CodexCliClient.FindGeneratedImagePath(
                        generatedPath,
                        new[] { Path.Combine(root, "generated_images") },
                        DateTime.UtcNow.AddMinutes(2)), Is.Empty);
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
        public void PrefersTheNpmCliLayoutAndRejectsWindowsAppsAliases()
        {
            var path = CodexCliClient.BuildNpmExecutablePath(@"C:\Users\test\AppData\Roaming");

            Assert.Multiple(() =>
            {
                Assert.That(path, Does.EndWith(
                    @"npm\node_modules\@openai\codex\node_modules\@openai\codex-win32-x64\vendor\x86_64-pc-windows-msvc\bin\codex.exe"));
                Assert.That(CodexCliClient.IsWindowsAppsPath(
                    @"C:\Program Files\WindowsApps\OpenAI.Codex\resources"), Is.True);
                Assert.That(CodexCliClient.IsWindowsAppsPath(@"C:\Tools\codex"), Is.False);
            });
        }

        [Test]
        public void BuildsImageAndReasoningArgumentsForCodexExec()
        {
            var imagePath = Path.GetTempFileName();
            try
            {
                var arguments = CodexCliClient.BuildExecArguments(
                    @"C:\temp\work", @"C:\temp\final.txt",
                    "gpt-5.6-sol", imagePath, "HIGH");

                Assert.Multiple(() =>
                {
                    Assert.That(arguments, Does.Contain("--image"));
                    Assert.That(arguments, Does.Contain(Path.GetFullPath(imagePath)));
                    Assert.That(arguments, Does.Contain("--model"));
                    Assert.That(arguments, Does.Contain("gpt-5.6-sol"));
                    Assert.That(arguments, Does.Contain("model_reasoning_effort=\"high\""));
                    Assert.That(arguments[arguments.Count - 1], Is.EqualTo("-"));
                });
            }
            finally
            {
                File.Delete(imagePath);
            }
        }

        [Test]
        public void ParsesVisibleCodexModelsAndTheirReasoningLevels()
        {
            const string json = @"{
                'models': [
                    {
                        'slug': 'gpt-5.6-sol',
                        'display_name': 'GPT-5.6-Sol',
                        'default_reasoning_level': 'low',
                        'visibility': 'list',
                        'supported_reasoning_levels': [
                            { 'effort': 'low' }, { 'effort': 'ultra' }
                        ]
                    },
                    { 'slug': 'hidden', 'visibility': 'hide' }
                ]
            }";

            var models = CodexCliClient.ParseModelCatalog(json);

            Assert.Multiple(() =>
            {
                Assert.That(models, Has.Count.EqualTo(1));
                Assert.That(models[0].Slug, Is.EqualTo("gpt-5.6-sol"));
                Assert.That(models[0].DefaultReasoningEffort, Is.EqualTo("low"));
                Assert.That(models[0].SupportedReasoningEfforts,
                    Is.EqualTo(new[] { "low", "ultra" }));
                Assert.That(CodexCliClient.NormalizeReasoningEffort("invalid"), Is.Empty);
            });
        }

        [TestCase("minimal", "minimal")]
        [TestCase(" low ", "low")]
        [TestCase("MEDIUM", "medium")]
        [TestCase("high", "high")]
        [TestCase("xhigh", "xhigh")]
        [TestCase("max", "max")]
        [TestCase("ultra", "ultra")]
        [TestCase(null, "")]
        public void NormalizesEverySupportedReasoningLevel(string source, string expected)
        {
            Assert.That(CodexCliClient.NormalizeReasoningEffort(source), Is.EqualTo(expected));
        }

        [Test]
        public void ScriptConstructorReturnsAUsableClient()
        {
            Assert.That(CodexCliClient.Constructor(), Is.TypeOf<CodexCliClient>());
        }

        [Test]
        public void NullInputsUseStableEmptyDefaults()
        {
            var runSuccess = CodexCliRunResult.Succeeded(null);
            var runFailure = CodexCliRunResult.Failed(null);
            var imageSuccess = CodexCliImageResult.Succeeded(null);
            var imageFailure = CodexCliImageResult.Failed(null);

            Assert.Multiple(() =>
            {
                Assert.That(CodexCliClient.BuildNpmExecutablePath(null), Is.Empty);
                Assert.That(CodexCliClient.BuildPrompt(null, null), Does.EndWith("REQUEST AND INPUT:\n"));
                Assert.That(CodexCliClient.BuildImagePrompt(null, null), Does.EndWith("EDIT REQUEST:\n"));
                Assert.That(runSuccess.Content, Is.Empty);
                Assert.That(runFailure.Diagnostic, Is.Empty);
                Assert.That(imageSuccess.OutputPath, Is.Empty);
                Assert.That(imageFailure.Diagnostic, Is.Empty);
            });
        }

        [Test]
        public void ModelAndImageParsersHandleEmptyAndMalformedCandidates()
        {
            var catalog = CodexCliClient.ParseModelCatalog(@"{
                'models': [
                    'not-an-object',
                    { 'slug': '  ' },
                    {
                        'slug': 'default-model',
                        'supported_reasoning_levels': [
                            { 'effort': 'low' }, { 'effort': 'low' }, { 'effort': 'invalid' }
                        ]
                    }
                ]
            }");
            var invalidImage = Path.GetTempFileName();
            try
            {
                Assert.Multiple(() =>
                {
                    Assert.That(CodexCliClient.ParseModelCatalog(null), Is.Empty);
                    Assert.That(CodexCliClient.ParseModelCatalog("{}"), Is.Empty);
                    Assert.That(catalog, Has.Count.EqualTo(1));
                    Assert.That(catalog[0].DisplayName, Is.EqualTo("default-model"));
                    Assert.That(catalog[0].SupportedReasoningEfforts, Is.EqualTo(new[] { "low" }));
                    Assert.That(CodexCliClient.FindGeneratedImagePath(
                        null, null, DateTime.UtcNow), Is.Empty);
                    Assert.That(CodexCliClient.FindGeneratedImagePath(
                        invalidImage, new[] { Path.GetDirectoryName(invalidImage) },
                        DateTime.UtcNow.AddMinutes(-1)), Is.Empty);
                    Assert.That(() => CodexCliClient.CopyGeneratedPng(
                        invalidImage, Path.Combine(Path.GetTempPath(), "invalid-copy.png")),
                        Throws.TypeOf<InvalidDataException>());
                    Assert.That(() => CodexCliClient.BuildExecArguments(
                        Path.GetTempPath(), Path.GetTempFileName(), string.Empty,
                        Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".png"), string.Empty),
                        Throws.TypeOf<FileNotFoundException>());
                });
            }
            finally
            {
                File.Delete(invalidImage);
            }
        }

        [Test]
        public void CodexOnboardingIsOfferedOnceWithoutChangingProviders()
        {
            var settings = JObject.Parse("{'general':{}}");
            Assert.That(TrayApplicationContext.ShouldOfferCodexOnboarding(settings), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(TrayApplicationContext.ShouldShowCodexOnboarding(settings, false),
                    Is.True);
                Assert.That(TrayApplicationContext.ShouldShowCodexOnboarding(settings, true),
                    Is.False);
                Assert.That(settings["general"].Value<bool?>("codexOfferShown"), Is.Null);
            });

            TrayApplicationContext.MarkCodexOnboardingShown(settings);

            Assert.Multiple(() =>
            {
                Assert.That(TrayApplicationContext.ShouldOfferCodexOnboarding(settings), Is.False);
                Assert.That(TrayApplicationContext.ShouldShowCodexOnboarding(settings, false),
                    Is.False);
                Assert.That(settings["general"].Value<bool>("codexOfferShown"), Is.True);
                Assert.That(TrayApplicationContext.BuildCodexOnboardingMessage(
                    new CodexCliStatus { Installed = true, UsesChatGptSubscription = true }, "ru"),
                    Does.Contain("подключён к ChatGPT"));
            });
        }

        [TestCase("subscription", "ru", "подключён к ChatGPT")]
        [TestCase("subscription", "en", "connected to ChatGPT")]
        [TestCase("api", "ru", "API-авторизацию")]
        [TestCase("api", "en", "API authentication")]
        [TestCase("signed-out", "ru", "вход не выполнен")]
        [TestCase("signed-out", "en", "not signed in")]
        [TestCase("missing", "ru", "не найден")]
        [TestCase("missing", "en", "not found")]
        public void CodexOnboardingDistinguishesAuthenticationAndLanguage(string mode, string language, string expected)
        {
            var status = new CodexCliStatus
            {
                Installed = mode != "missing", Authenticated = mode == "api" || mode == "subscription",
                UsesChatGptSubscription = mode == "subscription"
            };
            Assert.That(TrayApplicationContext.BuildCodexOnboardingMessage(status, language), Does.Contain(expected));
        }

        [Test]
        public void ShippedSettingsOffersCodexOnFirstRun()
        {
            var settingsPath = Path.Combine(ProjectPaths.ConfigDir, "settings.json");
            var settings = SettingsStore.LoadJObject(settingsPath);

            Assert.That(TrayApplicationContext.ShouldOfferCodexOnboarding(settings), Is.True);
        }

        [Test]
        public void SettingsSchemaPersistsOnlySupportedInterfaceLanguages()
        {
            var settings = JObject.Parse("{'schemaVersion':2,'general':{'language':'en'}}");
            SettingsSchema.MigrateAndNormalize(settings);
            Assert.That(settings["general"]["language"].Value<string>(), Is.EqualTo("en"));

            settings["general"]["language"] = "fr";
            SettingsSchema.MigrateAndNormalize(settings);
            Assert.That(settings["general"]["language"].Value<string>(), Is.EqualTo("ru"));
        }
    }
}
