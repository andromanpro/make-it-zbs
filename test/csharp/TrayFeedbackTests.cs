using System;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture, NonParallelizable]
    public class TrayFeedbackTests
    {
        [TestCase(null, "both", true, true)]
        [TestCase("garbage", "both", true, true)]
        [TestCase("both", "both", true, true)]
        [TestCase(" ICON ", "icon", true, false)]
        [TestCase("popup", "popup", false, true)]
        [TestCase("none", "none", false, false)]
        public void ModesNormalizeAndRoundTripWithoutLosingOtherSettings(string input, string mode, bool icon, bool popup)
        {
            var settings = JObject.Parse("{'general':{'hotkey':'Alt+F10','custom':'keep'}}");
            SettingsWebForm.ApplyGeneral(settings, new JObject { [OperationFeedbackOptions.Key] = input }, "Alt+F10");
            SettingsSchema.MigrateAndNormalize(settings);
            var payload = SettingsWebForm.BuildGeneralPayload(settings);
            var options = OperationFeedbackOptions.FromSettings(settings);
            Assert.That((string)payload[OperationFeedbackOptions.Key], Is.EqualTo(mode));
            Assert.That(options.ShowIcon, Is.EqualTo(icon));
            Assert.That(options.ShowPopup, Is.EqualTo(popup));
            Assert.That((string)settings["general"]["custom"], Is.EqualTo("keep"));
        }

        [TestCase(-1, "00:00")]
        [TestCase(0, "00:00")]
        [TestCase(65, "01:05")]
        [TestCase(3599, "59:59")]
        [TestCase(3600, "1:00:00")]
        [TestCase(3723, "1:02:03")]
        public void ElapsedTimeHasStableReadableFormatting(int seconds, string expected)
        {
            Assert.That(OperationFeedbackOptions.FormatElapsed(TimeSpan.FromSeconds(seconds)), Is.EqualTo(expected));
        }

        [Test, Apartment(ApartmentState.STA)]
        public void PopupClockAdvancesAcrossStagesAndHiddenPopupCannotResurrectItself()
        {
            var elapsed = TimeSpan.FromSeconds(2);
            Assert.That(Operations.Begin(), Is.True);
            try
            {
                using (var form = new OperationStatusForm(() => elapsed))
                {
                    form.ShowBusy("Capturing");
                    Assert.That(form.ElapsedText, Is.EqualTo("00:02"));
                    elapsed = TimeSpan.FromSeconds(65);
                    form.ShowBusy("Request sent");
                    Assert.That(form.ElapsedText, Is.EqualTo("01:05"));
                    elapsed = TimeSpan.FromSeconds(3723);
                    form.RefreshStatus();
                    Assert.That(form.ElapsedText, Is.EqualTo("1:02:03"));
                    form.HideStatus();
                    Operations.End();
                    Application.DoEvents();
                    Assert.That(form.Visible, Is.False);
                    form.ShowReady("Done");
                    Assert.That(form.ElapsedText, Is.EqualTo("1:02:03"));
                }
            }
            finally { Operations.End(); }
        }

        [TestCase("both", true, true)]
        [TestCase("icon", true, false)]
        [TestCase("popup", false, true)]
        [TestCase("none", false, false)]
        [Apartment(ApartmentState.STA)]
        public void TrayUsesChosenSignalAndRestoresItsIconEvenWithoutExplicitReady(string mode, bool icon, bool popup)
        {
            Assert.That(Operations.Begin(), Is.True);
            try
            {
                using (var tray = new TrayApplicationContext(null, null, false, () => new OperationFeedbackOptions(mode)))
                {
                    tray.ShowBusy("Request sent");
                    Assert.That(tray.BusyIconVisible, Is.EqualTo(icon));
                    Assert.That(tray.PopupVisible, Is.EqualTo(popup));
                    Assert.That(tray.FeedbackText, Does.Contain("00:00"));
                    mode = "icon";
                    SettingsState.MarkDirty();
                    tray.RefreshFeedback();
                    Assert.That(tray.BusyIconVisible, Is.True);
                    Assert.That(tray.PopupVisible, Is.False);
                    Operations.Cancel();
                    tray.RefreshFeedback();
                    Assert.That(tray.FeedbackText, Does.Contain("00:00"));
                    Operations.End();
                    tray.RefreshFeedback();
                    Assert.That(tray.BusyIconVisible, Is.False);
                    Assert.That(tray.FeedbackText, Is.EqualTo("Make it ZBS"));
                    Assert.That(tray.PopupVisible, Is.False);
                }
            }
            finally { Operations.End(); SettingsState.ConsumeDirty(); }
        }
    }
}
