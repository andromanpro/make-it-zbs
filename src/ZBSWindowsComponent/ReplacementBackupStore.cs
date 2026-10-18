using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ScriptEngine.Machine.Contexts;

namespace ZBSWindowsComponent
{
    [ContextClass("ХранилищеРезервныхКопий", "ReplacementBackupStore")]
    public sealed class ReplacementBackupStore : AutoContext<ReplacementBackupStore>
    {
        internal const int MaximumTextBytes = 8 * 1024 * 1024;
        private const int MaximumEncryptedBytes = MaximumTextBytes + 4096;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly byte[] Header = Encoding.ASCII.GetBytes("ZBSBACKUP1\n");
        private static readonly byte[] Purpose = Encoding.ASCII.GetBytes("MakeItZBS.ReplacementBackup.v1");

        [ScriptConstructor]
        public static ReplacementBackupStore Constructor() => new ReplacementBackupStore();

        [ContextMethod("Сохранить", "Save")]
        public string Save(string directory, string original, string context)
        {
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (string.IsNullOrEmpty(context) || context.Length > 32
                || !Regex.IsMatch(context, @"\A[A-Za-z][A-Za-z0-9_]{0,31}\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                throw new ArgumentException("Invalid replacement context", nameof(context));
            if (original.Length > MaximumTextBytes || Utf8.GetByteCount(original) > MaximumTextBytes)
                throw new ArgumentException("Replacement backup is too large", nameof(original));
            var root = Path.GetFullPath(directory);
            byte[] plain = Utf8.GetBytes(original);
            try
            {
                // Encrypt before touching disk; a temporary file contains ciphertext only.
                var encrypted = ProtectedData.Protect(plain, Purpose, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, "backup_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture)
                    + "_" + context + "_" + Guid.NewGuid().ToString("N") + ".zbsbackup");
                var temporary = path + ".tmp";
                var ownsTemporary = false;
                try
                {
                    using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        ownsTemporary = true;
                        stream.Write(Header, 0, Header.Length);
                        stream.Write(encrypted, 0, encrypted.Length);
                        stream.Flush(true);
                    }
                    File.Move(temporary, path);
                    return path;
                }
                finally
                {
                    if (ownsTemporary && File.Exists(temporary)) File.Delete(temporary);
                }
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        [ContextMethod("Прочитать", "Read")]
        public string Read(string path)
        {
            var plain = ReadProtected(path);
            try { return Utf8.GetString(plain); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        // Plaintext is written only on an explicit recovery request, never over an existing file.
        [ContextMethod("ВосстановитьВФайл", "RestoreToFile")]
        public void RestoreToFile(string path, string destination)
        {
            var plain = ReadProtected(path);
            try
            {
                using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(plain, 0, plain.Length);
                    stream.Flush(true);
                }
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }

        private static byte[] ReadProtected(string path)
        {
            byte[] encrypted;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new BinaryReader(stream))
            {
                if (stream.Length <= Header.Length || stream.Length > Header.Length + MaximumEncryptedBytes
                    || !reader.ReadBytes(Header.Length).SequenceEqual(Header))
                    throw new InvalidDataException("Unsupported replacement backup format or size");
                var size = checked((int)(stream.Length - Header.Length));
                encrypted = reader.ReadBytes(size);
                if (encrypted.Length != size) throw new EndOfStreamException("Incomplete replacement backup");
            }
            var plain = ProtectedData.Unprotect(encrypted, Purpose, DataProtectionScope.CurrentUser);
            if (plain.Length <= MaximumTextBytes) return plain;
            Array.Clear(plain, 0, plain.Length);
            throw new InvalidDataException("Replacement backup is too large");
        }
    }
}
