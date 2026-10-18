using System;
using System.Threading;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
    public class ResultFallbackTests
    {
        [TestCase("")]
        [TestCase("synthetic result\nwith tabs\tand unicode: текст")]
        [TestCase("CRLF\r\nCR\rLF\ntrailing spaces  ")]
        public void NativeFallbackKeepsExactReadOnlyResultWithoutWebView(string text)
        {
            using (var form = new ResultFallbackForm(text, "synthetic warning", null))
            {
                Assert.That(form.ResultText, Is.EqualTo(text));
                Assert.That(form.IsReadOnly, Is.True);
                Assert.That(form.CanCopy, Is.True);
                Assert.That(form.ShowInTaskbar, Is.True);
                if (text.Length == 0) Assert.That(form.CopyResult(), Is.False);
            }
        }

        [Test]
        public void CancelledFallbackCannotCopyALateResult()
        {
            using (var operation = new WorkOperation())
            using (var form = new ResultFallbackForm("late result", "", operation))
            {
                operation.Cancel();
                Assert.That(form.CanCopy, Is.False);
                Assert.That(form.CopyResult(), Is.False);
            }
        }
    }
}
