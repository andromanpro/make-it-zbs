using System;
using System.Collections.Generic;
using System.Windows.Automation;
using NUnit.Framework;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class AiChatInteractionTests
    {
        [Test]
        public void TextCollectorKeepsOrderFiltersProcessesAndStopsAtDocument()
        {
            var documentChild = new FakeNode(10, "must not appear");
            var root = new FakeNode(10, "root",
                new FakeNode(10, "first"),
                new FakeNode(20, "foreign"),
                new FakeNode(10, "document", true, documentChild),
                new FakeNode(10, "last"));

            var result = AiChatTextCollector.Collect(root, 10, 100);

            Assert.That(result, Is.EqualTo("root\nfirst\ndocument\nlast"));
            Assert.That(result, Does.Not.Contain("foreign"));
            Assert.That(result, Does.Not.Contain("must not appear"));
        }

        [Test]
        public void TextCollectorHandlesLimitsNullsAndUnavailableNodes()
        {
            Assert.That(AiChatTextCollector.Collect(null, 1, 10), Is.Empty);
            Assert.That(AiChatTextCollector.Collect(new FakeNode(1, "text"), 1, 0), Is.Empty);
            Assert.That(AiChatTextCollector.Collect(new FakeNode(1, "text"), 1, 10, 0), Is.Empty);

            var root = new FakeNode(1, null,
                null,
                new FakeNode(1, "  "),
                new FakeNode(1, "abcdef"),
                new FakeNode(1, "later"));
            Assert.That(AiChatTextCollector.Collect(root, 1, 4), Is.EqualTo("abcd"));

            var unavailable = new FakeNode(1, "ignored") { ThrowUnavailable = true };
            var missingChildren = new FakeNode(1, "kept") { ReturnNullChildren = true };
            root = new FakeNode(1, "", unavailable, missingChildren);
            Assert.That(AiChatTextCollector.Collect(root, 1, 20), Is.EqualTo("kept"));
        }

        [Test]
        public void TextCollectorAcceptsValidatedAutomationHostOnly()
        {
            var root = new FakeNode(20, "terminal",
                new FakeNode(20, "visible transcript"),
                new FakeNode(30, "foreign overlay"));

            var withoutHost = AiChatTextCollector.Collect(root, 10, 100);
            var withHost = AiChatTextCollector.Collect(root, 10, 100, 3000, 20);

            Assert.That(withoutHost, Is.Empty);
            Assert.That(withHost, Is.EqualTo("terminal\nvisible transcript"));
            Assert.That(withHost, Does.Not.Contain("foreign overlay"));
        }

        [Test]
        public void TextCaptureRedactsSecretsAndRejectsUnavailableTranscript()
        {
            var root = new FakeNode(20, "Last task token=DEMO_CAPTURE_SECRET",
                new FakeNode(20, "Continue with the next step"),
                new FakeNode(30, "foreign overlay"));

            var capture = AiChatTextCaptureBuilder.Capture(root, 10, 20, 1000);

            Assert.Multiple(() =>
            {
                Assert.That(capture.Success, Is.True);
                Assert.That(capture.Text, Does.Contain("token=[REDACTED]"));
                Assert.That(capture.Text, Does.Contain("Continue with the next step"));
                Assert.That(capture.Text, Does.Not.Contain("DEMO_CAPTURE_SECRET"));
                Assert.That(capture.Text, Does.Not.Contain("foreign overlay"));
                Assert.That(capture.RedactionCount, Is.EqualTo(1));
                Assert.That(capture.Diagnostic, Does.Contain("секреты скрыты: 1"));
            });

            var clean = AiChatTextCaptureBuilder.Capture(
                new FakeNode(10, "No credentials here"), 10, 0, 1000);
            Assert.Multiple(() =>
            {
                Assert.That(clean.Success, Is.True);
                Assert.That(clean.Text, Is.EqualTo("No credentials here"));
                Assert.That(clean.RedactionCount, Is.Zero);
                Assert.That(clean.Diagnostic, Is.Empty);
            });

            var unavailable = AiChatTextCaptureBuilder.Capture(root, 10, 0, 1000);
            Assert.Multiple(() =>
            {
                Assert.That(unavailable.Success, Is.False);
                Assert.That(unavailable.Text, Is.Empty);
                Assert.That(unavailable.RedactionCount, Is.Zero);
                Assert.That(unavailable.Diagnostic, Does.Contain("видимый текст не найден"));
            });
        }

        [Test]
        public void PasteCoordinatorCompletesDraftAndSubmittedChat()
        {
            var draft = new FakePastePlatform();
            var draftResult = PasteCoordinator.Execute(draft, 42, 7, "text", "chat", false);
            Assert.That(draftResult.Success, Is.True);
            Assert.That(draft.SubmitCalls, Is.Zero);
            Assert.That(draft.RestoreCalls, Is.EqualTo(1));

            var submitted = new FakePastePlatform();
            var submitResult = PasteCoordinator.Execute(submitted, 42, 7, "text", "chat", true);
            Assert.That(submitResult.Success, Is.True);
            Assert.That(submitted.SubmitCalls, Is.EqualTo(1));
            Assert.That(submitted.LastText, Is.EqualTo("text"));
            Assert.That(submitted.LastPasteTarget, Is.EqualTo(42));
            Assert.That(submitted.LastPasteMode, Is.EqualTo("chat"));
        }

        [Test]
        public void TerminalChatInputRecognizesHostsAndBuildsLayoutIndependentUnicode()
        {
            Assert.Multiple(() =>
            {
                Assert.That(TerminalChatInput.IsTerminalWindow("cmd.exe", "other"), Is.True);
                Assert.That(TerminalChatInput.IsTerminalWindow("app.exe", "ConsoleWindowClass"), Is.True);
                Assert.That(TerminalChatInput.IsTerminalWindow(
                    "WindowsTerminal.exe", "CASCADIA_HOSTING_WINDOW_CLASS"), Is.True);
                Assert.That(TerminalChatInput.IsTerminalWindow("chatgpt.exe", "Chrome_WidgetWin_1"), Is.False);
                Assert.That(TerminalChatInput.IsTerminalWindow(null, null), Is.False);
                Assert.That(TerminalChatInput.BuildUnicodeInputs(null), Is.Empty);
            });

            var inputs = TerminalChatInput.BuildUnicodeInputs("Aя");
            Assert.Multiple(() =>
            {
                Assert.That(inputs, Has.Length.EqualTo(4));
                Assert.That(inputs[0].u.ki.wVk, Is.Zero);
                Assert.That(inputs[0].u.ki.wScan, Is.EqualTo('A'));
                Assert.That(inputs[0].u.ki.dwFlags, Is.EqualTo(NativeMethods.KEYEVENTF_UNICODE));
                Assert.That(inputs[1].u.ki.dwFlags, Is.EqualTo(
                    NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP));
                Assert.That(inputs[2].u.ki.wScan, Is.EqualTo('я'));
            });
        }

        [TestCase("windowsterminal.exe")]
        [TestCase("wezterm-gui.exe")]
        [TestCase("alacritty.exe")]
        [TestCase("conhost.exe")]
        [TestCase("cmd.exe")]
        [TestCase("powershell.exe")]
        [TestCase("pwsh.exe")]
        public void TerminalChatInputRecognizesEverySupportedExecutable(string executable)
        {
            Assert.That(TerminalChatInput.IsTerminalWindow(executable, "OtherWindowClass"), Is.True);
        }

        [Test]
        public void TargetPasteRouterUsesUnicodeOnlyForTerminalChats()
        {
            var terminal = new FakeTargetPasteActions { Terminal = true };
            var terminalResult = new PasteExecutionResult();

            var terminalSuccess = TargetPasteRouter.Paste(
                terminal, 42, "Давай дальше.", "chat", "AIChat", terminalResult);

            Assert.Multiple(() =>
            {
                Assert.That(terminalSuccess, Is.True);
                Assert.That(terminal.UnicodeTexts, Is.EqualTo(new[] { "Давай дальше." }));
                Assert.That(terminal.TerminalHandles, Is.EqualTo(new[] { 42 }));
                Assert.That(terminal.CtrlVCalls, Is.Zero);
                Assert.That(terminal.Pauses, Is.EqualTo(new[] { 250 }));
            });

            var gui = new FakeTargetPasteActions();
            var guiSuccess = TargetPasteRouter.Paste(
                gui, 84, "draft", "chat", "AIChat черновик", new PasteExecutionResult());

            Assert.Multiple(() =>
            {
                Assert.That(guiSuccess, Is.True);
                Assert.That(gui.CtrlVCalls, Is.EqualTo(1));
                Assert.That(gui.UnicodeTexts, Is.Empty);
                Assert.That(gui.Pauses, Is.EqualTo(new[] { 250 }));
            });
        }

        [Test]
        public void TargetPasteRouterHandlesFullModuleAndInputFailures()
        {
            var fullModule = new FakeTargetPasteActions();
            var fullModuleSuccess = TargetPasteRouter.Paste(
                fullModule, 1, "module", "fullModule", "замена", new PasteExecutionResult());
            Assert.Multiple(() =>
            {
                Assert.That(fullModuleSuccess, Is.True);
                Assert.That(fullModule.CtrlACalls, Is.EqualTo(1));
                Assert.That(fullModule.CtrlVCalls, Is.EqualTo(1));
                Assert.That(fullModule.Pauses, Is.EqualTo(new[] { 60, 500 }));
            });

            AssertPasteFailure(
                new FakeTargetPasteActions { CtrlAResult = false },
                "fullModule",
                "Ctrl+A");
            AssertPasteFailure(
                new FakeTargetPasteActions { CtrlVResult = false },
                "selection",
                "Ctrl+V");
            AssertPasteFailure(
                new FakeTargetPasteActions { Terminal = true, UnicodeResult = false },
                "chat",
                "Unicode");
        }

        private static void AssertPasteFailure(
            FakeTargetPasteActions actions, string mode, string expectedDiagnostic)
        {
            var result = new PasteExecutionResult();

            var success = TargetPasteRouter.Paste(
                actions, 7, "text", mode, "операция", result);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.False);
                Assert.That(result.Success, Is.False);
                Assert.That(result.Diagnostic, Does.Contain(expectedDiagnostic));
            });
        }

        [Test]
        public void FullModulePasteStopsIfFocusChangesAfterSelectAll()
        {
            var actions = new FakeTargetPasteActions { LoseFocusOnPause = true };
            var result = new PasteExecutionResult();

            var success = TargetPasteRouter.Paste(actions, 7, "module", "fullModule", "replace", result);

            Assert.Multiple(() =>
            {
                Assert.That(success, Is.False);
                Assert.That(actions.CtrlACalls, Is.EqualTo(1));
                Assert.That(actions.CtrlVCalls, Is.Zero);
                Assert.That(result.Diagnostic, Is.Not.Empty);
            });
        }

        [TestCase("selection", false)]
        [TestCase("fullModule", false)]
        [TestCase("chat", true)]
        public void PasteDoesNotInjectKeysWhenTargetHasAlreadyLostFocus(string mode, bool terminal)
        {
            var actions = new FakeTargetPasteActions { Focused = false, Terminal = terminal };
            var success = TargetPasteRouter.Paste(actions, 7, "text", mode, "paste", new PasteExecutionResult());

            Assert.That(success, Is.False);
            Assert.That(actions.CtrlACalls + actions.CtrlVCalls, Is.Zero);
            Assert.That(actions.UnicodeTexts, Is.Empty);
        }

        [Test]
        public void PasteCoordinatorRejectsEmptyTextAndUnavailableBackup()
        {
            var platform = new FakePastePlatform();
            var empty = PasteCoordinator.Execute(platform, 0, 0, "", "selection", false);
            Assert.That(empty.Diagnostic, Does.Contain("пустой текст"));
            Assert.That(platform.CaptureCalls, Is.Zero);

            platform = new FakePastePlatform { Snapshot = null };
            var missing = PasteCoordinator.Execute(platform, 0, 0, "text", "selection", false);
            Assert.That(missing.Diagnostic, Does.Contain("снимок не создан"));

            platform = new FakePastePlatform
            {
                Snapshot = PasteClipboardSnapshot.Failed(new InvalidOperationException("locked"))
            };
            var failed = PasteCoordinator.Execute(platform, 0, 0, "text", "selection", false);
            Assert.That(failed.Diagnostic, Does.Contain("locked"));
        }

        [TestCase(FailureStage.Validate)]
        [TestCase(FailureStage.Focus)]
        [TestCase(FailureStage.Paste)]
        [TestCase(FailureStage.Submit)]
        public void PasteCoordinatorStopsAtFailedPlatformStage(FailureStage stage)
        {
            var platform = new FakePastePlatform { Failure = stage };
            var result = PasteCoordinator.Execute(platform, 42, 7, "text", "chat", true);
            Assert.That(result.Success, Is.False);
            Assert.That(result.Diagnostic, Does.Contain(stage.ToString()));
            Assert.That(platform.RestoreCalls, Is.EqualTo(1));
        }

        [Test]
        public void PasteCoordinatorReportsOperationAndRestoreExceptions()
        {
            var platform = new FakePastePlatform { ThrowOnSet = true };
            var setFailure = PasteCoordinator.Execute(platform, 1, 1, "text", "selection", false);
            Assert.That(setFailure.Diagnostic, Does.Contain("set failed"));
            Assert.That(platform.RestoreCalls, Is.EqualTo(1));

            platform = new FakePastePlatform { ThrowOnRestore = true };
            var restoreAfterSuccess = PasteCoordinator.Execute(platform, 1, 1, "text", "selection", false);
            Assert.That(restoreAfterSuccess.Success, Is.True, "A restore failure must not undo an already performed paste.");
            Assert.That(restoreAfterSuccess.ClipboardRestoreFailed, Is.True);
            Assert.That(platform.LastPasteTarget, Is.EqualTo(1));
            Assert.That(restoreAfterSuccess.Diagnostic, Does.Contain("restore failed"));

            platform = new FakePastePlatform
            {
                Failure = FailureStage.Validate,
                ThrowOnRestore = true
            };
            var restoreAfterFailure = PasteCoordinator.Execute(platform, 1, 1, "text", "selection", false);
            Assert.That(restoreAfterFailure.Success, Is.False);
            Assert.That(restoreAfterFailure.ClipboardRestoreFailed, Is.True);
            Assert.That(restoreAfterFailure.Diagnostic, Does.Contain("Validate | restore failed"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void RestoreFailurePreservesDraftOrSubmittedChatAndCrossLayerWarning(bool submit)
        {
            var platform = new FakePastePlatform { ThrowOnRestore = true };
            var result = PasteCoordinator.Execute(platform, 42, 7, "synthetic message", "chat", submit);
            var exposed = (ScriptEngine.Machine.IRuntimeContextInstance)new WindowInfo().CreatePasteResult(result);
            Assert.That(result.Success, Is.True);
            Assert.That(platform.SubmitCalls, Is.EqualTo(submit ? 1 : 0));
            Assert.That(exposed.GetPropValue(exposed.FindProperty("Успех")).AsBoolean(), Is.True);
            Assert.That(exposed.GetPropValue(exposed.FindProperty("БуферНеВосстановлен")).AsBoolean(), Is.True);
        }

        [Test]
        public void ClipboardSnapshotFactoriesExposeSafeState()
        {
            var empty = PasteClipboardSnapshot.Empty();
            var data = new object();
            var captured = PasteClipboardSnapshot.Captured(data);
            var failed = PasteClipboardSnapshot.Failed(new ArgumentException("bad"));
            Assert.Multiple(() =>
            {
                Assert.That(empty.CanRestore, Is.True);
                Assert.That(empty.WasEmpty, Is.True);
                Assert.That(captured.Data, Is.SameAs(data));
                Assert.That(failed.CanRestore, Is.False);
                Assert.That(failed.Error, Does.Contain("ArgumentException: bad"));
                Assert.That(new PasteExecutionResult().Fail(null).Diagnostic, Is.Empty);
            });
        }

        [Test]
        public void ClipboardCopiedByAnotherApplicationIsNotOverwrittenOnRestore()
        {
            var platform = new FakePastePlatform { ExternalCopyDuringPaste = true };
            var result = PasteCoordinator.Execute(platform, 42, 7, "text", "selection", false);
            Assert.That(result.Success, Is.True);
            Assert.That(platform.RestoreCalls, Is.Zero);
        }

        private sealed class FakeNode : IAiChatTextNode
        {
            private readonly string _text;
            private readonly bool _stopTraversal;
            private readonly IReadOnlyList<IAiChatTextNode> _children;

            public FakeNode(int processId, string text, params IAiChatTextNode[] children)
                : this(processId, text, false, children)
            {
            }

            public FakeNode(
                int processId, string text, bool stopTraversal, params IAiChatTextNode[] children)
            {
                ProcessId = processId;
                _text = text;
                _stopTraversal = stopTraversal;
                _children = children;
            }

            public int ProcessId { get; }
            public bool ThrowUnavailable { get; set; }
            public bool ReturnNullChildren { get; set; }

            public AiChatNodeText ReadText(int maxCharacters)
            {
                if (ThrowUnavailable)
                {
                    throw new ElementNotAvailableException();
                }

                return new AiChatNodeText(_text, _stopTraversal);
            }

            public IReadOnlyList<IAiChatTextNode> GetChildren()
            {
                return ReturnNullChildren ? null : _children;
            }
        }

        public enum FailureStage
        {
            None,
            Validate,
            Focus,
            Paste,
            Submit
        }

        private sealed class FakePastePlatform : IPastePlatform
        {
            public uint Sequence { get; set; }
            public bool ExternalCopyDuringPaste { get; set; }
            public uint GetClipboardSequence() => Sequence;
            public PasteClipboardSnapshot Snapshot { get; set; } = PasteClipboardSnapshot.Empty();
            public FailureStage Failure { get; set; }
            public bool ThrowOnSet { get; set; }
            public bool ThrowOnRestore { get; set; }
            public int CaptureCalls { get; private set; }
            public int RestoreCalls { get; private set; }
            public int SubmitCalls { get; private set; }
            public string LastText { get; private set; }
            public int LastPasteTarget { get; private set; }
            public string LastPasteMode { get; private set; }

            public PasteClipboardSnapshot CaptureClipboard()
            {
                CaptureCalls++;
                return Snapshot;
            }

            public void SetClipboardText(string text)
            {
                if (ThrowOnSet)
                {
                    throw new InvalidOperationException("set failed");
                }

                LastText = text;
                Sequence++;
            }

            public bool ValidateTarget(
                int targetHandle, int expectedProcessId, string operation, PasteExecutionResult result)
            {
                return Complete(FailureStage.Validate, result);
            }

            public bool FocusTarget(int targetHandle, string operation, PasteExecutionResult result)
            {
                return Complete(FailureStage.Focus, result);
            }

            public bool PasteText(
                int targetHandle,
                string text,
                string mode,
                string operation,
                PasteExecutionResult result)
            {
                LastPasteTarget = targetHandle;
                LastPasteMode = mode;
                if (ExternalCopyDuringPaste) Sequence++;
                return Complete(FailureStage.Paste, result);
            }

            public bool SubmitChatMessage(int targetHandle, PasteExecutionResult result)
            {
                SubmitCalls++;
                return Complete(FailureStage.Submit, result);
            }

            public void RestoreClipboard(PasteClipboardSnapshot snapshot)
            {
                RestoreCalls++;
                if (ThrowOnRestore)
                {
                    throw new InvalidOperationException("restore failed");
                }
            }

            private bool Complete(FailureStage stage, PasteExecutionResult result)
            {
                if (Failure != stage)
                {
                    return true;
                }

                result.Fail(stage.ToString());
                return false;
            }
        }

        private sealed class FakeTargetPasteActions : ITargetPasteActions
        {
            public bool Terminal { get; set; }
            public bool Focused { get; set; } = true;
            public bool LoseFocusOnPause { get; set; }
            public bool CtrlAResult { get; set; } = true;
            public bool CtrlVResult { get; set; } = true;
            public bool UnicodeResult { get; set; } = true;
            public int CtrlACalls { get; private set; }
            public int CtrlVCalls { get; private set; }
            public List<int> TerminalHandles { get; } = new List<int>();
            public List<string> UnicodeTexts { get; } = new List<string>();
            public List<int> Pauses { get; } = new List<int>();

            public bool TargetIsFocused(int targetHandle) => Focused;

            public bool TargetIsTerminal(int targetHandle)
            {
                TerminalHandles.Add(targetHandle);
                return Terminal;
            }

            public bool SelectAll()
            {
                CtrlACalls++;
                return CtrlAResult;
            }

            public bool PasteClipboard()
            {
                CtrlVCalls++;
                return CtrlVResult;
            }

            public bool SendUnicode(string text)
            {
                UnicodeTexts.Add(text);
                return UnicodeResult;
            }

            public void Pause(int milliseconds)
            {
                Pauses.Add(milliseconds);
                if (LoseFocusOnPause)
                {
                    Focused = false;
                }
            }
        }
    }
}
