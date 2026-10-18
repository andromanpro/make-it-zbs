using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using ScriptEngine.Machine;
using ZBSWindowsComponent;

namespace MakeItZBS.UiTests
{
    [TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
    public class WordUiTests
    {
        [Test]
        public void NewWordDocumentCapturesSelectionAndAllowsOnlyVerifiedReplacement()
        {
            var output = Environment.GetEnvironmentVariable("ZBS_UI_ARTIFACT_DIR");
            Assert.That(output, Is.Not.Null.And.Not.Empty);
            var existing = Process.GetProcessesByName("WINWORD").Select(p => { using (p) return p.Id; }).ToArray();
            dynamic application = null, documents = null, document = null, content = null, range = null, documentWindow = null;
            Process owned = null;
            var createdEmptyApplication = false;
            using (var clipboard = new ClipboardTestScope())
            using (var trace = new TextWriterTraceListener(Path.Combine(output, "word-paste-trace.log")))
            {
                Trace.AutoFlush = true;
                Trace.Listeners.Add(trace);
                void Stage(string name) => File.AppendAllText(Path.Combine(output, "word-progress.log"), DateTime.UtcNow.ToString("o") + " " + name + Environment.NewLine);
                try
                {
                    var type = Type.GetTypeFromProgID("Word.Application");
                    Assert.That(type, Is.Not.Null, "Microsoft Word is not installed");
                    application = Activator.CreateInstance(type);
                    documents = application.Documents;
                    Assert.That((int)documents.Count, Is.Zero, "Refusing an application with existing documents");
                    document = documents.Add();
                    documentWindow = application.ActiveWindow;
                    var handle = new IntPtr((int)documentWindow.Hwnd);
                    NativeMethods.GetWindowThreadProcessId(handle, out var pid);
                    if (existing.Contains((int)pid))
                    {
                        document.Close(0);
                        Assert.Fail("Refusing to close or modify an existing Word process");
                    }
                    owned = Process.GetProcessById((int)pid);
                    createdEmptyApplication = true;
                    clipboard.AllowOwner(owned.Id);
                    var clipboardMarker = "ZBS-marker-" + Guid.NewGuid().ToString("N");
                    ClipboardDispatcher.Shared.Run(() => { System.Windows.Forms.Clipboard.SetText(clipboardMarker); return true; });
                    Stage("owned-pid=" + pid);
                    content = document.Content;
                    content.Text = "before selected after";
                    application.Visible = true;
                    application.Activate();
                    documentWindow.Activate();
                    range = document.Range(7, 15);
                    range.Select();
                    application.ScreenRefresh();
                    WaitFor(() => NativeMethods.GetForegroundWindow() == handle, "New Word document did not acquire focus");
                    var window = new WindowInfo();
                    var path = Path.Combine(output, "word-selection.png");
                    new ScreenCapture().CaptureWindow(path, handle.ToInt32(), owned.Id);
                    TestContext.AddTestAttachment(path);
                    Stage("capture-selection");
                    Assert.That(RunClient(() => window.GetSelectedText(handle.ToInt32(), owned.Id)), Is.EqualTo("selected"), window.GetLastCaptureDiagnostic());
                    File.WriteAllText(Path.Combine(output, "word-capture-diagnostic.txt"), window.GetLastCaptureDiagnostic());
                    Assert.That(ClipboardMarkerRestored(clipboardMarker, output), Is.True,
                        "Selection capture must restore the clipboard marker");
                    Stage("capture-replacement");
                    var token = RunClient(() => window.CaptureReplacement(handle.ToInt32(), owned.Id, "selection", "selected"));
                    Assert.That(token, Is.Not.Empty, window.GetLastCaptureDiagnostic());
                    File.WriteAllText(Path.Combine(output, "word-replacement-mode.txt"), "verified replacement");
                    var result = (IRuntimeContextInstance)RunClient(() => window.ReplaceCapturedText(token, "improved"));
                    Assert.That(result.GetPropValue(result.FindProperty("Успех")).AsBoolean(), Is.True, window.GetLastCaptureDiagnostic());
                    Assert.That(ClipboardMarkerRestored(clipboardMarker, output), Is.True,
                        "Replacement must restore the clipboard marker");
                    Stage("replacement-returned");
                    WaitFor(() => ((string)content.Text).TrimEnd('\r', '\n') == "before improved after", "Wrong Word replacement range");
                    document.Undo();
                    WaitFor(() => ((string)content.Text).TrimEnd('\r', '\n') == "before selected after", "Word Undo did not restore the source");
                    Marshal.ReleaseComObject(range);
                    range = document.Range(7, 15);
                    range.Select();
                    token = RunClient(() => window.CaptureReplacement(handle.ToInt32(), owned.Id, "selection", "selected"));
                    Assert.That(token, Is.Not.Empty, window.GetLastCaptureDiagnostic());
                    content.Text = "changed document";
                    result = (IRuntimeContextInstance)RunClient(() => window.ReplaceCapturedText(token, "must-not-appear"));
                    Assert.That(result.GetPropValue(result.FindProperty("Успех")).AsBoolean(), Is.False);
                    Assert.That(((string)content.Text).TrimEnd('\r', '\n'), Is.EqualTo("changed document"));
                    Stage("stale-source-rejected");
                    WordPipeline.RunIfRequested((object)application, (object)document, (object)content, handle, owned.Id, output,
                        clipboardMarker, clipboard.AllowOwner, clipboard.Dispose);
                    path = Path.Combine(output, "word-disposable-document.png");
                    var capture = new ScreenCapture();
                    capture.CaptureWindow(path, handle.ToInt32(), owned.Id);
                    File.WriteAllText(Path.ChangeExtension(path, ".json"), capture.GetLastCaptureMetadata());
                    TestContext.AddTestAttachment(path);
                }
                finally
                {
                    Stage("cleanup");
                    try
                    {
                        clipboard.Dispose();
                        if (createdEmptyApplication && (owned == null || !owned.HasExited)) application.Quit(0);
                    }
                    finally
                    {
                        foreach (object value in new object[] { range, content, documentWindow, document, documents, application })
                            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
                        if (owned != null)
                        {
                            if (!owned.WaitForExit(5000)) { owned.Kill(); owned.WaitForExit(3000); }
                            owned.Dispose();
                        }
                        Trace.Listeners.Remove(trace);
                    }
                }
            }
        }

        private static void WaitFor(Func<bool> condition, string message)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 10000)
            {
                if (condition()) return;
                Thread.Sleep(100);
            }
            Assert.Fail(message);
        }

