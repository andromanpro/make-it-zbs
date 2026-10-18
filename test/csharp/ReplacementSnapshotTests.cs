using NUnit.Framework;
using ScriptEngine.Machine;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class ReplacementSnapshotTests
    {
        private static ReplacementSnapshot Source(string mode = "selection") => new ReplacementSnapshot
        {
            Handle = 100, ProcessId = 20, Identity = "started:document:control",
            Document = ReplacementSnapshot.Fingerprint("first\nfirst"),
            Selection = ReplacementSnapshot.Fingerprint("first"), SelectionStart = 0, Mode = mode
        };

        [Test]
        public void InterruptedStaReplacementReturnsFailureInsteadOfNullReference()
        {
            var window = WindowInfo.Constructor();
            var result = (IRuntimeContextInstance)window.CreatePasteResult(null);
            Assert.That(result.GetPropValue(result.FindProperty("Успех")).AsBoolean(), Is.False);
            Assert.That(result.GetPropValue(result.FindProperty("Диагностика")).AsString(), Does.Contain("без результата"));
            Assert.That(window.GetLastCaptureDiagnostic(), Does.Contain("без результата"));
        }

        [Test]
        public void SameWindowIsInsufficientWhenDocumentOrSelectionChanges()
        {
            var source = Source();
            Assert.That(source.Matches(Source()), Is.True);
            Assert.That(source.Matches(null), Is.False);
            var current = Source();
            current.Document = ReplacementSnapshot.Fingerprint("first\nmodified");
            Assert.That(source.Matches(current), Is.False);
            current = Source();
            current.SelectionStart = 6;
            Assert.That(source.Matches(current), Is.False, "Identical text in another range is not the captured selection.");
            current = Source();
            current.Selection = ReplacementSnapshot.Fingerprint("other");
            Assert.That(source.Matches(current), Is.False);
            current = Source();
            current.Identity = "other-tab-with-identical-text";
            Assert.That(source.Matches(current), Is.False);
            current = Source();
            current.ProcessId++;
            Assert.That(source.Matches(current), Is.False);
            current = Source();
            current.Handle++;
            Assert.That(source.Matches(current), Is.False);
        }

        [Test]
        public void FullDocumentMayChangeSelectionButNotContentAfterSelectAll()
        {
            var source = Source("fullModule");
            var current = Source("fullModule");
            current.SelectionStart = 6;
            current.Selection = current.Document;
            Assert.That(source.Matches(current), Is.True);
            current.Document = "changed";
            Assert.That(source.Matches(current), Is.False);
        }

        [Test]
        public void FingerprintPreservesContentButNormalizesWindowsLineEndings()
        {
            Assert.That(ReplacementSnapshot.Fingerprint("a\r\nb\r"), Is.EqualTo(ReplacementSnapshot.Fingerprint("a\nb\n")));
            Assert.That(ReplacementSnapshot.Fingerprint("a "), Is.Not.EqualTo(ReplacementSnapshot.Fingerprint("a")));
            Assert.That(ReplacementSnapshot.Fingerprint(null), Is.EqualTo(ReplacementSnapshot.Fingerprint("")));
            Assert.That(Source().Token, Is.Not.EqualTo(Source().Token));
        }

        [TestCase("selection", "first")]
        [TestCase("fullModule", "first\nfirst")]
        public void CapturedLeaseCanBeConsumedOnlyOnce(string mode, string original)
        {
            var lease = new ReplacementLease();
            var snapshot = Source(mode);
            var token = lease.Capture(snapshot, original);
            Assert.That(token, Is.EqualTo(snapshot.Token));
            Assert.That(lease.Take(token), Is.SameAs(snapshot));
            Assert.That(lease.Take(token), Is.Null);
        }

        [Test]
        public void MismatchedSourceAndUnknownModeNeverAuthorizeReplacement()
        {
            var lease = new ReplacementLease();
            Assert.That(lease.Capture(Source(), "other"), Is.Empty);
            Assert.That(lease.Capture(Source("chat"), "first"), Is.Empty);
            Assert.That(lease.Capture(null, "first"), Is.Empty);
            Assert.That(lease.Take("forged"), Is.Null);
        }

        [Test]
        public void CancellationFailedRecaptureAndWrongTokenInvalidateOldLease()
        {
            var lease = new ReplacementLease();
            var token = lease.Capture(Source(), "first");
            lease.Release();
            Assert.That(lease.Take(token), Is.Null);
            token = lease.Capture(Source(), "first");
            lease.Capture(null, "first");
            Assert.That(lease.Take(token), Is.Null);
            token = lease.Capture(Source(), "first");
            Assert.That(lease.Take("another-operation"), Is.Null);
            Assert.That(lease.Take(token), Is.Null);
        }
    }
}
