using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class OperationFeedbackTests
    {
        [Test]
        public void DiagnosticSettingIsOptInAndCannotEnableContentLogging()
        {
            Assert.That(SettingsWebForm.BuildLoggingPayload(new JObject()).Value<bool>("enabled"), Is.False);
            var settings = JObject.Parse("{'logging':{'enabled':false,'maxLogFiles':17,'logRequests':true,'logResponses':true}}");
            SettingsWebForm.ApplyLogging(settings, null);
            Assert.That(settings["logging"].Value<bool>("logRequests"), Is.True);
            SettingsWebForm.ApplyLogging(settings, new JObject { ["enabled"] = true });
            Assert.Multiple(() =>
            {
                Assert.That(SettingsWebForm.BuildLoggingPayload(settings).Value<bool>("enabled"), Is.True);
                Assert.That(settings["logging"].Value<bool>("diagnosticsOnly"), Is.True);
                Assert.That(settings["logging"].Value<bool>("logRequests"), Is.False);
                Assert.That(settings["logging"].Value<bool>("logResponses"), Is.False);
                Assert.That(settings["logging"].Value<int>("maxLogFiles"), Is.EqualTo(17));
            });
            SettingsWebForm.ApplyLogging(settings, new JObject { ["enabled"] = false });
            Assert.That(SettingsWebForm.BuildLoggingPayload(settings).Value<bool>("enabled"), Is.False);
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void StatusWindowDoesNotActivateOrCreateATaskbarButtonAndTracksCompletion()
        {
            using (var form = new OperationStatusForm())
            {
                var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var parameters = (CreateParams)typeof(Control).GetProperty("CreateParams", flags).GetValue(form);
                Assert.That(parameters.ExStyle & 0x08000080, Is.EqualTo(0x08000080));
                Assert.That((bool)typeof(Form).GetProperty("ShowWithoutActivation", flags).GetValue(form), Is.True);
                Assert.That(form.ShowInTaskbar, Is.False);
                Assert.That(Operations.Begin(), Is.True);
                try
                {
                    form.ShowBusy("Word - capturing input");
                    Assert.That(form.Visible, Is.True);
                    Assert.That(form.Controls["operationMessage"].Text, Is.EqualTo("Word - capturing input"));
                    form.RefreshStatus();
                    Assert.That(form.Visible, Is.True, "A pending operation keeps the status visible");
                }
                finally { Operations.End(); }
                form.RefreshStatus();
                Assert.That(form.Visible, Is.True, "Completion is visible before auto-hide");
                form.RefreshStatus();
                Assert.That(form.Visible, Is.False);
                form.ShowReady("Cancelled");
                Assert.That(form.Controls["operationMessage"].Text, Is.EqualTo("Cancelled"));
                form.RefreshStatus();
                Assert.That(form.Visible, Is.False);
            }
        }
    }
}
