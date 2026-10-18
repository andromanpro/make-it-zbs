using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Xml;
using Microsoft.Win32;
using NUnit.Framework;
using ZBSWindowsComponent;

namespace MakeItZBS.Tests
{
    [TestFixture]
    public class WindowsAppIdentityTests
    {
        [TestCase("oscript", true)]
        [TestCase("OSCRIPT", true)]
        [TestCase("testhost.net472", false)]
        [TestCase("1cv8", false)]
        [TestCase(null, false)]
        public void OnlyApplicationHostReceivesProcessIdentity(string host, bool expected)
        {
            Assert.That(WindowsAppIdentity.IsApplicationHost(host), Is.EqualTo(expected));
        }

        [Test]
        public void RegistrationWritesBrandAndPersistentIconWithoutChangingOtherValues()
        {
            WithKey(key =>
            {
                key.SetValue("DisplayName", "1Script console host");
                key.SetValue("Unrelated", "keep");
                var path = @"F:\Профиль пользователя\notification.ico";
                WindowsAppIdentity.WriteRegistration(key, path);
                WindowsAppIdentity.WriteRegistration(key, path);
                Assert.That(key.GetValue("DisplayName"), Is.EqualTo("Make it ZBS"));
                Assert.That(key.GetValue("IconUri"), Is.EqualTo(path));
                Assert.That(key.GetValueKind("DisplayName"), Is.EqualTo(RegistryValueKind.ExpandString));
                Assert.That(key.GetValueKind("IconUri"), Is.EqualTo(RegistryValueKind.ExpandString));
                Assert.That(key.GetValue("Unrelated"), Is.EqualTo("keep"));
            });
        }

        [Test]
        public void MissingIconDoesNotPreventBrandRegistrationOrErasePreviousIcon()
        {
            WithKey(key =>
            {
                key.SetValue("IconUri", "previous.ico");
                WindowsAppIdentity.WriteRegistration(key, null);
                Assert.That(key.GetValue("DisplayName"), Is.EqualTo("Make it ZBS"));
                Assert.That(key.GetValue("IconUri"), Is.EqualTo("previous.ico"));
            });
        }

        [Test]
        public void RegistrationFailureStillAttemptsProcessIdentityAndDoesNotExposeExceptionText()
        {
            var applied = false;
            var warnings = new List<string>();
            WindowsAppIdentity.TryApply(() => throw new UnauthorizedAccessException("private data"),
                () => applied = true, warnings.Add);
            Assert.That(applied, Is.True);
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.Contain("Registration failed: UnauthorizedAccessException").And.Not.Contain("private data"));
        }

        [Test]
        public void ProcessIdentityFailureDoesNotUndoRegistrationOrEscapeStartup()
        {
            var registered = false;
            var warnings = new List<string>();
            WindowsAppIdentity.TryApply(() => registered = true,
                () => throw new InvalidOperationException(), warnings.Add);
            Assert.That(registered, Is.True);
            Assert.That(warnings, Has.Count.EqualTo(1));
            Assert.That(warnings[0], Does.StartWith("Process AppUserModelID failed:"));
        }

        [Test]
        public void IconIsValidPersistentIdempotentAndRepairsCorruption()
        {
            var directory = Path.Combine(Path.GetTempPath(), "zbs-identity-" + Guid.NewGuid().ToString("N"));
            try
            {
                var path = WindowsAppIdentity.EnsureIconFile(directory);
                var bytes = File.ReadAllBytes(path);
                using (var icon = new Icon(path)) Assert.That(icon.Width, Is.GreaterThan(0));
                var oldTime = DateTime.UtcNow.AddDays(-1);
                File.SetLastWriteTimeUtc(path, oldTime);
                Assert.That(WindowsAppIdentity.EnsureIconFile(directory), Is.EqualTo(path));
                Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(oldTime).Within(TimeSpan.FromSeconds(1)));
                File.WriteAllText(path, "corrupt");
                WindowsAppIdentity.EnsureIconFile(directory);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes));
                Assert.That(Directory.GetFiles(Path.GetDirectoryName(path), "*.tmp"), Is.Empty);
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [TestCase(null)]
        [TestCase("")]
        public void MissingUserDataDirectoryFailsWithoutWritingRelativeFiles(string directory)
        {
            Assert.Throws<InvalidOperationException>(() => WindowsAppIdentity.EnsureIconFile(directory));
        }

        [Test]
        public void NotificationXmlPreservesMessageWithoutMarkupInjection()
        {
            const string message = "A <tag> & \"quoted\" text";
            var xml = new XmlDocument();
            xml.LoadXml(DesktopNotification.BuildXml(message));
            Assert.That(xml.SelectSingleNode("/toast/visual/binding/@template").Value, Is.EqualTo("ToastGeneric"));
            var texts = xml.SelectNodes("/toast/visual/binding/text");
            Assert.That(texts.Count, Is.EqualTo(2));
            Assert.That(texts[0].InnerText, Is.EqualTo("Make it ZBS"));
            Assert.That(texts[1].InnerText, Is.EqualTo(message));
            Assert.That(xml.SelectNodes("//tag").Count, Is.Zero);
        }

        private static void WithKey(Action<RegistryKey> action)
        {
            var path = @"Software\MakeItZBS.Tests\Identity-" + Guid.NewGuid().ToString("N");
            try { using (var key = Registry.CurrentUser.CreateSubKey(path)) action(key); }
            finally { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
        }

        [Test, Apartment(ApartmentState.STA)]
        public void ShortcutPersistsAppIdAndTargetAndRepairsMovedInstallation()
        {
            var directory = Path.Combine(Path.GetTempPath(), "zbs-shortcut-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "Make it ZBS.lnk");
            var launcher = typeof(WindowsAppIdentityTests).Assembly.Location;
            try
            {
                ApplicationShortcut.Ensure(path, launcher);
                var bytes = File.ReadAllBytes(path);
                var oldTime = DateTime.UtcNow.AddDays(-1);
                File.SetLastWriteTimeUtc(path, oldTime);
                ApplicationShortcut.Ensure(path, launcher);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(bytes), "An unchanged shortcut must not be rewritten");
                Assert.That(File.GetLastWriteTimeUtc(path), Is.EqualTo(oldTime).Within(TimeSpan.FromSeconds(1)));
                var movedLauncher = Path.Combine(directory, "MakeItZBS.exe");
                File.Copy(launcher, movedLauncher);
                ApplicationShortcut.Ensure(path, movedLauncher);
                Assert.That(File.ReadAllBytes(path), Is.Not.EqualTo(bytes));
                var movedBytes = File.ReadAllBytes(path);
                ApplicationShortcut.Ensure(path, movedLauncher);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(movedBytes));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }

        [Test, Apartment(ApartmentState.STA)]
        public void DesktopIdentityDoesNotCreateOrRetargetAnUnrelatedShortcut()
        {
            var directory = Path.Combine(Path.GetTempPath(), "zbs-desktop-link-" + Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "Make it ZBS.lnk");
            var launcher = typeof(WindowsAppIdentityTests).Assembly.Location;
            try
            {
                ApplicationShortcut.Ensure(path, launcher, onlyMatchingTarget: true);
                Assert.That(File.Exists(path), Is.False);
                ApplicationShortcut.Ensure(path, launcher);
                var before = File.ReadAllBytes(path);
                ApplicationShortcut.Ensure(path, Path.Combine(directory, "unrelated.exe"), onlyMatchingTarget: true);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
                ApplicationShortcut.Ensure(path, launcher, onlyMatchingTarget: true);
                Assert.That(File.ReadAllBytes(path), Is.EqualTo(before));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
    }
}
