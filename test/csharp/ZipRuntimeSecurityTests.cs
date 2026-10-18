using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using NUnit.Framework;
using ScriptEngine.HostedScript.Library.Zip;
using ScriptEngine.Machine;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class ZipRuntimeSecurityTests
    {
        private string _root;
        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "zbs-zip-security-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }
        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        [Test]
        public void HostedScriptReferencesFixedZipAndStillExtractsNormalArchives()
        {
            var names = typeof(ZipReader).Assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
            Assert.That(names, Does.Contain("ProDotNetZip"));
            Assert.That(names, Does.Not.Contain("DotNetZip"));
            var zip = CreateZip("folder/test.txt");
            var output = Path.Combine(_root, "output");
            using (var reader = new ZipReader(ValueFactory.Create(zip))) reader.ExtractAll(output);
            Assert.That(File.ReadAllText(Path.Combine(output, "folder", "test.txt")), Is.EqualTo("fixture"));
        }

        [TestCase("../outside.txt")]
        [TestCase("../output-sibling/outside.txt")]
        [TestCase("../../outside.txt")]
        [TestCase("absolute")]
        public void ArchiveCannotWriteOutsideSelectedDirectory(string entry)
        {
            var output = Path.Combine(_root, "output");
            var sentinel = Path.Combine(_root, "outside.txt");
            File.WriteAllText(sentinel, "unchanged");
            if (entry == "absolute") entry = sentinel.Replace('\\', '/');
            var zip = CreateZip(entry);
            using (var reader = new ZipReader(ValueFactory.Create(zip)))
                Assert.Throws<IOException>(() => reader.ExtractAll(output));
            Assert.That(File.ReadAllText(sentinel), Is.EqualTo("unchanged"));
            Assert.That(Directory.Exists(Path.Combine(_root, "output-sibling")), Is.False);
        }

        private string CreateZip(string entryName)
        {
            var path = Path.Combine(_root, "fixture.zip");
            using (var file = File.Create(path))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(archive.CreateEntry(entryName).Open())) writer.Write("fixture");
            return path;
        }
    }
}
