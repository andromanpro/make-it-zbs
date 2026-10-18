using System;
using System.IO;
using System.Linq;

namespace ZBSWindowsComponent
{
    internal static class TemporaryFileStore
    {
        private const string LeaseSuffix = ".lease";
        internal static readonly string DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MakeItZBS",
            "Temp");

        public static string CreatePath(string prefix, string extension)
        {
            Directory.CreateDirectory(DirectoryPath);
            string fileName = string.Format(
                "{0}_{1:yyyyMMdd_HHmmssfff}_{2}.{3}",
                prefix,
                DateTime.UtcNow,
                Guid.NewGuid().ToString("N"),
                extension.TrimStart('.'));
            var path = Path.GetFullPath(Path.Combine(DirectoryPath, fileName));
            if (!string.Equals(Path.GetDirectoryName(path), DirectoryPath, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Temporary file must stay inside the application temp directory.");
            var operation = Operations.Current;
            if (operation != null)
            {
                // The exclusive lease also protects this operation from cleanup in another process.
                var lease = new FileStream(path + LeaseSuffix, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    operation.OwnCleanup(() =>
                    {
                        try { File.Delete(path); }
                        finally { lease.Dispose(); File.Delete(path + LeaseSuffix); }
                    });
                }
                catch { lease.Dispose(); File.Delete(path + LeaseSuffix); throw; }
            }
            return path;
        }

        internal static int Cleanup(int maximumFiles) => Cleanup(DirectoryPath, maximumFiles);

        internal static int Cleanup(string directory, int maximumFiles)
        {
            if (maximumFiles < 0) throw new ArgumentOutOfRangeException(nameof(maximumFiles));
            if (!Directory.Exists(directory)) return 0;
            var removed = 0;
            foreach (var prefix in new[] { "screenshot", "clipboard_image", "capture-worker" })
            {
                var files = new DirectoryInfo(directory).GetFiles(prefix + "_*.png")
                    .Where(file => (file.Attributes & FileAttributes.ReparsePoint) == 0)
                    .OrderBy(file => file.LastWriteTimeUtc).ThenBy(file => file.Name, StringComparer.Ordinal).ToArray();
                var excess = files.Length - maximumFiles;
                foreach (var file in files)
                {
                    if (excess <= 0) break;
                    if (!TryDelete(file.FullName)) continue;
                    excess--;
                    removed++;
                }
                foreach (var orphan in Directory.GetFiles(directory, prefix + "_*.png" + LeaseSuffix)
                    .Select(lease => lease.Substring(0, lease.Length - LeaseSuffix.Length))
                    .Where(path => !File.Exists(path)))
                    TryDelete(orphan);
            }
            return removed;
        }

        private static bool TryDelete(string path)
        {
            var leasePath = path + LeaseSuffix;
            try
            {
                if (File.Exists(leasePath) && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0)
                    return false;
                using (new FileStream(leasePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                    File.Delete(path);
                File.Delete(leasePath);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }
}
