using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace ZBSWindowsComponent
{
    internal sealed class ReplacementLease
    {
        private ReplacementSnapshot _snapshot;

        public string Capture(ReplacementSnapshot snapshot, string original)
        {
            Release();
            if (snapshot == null || (snapshot.Mode != "selection" && snapshot.Mode != "fullModule")) return string.Empty;
            var source = snapshot.Mode == "fullModule" ? snapshot.Document : snapshot.Selection;
            if (source != ReplacementSnapshot.Fingerprint(original)) return string.Empty;
            _snapshot = snapshot;
            return snapshot.Token;
        }

        public ReplacementSnapshot Take(string token)
        {
            var snapshot = Interlocked.Exchange(ref _snapshot, null);
            return snapshot != null && string.Equals(snapshot.Token, token, StringComparison.Ordinal) ? snapshot : null;
        }

        public void Release() => Interlocked.Exchange(ref _snapshot, null);
    }

    internal sealed class ReplacementSnapshot
    {
        public int Handle { get; set; }
        public int ProcessId { get; set; }
        public string Identity { get; set; }
        public string Document { get; set; }
        public string Selection { get; set; }
        public int SelectionStart { get; set; }
        public string Mode { get; set; }
        public VsCodeCapture Editor { get; set; }
        public string Token { get; } = Guid.NewGuid().ToString("N");

        public bool Matches(ReplacementSnapshot current) => current != null
            && Handle == current.Handle && ProcessId == current.ProcessId
            && string.Equals(Identity, current.Identity, StringComparison.Ordinal)
            && string.Equals(Document, current.Document, StringComparison.Ordinal)
            && (Mode == "fullModule" || (SelectionStart == current.SelectionStart
                && string.Equals(Selection, current.Selection, StringComparison.Ordinal)));

        public static string Normalize(string value) =>
            (value ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n');

        public static string Fingerprint(string value)
        {
            using (var hash = SHA256.Create())
            {
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(Normalize(value))));
            }
        }
    }
}
