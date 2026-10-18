using System.Threading;
using NUnit.Framework;
using ScriptEngine.HostedScript.Library;
using ScriptEngine.Machine;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class ResultIntegrityTests
    {
        [TestCase("Text")]
        [TestCase("Email")]
        [TestCase("Word")]
        public void ReviewReportsChangedAmountsAndNumericDates(string context)
        {
            var review = ResultIntegrity.Compare("Pay 1200.50 by 05.09.2026", "Pay 120.50 by 09.05.2026", context);
            Assert.That(review.RequiresReview, Is.True);
            Assert.That(review.Removed, Is.EqualTo(new[] { "1200.50", "05.09.2026" }));
            Assert.That(review.Added, Is.EqualTo(new[] { "120.50", "09.05.2026" }));
        }

        [TestCase("A 12, B 13", "B 13, A 12", false)]
        [TestCase("Fee 10%", "Fee 10", true)]
        [TestCase("Debt -100", "Debt 100", true)]
        [TestCase("Dates 12/05/2026", "Dates 05/12/2026", true)]
        [TestCase("A 10, B 10", "A 10", true)]
        [TestCase("No amount", "Amount 10", true)]
        [TestCase("123kg", "124kg", true)]
        [TestCase(null, null, false)]
        public void ComparisonPreservesMultiplicitySignsAndSeparators(string before, string after, bool expected)
        {
            Assert.That(ResultIntegrity.Compare(before, after, "Text").RequiresReview, Is.EqualTo(expected));
        }

        [TestCase("Code")]
        [TestCase("OneCCode")]
        [TestCase("AIChat")]
        public void NonProseChangesAreNotRestricted(string context)
        {
            Assert.That(ResultIntegrity.Compare("return 1", "return 2", context).RequiresReview, Is.False);
        }

        [Test]
        public void OversizedInputCannotSilentlyPassReview()
        {
            Assert.That(ResultIntegrity.Compare(new string('a', 1000001), "ok", "Text").Unavailable, Is.True);
            Assert.That(ResultIntegrity.Compare("ok", new string('a', 1000001), "Email").RequiresReview, Is.True);
            Assert.That(ResultIntegrity.Compare(new string('1', 80), "", "Text").Removed[0].Length, Is.EqualTo(67));
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void HostRejectsApplyWithoutApprovalAndAcceptsExplicitReviewOnce()
        {
            using (var preview = new PreviewForm("Pay 100", "Pay 1000", "Email", "fixture", 0, true))
            {
                Assert.That(preview.TryApply(false), Is.False);
                Assert.That(preview.Outcome, Is.EqualTo("cancelled"));
                Assert.That(preview.TryApply(true), Is.True);
                Assert.That(preview.Outcome, Is.EqualTo("applied"));
                Assert.That(preview.TryApply(true), Is.False);
            }
        }

        [Test]
        [Apartment(ApartmentState.STA)]
        public void ApprovalCannotOverrideDisabledReplacementOrEmptyResult()
        {
            using (var disabled = new PreviewForm("100", "200", "Text", "fixture", 0, false))
                Assert.That(disabled.TryApply(true), Is.False);
            using (var empty = new PreviewForm("100", " ", "Text", "fixture", 0, true))
                Assert.That(empty.TryApply(true), Is.False);
            using (var unchanged = new PreviewForm("Due 100", "Pay 100", "Text", "fixture", 0, true))
                Assert.That(unchanged.TryApply(false), Is.True);
            var request = new StructureImpl();
            request.Insert("url", ValueFactory.Create("https://example.test"));
            request.Insert("authHeader", ValueFactory.Create(""));
            request.Insert("bodyJson", ValueFactory.Create("{}"));
            using (var stream = PreviewForm.ДляСтрима(StreamPreviewOptions.FromRuntimeContext(request, "100", "Text", "fixture", 0, true)))
            {
                stream.ReadSseDelta("{\"choices\":[{\"delta\":{\"content\":\"200\"}}]}");
                Assert.That(stream.TryApply(true), Is.False, "An unfinished stream is never applicable.");
                stream.PostStreamDone();
                Assert.That(stream.TryApply(false), Is.False);
                Assert.That(stream.TryApply(true), Is.True);
            }
        }
    }
}
