using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture, NonParallelizable]
    public class SettingsSaveWorkflowTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void RegistryFailureStillPersistsConfigurationAndAppliesLiveReload(bool enabled)
        {
            var directory = Path.Combine(Path.GetTempPath(), "zbs-save-workflow-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            SettingsState.ConsumeDirty();
            try
            {
                var order = new List<string>();
                var denied = new UnauthorizedAccessException("Controlled registry denial");
                var warning = SettingsSaveWorkflow.Commit(
                    () => { ConfigurationStore.Save(directory, new Dictionary<string, JObject>
                        { ["settings.json"] = JObject.Parse("{'general':{'hotkey':'Alt+F11','operationFeedback':'icon'}}") }); order.Add("saved"); },
                    () => { order.Add("startup=" + enabled); throw denied; },
                    () => { SettingsState.MarkDirty(); order.Add("applied"); });
                Assert.That(warning, Is.SameAs(denied));
                Assert.That(order, Is.EqualTo(new[] { "saved", "startup=" + enabled, "applied" }));
                var settings = JObject.Parse(File.ReadAllText(Path.Combine(directory, "settings.json")));
                Assert.That((string)settings["general"]["hotkey"], Is.EqualTo("Alt+F11"));
                Assert.That((string)settings["general"]["operationFeedback"], Is.EqualTo("icon"));
                Assert.That(SettingsState.ConsumeDirty(), Is.True);
            }
            finally { SettingsState.ConsumeDirty(); Directory.Delete(directory, true); }
        }

        [Test]
        public void FailedConfigurationCommitDoesNotWriteRegistryOrReportApplied()
        {
            var touched = false;
            Assert.Throws<IOException>(() => SettingsSaveWorkflow.Commit(
                () => throw new IOException("Controlled file failure"), () => touched = true, () => touched = true));
            Assert.That(touched, Is.False);
        }

        [Test]
        public void SuccessfulStartupReturnsNoWarningAndRunsApplyOnce()
        {
            var count = 0;
            var warning = SettingsSaveWorkflow.Commit(() => count++, () => count++, () => count++);
            Assert.That(warning, Is.Null);
            Assert.That(count, Is.EqualTo(3));
        }
    }
}
