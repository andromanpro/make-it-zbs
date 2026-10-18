using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using ZBSWindowsComponent.Helpers;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class InfrastructureTests
    {
        [Test]
        public void RedactsCommonSecretFormats()
        {
            const string source = "Authorization: Bearer abcdefghijklmnopqrstuvwxyz\n"
                + "OPENROUTER_API_KEY=sk-or-v1-xxxxxxxxxxxxxxxxxxxxxxxx\n" // secret-scan: allow
                + "token: gitea_yyyyyyyyyyyyyyyyyyyyyyyy\n" // secret-scan: allow
                + "jwt eyJabcdefghijk.abcdefghijk.abcdefghijk";

            var result = SecretRedactor.Redact(source, out var count);
            Assert.That(count, Is.GreaterThanOrEqualTo(4));
            Assert.That(result, Does.Not.Contain("abcdefghijklmnopqrstuvwxyz"));
            Assert.That(result, Does.Contain("[REDACTED]"));
        }

        [Test]
        public void RedactsPrivateKeyBlock()
        {
            var result = SecretRedactor.Redact(
                "-----BEGIN PRIVATE KEY-----\nsecret\n-----END PRIVATE KEY-----", out var count); // secret-scan: allow
            Assert.That(count, Is.EqualTo(1));
            Assert.That(result, Is.EqualTo("[REDACTED]"));
        }

        [Test]
        public void AtomicJsonRoundTripAndEnvUpdateWork()
        {
            var directory = Path.Combine(Path.GetTempPath(), "zbs-store-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var jsonPath = Path.Combine(directory, "models.json");
            var envPath = Path.Combine(directory, ".env");
            try
            {
                SettingsStore.SaveJsonAtomic(jsonPath, new JObject { ["value"] = 1.25m });
                Assert.That(SettingsStore.LoadJObject(jsonPath).Value<decimal>("value"), Is.EqualTo(1.25m));

                File.WriteAllText(envPath, "# keep\nOTHER=value\nTOKEN=old\n");
                SettingsStore.WriteEnvValue(envPath, "TOKEN", "new");
                SettingsStore.WriteEnvValue(envPath, "ADDED", "yes");
                Assert.Multiple(() =>
                {
                    Assert.That(SettingsStore.ReadEnvValue(envPath, "TOKEN"), Is.EqualTo("new"));
                    Assert.That(SettingsStore.ReadEnvValue(envPath, "ADDED"), Is.EqualTo("yes"));
                    Assert.That(SettingsStore.ReadEnvValue(envPath, "MISSING"), Is.Empty);
                });
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void TemporaryPathsArePrivateAndUnique()
        {
            var first = TemporaryFileStore.CreatePath("capture", ".png");
            var second = TemporaryFileStore.CreatePath("capture", "png");
            Assert.Multiple(() =>
            {
                Assert.That(first, Is.Not.EqualTo(second));
                Assert.That(Path.GetFileName(first), Does.StartWith("capture_"));
                Assert.That(Path.GetExtension(first), Is.EqualTo(".png"));
                Assert.That(first, Does.Contain(Path.Combine("MakeItZBS", "Temp")));
            });
        }

        [Test]
        public void ExcelSelectionSerializesAsStableTsv()
        {
            var values = new object[,]
            {
                { "Регион", "Маржа", "Примечание" },
                { "Запад", -21550d, "строка\nс табом\t" },
                { "Восток", null, 39d }
            };

            var result = WindowInfo.SerializeExcelRange(values);

            Assert.That(result, Is.EqualTo(
                "Регион\tМаржа\tПримечание" + Environment.NewLine
                + "Запад\t-21550\tстрока с табом " + Environment.NewLine
                + "Восток\t\t39"));
            Assert.That(WindowInfo.SerializeExcelRange("одна ячейка"),
                Is.EqualTo("одна ячейка"));
        }

        [Test]
        public void ExcelSelectionRejectsUnexpectedOrHugeArrays()
        {
            Assert.Throws<InvalidOperationException>(() =>
                WindowInfo.SerializeExcelRange(new object[2, 2, 2]));
            Assert.Throws<InvalidOperationException>(() =>
                WindowInfo.SerializeExcelRange(new object[101, 100]));
        }

        [Test]
        public void ExcelSelectionValidatesForegroundAndReadsRange()
        {
            var windowInfo = WindowInfo.Constructor();
            var application = new FakeExcelApplication
            {
                Hwnd = 1234,
                Selection = new FakeExcelSelection
                {
                    Value2 = new object[,] { { "Регион", "Маржа" }, { "Запад", -5d } }
                }
            };

            var result = windowInfo.CaptureExcelSelectionFromApplication(
                application, new IntPtr(1234), 77, handle => 77, () => new IntPtr(1234));

            Assert.That(result, Is.EqualTo(
                "Регион\tМаржа" + Environment.NewLine + "Запад\t-5"));
            Assert.That(windowInfo.GetLastCaptureDiagnostic(), Is.Empty);

            Assert.That(windowInfo.CaptureExcelSelectionFromApplication(
                application, new IntPtr(1234), 77, handle => 78, () => new IntPtr(1234)),
                Is.Empty);
            Assert.That(windowInfo.GetLastCaptureDiagnostic(), Does.Contain("PID/HWND"));

            Assert.That(windowInfo.CaptureExcelSelectionFromApplication(
                application, new IntPtr(1234), 77, handle => 77, () => new IntPtr(4321)),
                Is.Empty);
            Assert.That(windowInfo.GetLastCaptureDiagnostic(), Does.Contain("PID/HWND"));
        }

        [Test]
        public void ExcelSelectionReportsMissingEmptyAndInvalidComValues()
        {
            var windowInfo = WindowInfo.Constructor();
            var foreground = new IntPtr(1234);
            Func<IntPtr, uint> processId = handle => 77;
            Func<IntPtr> foregroundWindow = () => foreground;

            Assert.That(windowInfo.CaptureExcelSelectionFromApplication(
                new FakeExcelApplication { Hwnd = 1234 }, foreground, 77,
                processId, foregroundWindow), Is.Empty);
            Assert.That(windowInfo.GetLastCaptureDiagnostic(), Does.Contain("не найден"));

            Assert.That(windowInfo.CaptureExcelSelectionFromApplication(
                new FakeExcelApplication
                {
                    Hwnd = 1234,
                    Selection = new FakeExcelSelection { Value2 = null }
                }, foreground, 77, processId, foregroundWindow), Is.Empty);
            Assert.That(windowInfo.GetLastCaptureDiagnostic(), Does.Contain("пуст"));

            Assert.That(windowInfo.CaptureExcelSelectionFromApplication(
                new BrokenExcelApplication(), foreground, 77,
                processId, foregroundWindow), Is.Empty);
            Assert.That(windowInfo.GetLastCaptureDiagnostic(), Does.Contain("Excel COM"));
        }

        [Test]
        public void WebViewDataUsesPerUserWritableDirectory()
        {
            var directory = ProjectPaths.WebViewUserDataDir;

            Assert.Multiple(() =>
            {
                Assert.That(Path.IsPathRooted(directory), Is.True);
                Assert.That(directory,
                    Does.EndWith(Path.Combine("MakeItZBS", "WebView2")));
                Assert.That(Directory.Exists(directory), Is.True);
                Assert.That(() => ProjectPaths.BuildWebViewUserDataDir(null),
                    Throws.TypeOf<InvalidOperationException>());
            });
        }

        [Test]
        public void WebViewDataFallsBackWhenProfileDirectoryIsNotWritable()
        {
            var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var blockedRoot = Path.Combine(root, "blocked");
            var fallbackRoot = Path.Combine(root, "fallback");
            Directory.CreateDirectory(root);
            File.WriteAllText(blockedRoot, "file blocks directory creation");

            try
            {
                var resolved = ProjectPaths.ResolveWebViewUserDataDir(
                    blockedRoot, fallbackRoot);

                Assert.Multiple(() =>
                {
                    Assert.That(resolved, Is.EqualTo(Path.Combine(
                        fallbackRoot, "MakeItZBS", "WebView2")));
                    Assert.That(Directory.Exists(resolved), Is.True);
                });
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void WebMessagesSupportObjectAndJsonStringPayloads()
        {
            var direct = WebMessageParser.ReadObject("{\"cmd\":\"save\",\"payload\":{}}");
            var encoded = WebMessageParser.ReadObject("\"{\\\"cmd\\\":\\\"copy\\\"}\"");

            Assert.Multiple(() =>
            {
                Assert.That(WebMessageParser.ReadCommand(direct), Is.EqualTo("save"));
                Assert.That(WebMessageParser.ReadCommand(encoded), Is.EqualTo("copy"));
                Assert.That(WebMessageParser.ReadCommand("\"\""), Is.Empty);
                Assert.That(WebMessageParser.ReadCommand("[]"), Is.Empty);
            });
        }

        [Test]
        public void ProjectPathsResolveRepositoryAssetsFromTestOutput()
        {
            Assert.Multiple(() =>
            {
                Assert.That(ProjectPaths.ConfigDir, Is.Not.Null);
                Assert.That(File.Exists(Path.Combine(ProjectPaths.ConfigDir, "settings.json")),
                    Is.True);
                Assert.That(ProjectPaths.UiDir, Is.Not.Null);
                Assert.That(File.Exists(Path.Combine(ProjectPaths.UiDir, "settings.html")),
                    Is.True);
                Assert.That(ProjectPaths.LauncherPath, Is.Not.Null);
                Assert.That(File.Exists(ProjectPaths.LauncherPath), Is.True);
            });
        }

        [Test]
        public void ImageHelpersRoundTripAndResizePng()
        {
            var directory = Path.Combine(
                Path.GetTempPath(), "zbs-image-test-" + Guid.NewGuid().ToString("N"));
            var sourcePath = Path.Combine(directory, "source.png");
            var decodedPath = Path.Combine(directory, "decoded.png");
            var resizedPath = Path.Combine(directory, "resized.png");
            try
            {
                using (var source = new Bitmap(8, 4))
                {
                    source.SetPixel(0, 0, Color.Crimson);
                    ImageHelper.SaveImage(source, sourcePath, ImageFormat.Png);
                }

                var info = ImageHelper.GetImageInfo(sourcePath);
                Assert.Multiple(() =>
                {
                    Assert.That(info.Width, Is.EqualTo(8));
                    Assert.That(info.Height, Is.EqualTo(4));
                    Assert.That(info.FileSizeBytes, Is.GreaterThan(0));
                });

                var encoded = ImageHelper.ImageToBase64(sourcePath);
                ImageHelper.Base64ToImage(encoded, decodedPath);
                Assert.That(ImageHelper.GetImageInfo(decodedPath).Width, Is.EqualTo(8));

                using (var source = Image.FromFile(sourcePath))
                using (var resized = ImageHelper.ResizeImage(source, 4, 4))
                {
                    Assert.That(resized.Size, Is.EqualTo(new Size(4, 2)));
                }

                ImageHelper.ResizeImageFile(sourcePath, resizedPath, 2, 2, false);
                Assert.That(ImageHelper.GetImageInfo(resizedPath).Height, Is.EqualTo(2));
                Assert.That(ImageHelper.GetImageFormatFromExtension("result.unknown"),
                    Is.EqualTo(ImageFormat.Png));
            }
            finally
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, true);
                }
            }
        }

        [TestCase("Ctrl+F12", "Ctrl+F12")]
        [TestCase("Alt+Shift+A", "Alt+Shift+A")]
        [TestCase("Win+1", "Win+1")]
        public void HotkeyParserRoundTrips(string source, string expected)
        {
            var parsed = KeyConverter.Parse(source);
            Assert.That(KeyConverter.GetKeyDescription(parsed.modifiers, parsed.vkCode), Is.EqualTo(expected));
        }

        [TestCase("")]
        [TestCase("Ctrl")]
        [TestCase("A+Ctrl")]
        [TestCase("Ctrl+F25")]
        public void HotkeyParserRejectsInvalidInput(string source)
        {
            Assert.That(() => KeyConverter.Parse(source), Throws.TypeOf<ArgumentException>());
        }

        [Test]
        public void SettingsDirtyFlagIsConsumedOnce()
        {
            SettingsState.ConsumeDirty();
            SettingsState.MarkDirty();
            Assert.That(SettingsState.ConsumeDirty(), Is.True);
            Assert.That(SettingsState.ConsumeDirty(), Is.False);
        }

        [Test]
        public void SingleInstanceKeepsRejectedMutexOwnershipState()
        {
            bool created;
            using (var externalOwner = new Mutex(
                true, @"Local\MakeItZBS-SingleInstance", out created))
            {
                var guard = SingleInstance.Constructor();
                Assert.That(guard.TryAcquire(), Is.False);
                Assert.That(guard.TryAcquire(), Is.False,
                    "Повторный вызов не должен превращать отказ в успешный захват");

                if (created)
                {
                    externalOwner.ReleaseMutex();
                }
            }
        }

        private sealed class FakeExcelApplication
        {
            public long Hwnd { get; set; }
            public FakeExcelSelection Selection { get; set; }
        }

        private sealed class FakeExcelSelection
        {
            public object Value2 { get; set; }
        }

        private sealed class BrokenExcelApplication
        {
            public long Hwnd
            {
                get { throw new InvalidOperationException("broken Excel automation object"); }
            }
        }
    }
}
