using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class SecretRedactorTests
    {
        [TestCase("password=abc==", "abc")]
        [TestCase("token=abc:def=ghi", "abc")]
        [TestCase("{\"password\": \"correct horse battery staple\", \"user\": \"reader\"}", "correct")]
        [TestCase("password='two secret words'", "secret words")]
        [TestCase("{\"token\":\"escaped\\\"value\"}", "value")]
        [TestCase("DATABASE_PASSWORD=database-credential", "database-credential")]
        [TestCase("OPENROUTER_API_KEY=provider-credential", "provider-credential")]
        [TestCase("glpat-abcdefghijklmnopqrst", "abcdefghijklmnopqrst")]
        public void RedactsTheEntireCredential(string input, string secret)
        {
            var output = SecretRedactor.Redact(input, out var count);

            Assert.Multiple(() =>
            {
                Assert.That(count, Is.GreaterThan(0));
                Assert.That(output, Does.Contain("[REDACTED]"));
                Assert.That(output, Does.Not.Contain(secret));
            });
        }

        [Test]
        public void PreservesLabelsAndUnrelatedFields()
        {
            var output = SecretRedactor.Redact(
                "{\"password\": \"two words\", \"user\": \"reader\"}\ncount=42", out _);

            Assert.That(output, Is.EqualTo(
                "{\"password\": \"[REDACTED]\", \"user\": \"reader\"}\ncount=42"));
        }

        [TestCase("password=abc==")]
        [TestCase("{\"password\":\"two words\"}")]
        public void RedactingAnAlreadyRedactedValueIsIdempotent(string input)
        {
            var once = SecretRedactor.Redact(input, out _);
            Assert.That(SecretRedactor.Redact(once, out var count), Is.EqualTo(once));
            Assert.That(count, Is.Zero);
        }

        [TestCase("The password can be reset in settings. Token count is 42.")]
        [TestCase("{\"max_tokens\": 42, \"tokenizer\": \"default\", \"passwordEnabled\": true}")]
        [TestCase("The key is on the table.")]
        public void PreservesNonSecretText(string input)
        {
            Assert.That(SecretRedactor.Redact(input, out var count), Is.EqualTo(input));
            Assert.That(count, Is.Zero);
        }
    }
}