        internal static T RunClient<T>(Func<T> read)
        {
            // The test owns Word COM objects on this STA; keep it pumping while
            // the application's independent client runs as OneScript normally does.
            var task = Task.Run(read);
            while (!task.IsCompleted)
            {
                System.Windows.Forms.Application.DoEvents();
                Thread.Sleep(10);
            }
            return task.GetAwaiter().GetResult();
        }

        internal static bool ClipboardMarkerRestored(string marker, string output)
        {
            return RunClient(() => ClipboardDispatcher.Shared.Run(() =>
            {
                var stable = 0;
                uint previous = 0;
                for (var attempt = 0; attempt < 20; attempt++)
                {
                    var text = System.Windows.Forms.Clipboard.GetText();
                    var sequence = NativeMethods.GetClipboardSequenceNumber();
                    // Record only classifications of our synthetic fixture, never clipboard contents.
                    var classification = text == marker ? "marker" : text == "improved" ? "replacement"
                        : text == "selected" ? "selection" : text.Length == 0 ? "empty" : "other";
                    File.AppendAllText(Path.Combine(output, "word-marker-observations.log"),
                        classification + ":" + sequence + Environment.NewLine);
                    stable = classification == "marker" ? (previous == sequence ? stable + 1 : 1) : 0;
                    if (stable >= 3) return true;
                    previous = sequence;
                    ClipboardDispatcher.PumpWait(25);
                }
                return false;
            }));
        }
    }
}
