using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Automation;

namespace ZBSWindowsComponent
{
    internal interface IAiChatTextNode
    {
        int ProcessId { get; }
        AiChatNodeText ReadText(int maxCharacters);
        IReadOnlyList<IAiChatTextNode> GetChildren();
    }

    internal struct AiChatNodeText
    {
        public AiChatNodeText(string text, bool stopTraversal)
        {
            Text = text;
            StopTraversal = stopTraversal;
        }

        public string Text { get; }
        public bool StopTraversal { get; }
    }

    internal static class AiChatTextCollector
    {
        public static string Collect(
            IAiChatTextNode root,
            int expectedProcessId,
            int maxCharacters,
            int maxNodes = 3000,
            int trustedAutomationProcessId = 0)
        {
            if (root == null || maxCharacters <= 0 || maxNodes <= 0)
            {
                return string.Empty;
            }

            var output = new StringBuilder(Math.Min(maxCharacters, 8192));
            var pending = new Stack<IAiChatTextNode>();
            pending.Push(root);
            var visited = 0;

            while (pending.Count > 0 && output.Length < maxCharacters && visited < maxNodes)
            {
                var node = pending.Pop();
                visited++;
                try
                {
                    CollectNode(
                        node,
                        expectedProcessId,
                        trustedAutomationProcessId,
                        maxCharacters,
                        output,
                        pending);
                }
                catch (ElementNotAvailableException)
                {
                    // Electron can replace an automation node while it is being inspected.
                }
            }

            return output.ToString().Trim();
        }

        private static void CollectNode(
            IAiChatTextNode node,
            int expectedProcessId,
            int trustedAutomationProcessId,
            int maxCharacters,
            StringBuilder output,
            Stack<IAiChatTextNode> pending)
        {
            if (node.ProcessId != expectedProcessId
                && (trustedAutomationProcessId <= 0
                    || node.ProcessId != trustedAutomationProcessId))
            {
                return;
            }

            var value = node.ReadText(Math.Max(0, maxCharacters - output.Length));
            Append(output, value.Text, maxCharacters);
            if (value.StopTraversal && !string.IsNullOrWhiteSpace(value.Text))
            {
                return;
            }

            var children = node.GetChildren();
            if (children == null)
            {
                return;
            }

            for (var i = children.Count - 1; i >= 0; i--)
            {
                if (children[i] != null)
                {
                    pending.Push(children[i]);
                }
            }
        }

        private static void Append(StringBuilder output, string value, int maxCharacters)
        {
            value = (value ?? string.Empty).Trim();
            if (value.Length == 0)
            {
                return;
            }

            var remaining = maxCharacters - output.Length;
            if (output.Length > 0)
            {
                output.Append('\n');
                remaining--;
            }

            if (remaining > 0)
            {
                output.Append(value.Length <= remaining ? value : value.Substring(0, remaining));
            }
        }
    }

    internal sealed class AiChatTextCapture
    {
        public bool Success { get; set; }
        public string Text { get; set; } = string.Empty;
        public string Diagnostic { get; set; } = string.Empty;
        public int RedactionCount { get; set; }
    }

    internal static class AiChatTextCaptureBuilder
    {
        public static AiChatTextCapture Capture(
            IAiChatTextNode root,
            int expectedProcessId,
            int trustedAutomationProcessId,
            int maxCharacters)
        {
            var result = new AiChatTextCapture();
            var text = AiChatTextCollector.Collect(
                root,
                expectedProcessId,
                maxCharacters,
                3000,
                trustedAutomationProcessId);
            if (string.IsNullOrWhiteSpace(text))
            {
                result.Diagnostic =
                    "AIChat UIA: видимый текст не найден; выберите источник 'Снимок окна' явно";
                return result;
            }

            result.Text = SecretRedactor.Redact(text, out var redactionCount);
            result.RedactionCount = redactionCount;
            result.Success = true;
            result.Diagnostic = redactionCount > 0
                ? "AIChat UIA: секреты скрыты: " + redactionCount
                : string.Empty;
            return result;
        }
    }

    internal interface IPastePlatform
    {
        uint GetClipboardSequence();
        PasteClipboardSnapshot CaptureClipboard();
        void SetClipboardText(string text);
        bool ValidateTarget(int targetHandle, int expectedProcessId, string operation, PasteExecutionResult result);
        bool FocusTarget(int targetHandle, string operation, PasteExecutionResult result);
        bool PasteText(
            int targetHandle,
            string text,
            string mode,
            string operation,
            PasteExecutionResult result);
        bool SubmitChatMessage(int targetHandle, PasteExecutionResult result);
        void RestoreClipboard(PasteClipboardSnapshot snapshot);
    }

    internal static class TerminalChatInput
    {
        public static bool IsTerminalWindow(string executable, string className)
        {
            var normalizedExecutable = (executable ?? string.Empty).Trim().ToLowerInvariant();
            var normalizedClass = (className ?? string.Empty).Trim();
            if (string.Equals(normalizedClass, "ConsoleWindowClass", StringComparison.OrdinalIgnoreCase)
                || string.Equals(normalizedClass, "CASCADIA_HOSTING_WINDOW_CLASS", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            switch (normalizedExecutable)
            {
                case "windowsterminal.exe":
                case "wezterm-gui.exe":
                case "alacritty.exe":
                case "conhost.exe":
                case "cmd.exe":
                case "powershell.exe":
                case "pwsh.exe":
                    return true;
                default:
                    return false;
            }
        }

        public static NativeMethods.Input[] BuildUnicodeInputs(string text)
        {
            var value = text ?? string.Empty;
            var inputs = new NativeMethods.Input[value.Length * 2];
            var index = 0;
            foreach (var character in value)
            {
                inputs[index].type = NativeMethods.INPUT_KEYBOARD;
                inputs[index].u.ki.wScan = character;
                inputs[index].u.ki.dwFlags = NativeMethods.KEYEVENTF_UNICODE;
                index++;

                inputs[index].type = NativeMethods.INPUT_KEYBOARD;
                inputs[index].u.ki.wScan = character;
                inputs[index].u.ki.dwFlags =
                    NativeMethods.KEYEVENTF_UNICODE | NativeMethods.KEYEVENTF_KEYUP;
                index++;
            }

            return inputs;
        }
    }

    internal interface ITargetPasteActions
    {
        bool TargetIsFocused(int targetHandle);
        bool TargetIsTerminal(int targetHandle);
        bool SelectAll();
        bool PasteClipboard();
        bool SendUnicode(string text);
        void Pause(int milliseconds);
    }

    internal static class TargetPasteRouter
    {
        public static bool Paste(
            ITargetPasteActions actions,
            int targetHandle,
            string text,
            string mode,
            string operation,
            PasteExecutionResult result)
        {
            if (!CheckFocus(actions, targetHandle, operation, result))
            {
                return false;
            }

            var isFullModule = string.Equals(mode, "fullModule", StringComparison.Ordinal);
            if (isFullModule && !actions.SelectAll())
            {
                result.Fail(operation + " отменён: WinAPI не принял Ctrl+A");
                return false;
            }

            if (isFullModule)
            {
                actions.Pause(60);
            }

            if (string.Equals(mode, "chat", StringComparison.Ordinal)
                && actions.TargetIsTerminal(targetHandle))
            {
                if (!CheckFocus(actions, targetHandle, operation, result))
                {
                    return false;
                }

                if (!actions.SendUnicode(text))
                {
                    result.Fail(operation + " отменён: WinAPI не принял Unicode-ввод");
                    return false;
                }

                actions.Pause(250);
                return true;
            }

            if (!CheckFocus(actions, targetHandle, operation, result))
            {
                return false;
            }

            if (!actions.PasteClipboard())
            {
                result.Fail(operation + " отменён: WinAPI не принял Ctrl+V");
                return false;
            }

            actions.Pause(isFullModule ? 500 : 250);
            return true;
        }

        private static bool CheckFocus(
            ITargetPasteActions actions, int targetHandle, string operation, PasteExecutionResult result)
        {
            if (actions.TargetIsFocused(targetHandle))
            {
                return true;
            }

            result.Fail(operation + " отменён: целевое окно потеряло фокус перед вводом");
            return false;
        }
    }

    internal sealed class PasteClipboardSnapshot : IDisposable
    {
        public bool CanRestore { get; private set; }
        public bool WasEmpty { get; private set; }
        public object Data { get; private set; }
        public string Error { get; private set; }

        public void Dispose() => (Data as IDisposable)?.Dispose();

        public static PasteClipboardSnapshot Empty()
        {
            return new PasteClipboardSnapshot
            {
                CanRestore = true,
                WasEmpty = true,
                Error = string.Empty
            };
        }

        public static PasteClipboardSnapshot Captured(object data)
        {
            return new PasteClipboardSnapshot
            {
                CanRestore = true,
                Data = data,
                Error = string.Empty
            };
        }

        public static PasteClipboardSnapshot Failed(Exception exception)
        {
            return new PasteClipboardSnapshot
            {
                CanRestore = false,
                Error = exception.GetType().Name + ": " + exception.Message
            };
        }
    }

    internal sealed class PasteExecutionResult
    {
        public bool Success { get; set; }
        public bool ClipboardRestoreFailed { get; set; }
        public string Diagnostic { get; set; } = string.Empty;

        public PasteExecutionResult Fail(string diagnostic)
        {
            Success = false;
            Diagnostic = diagnostic ?? string.Empty;
            return this;
        }
    }

    internal static class PasteCoordinator
    {
        public static PasteExecutionResult Execute(
            IPastePlatform platform,
            int targetHandle,
            int expectedProcessId,
            string text,
            string mode,
            bool submitAfterPaste)
        {
            var operation = "замена";
            if (string.Equals(mode, "chat", StringComparison.Ordinal))
            {
                operation = submitAfterPaste ? "AIChat" : "AIChat черновик";
            }

            var result = new PasteExecutionResult();
            if (string.IsNullOrEmpty(text))
            {
                return result.Fail(operation + ": пустой текст результата");
            }

            var backup = platform.CaptureClipboard();
            if (backup == null || !backup.CanRestore)
            {
                return result.Fail(operation
                    + " отменён: не удалось полностью сохранить буфер обмена ("
                    + (backup?.Error ?? "снимок не создан") + ")");
            }

            var clipboardSequence = platform.GetClipboardSequence();
            try
            {
                platform.SetClipboardText(text);
                clipboardSequence = platform.GetClipboardSequence();
                if (!platform.ValidateTarget(targetHandle, expectedProcessId, operation, result)
                    || !platform.FocusTarget(targetHandle, operation, result)
                    || !platform.PasteText(targetHandle, text, mode, operation, result))
                {
                    return result;
                }

                if (submitAfterPaste && !platform.SubmitChatMessage(targetHandle, result))
                {
                    return result;
                }

                result.Success = true;
            }
            catch (Exception exception)
            {
                result.Fail(operation + ": " + exception.GetType().Name + ": " + exception.Message);
            }
            finally
            {
                try
                {
                    if (platform.GetClipboardSequence() == clipboardSequence)
                    {
                        platform.RestoreClipboard(backup);
                    }
                }
                catch (Exception exception)
                {
                    result.ClipboardRestoreFailed = true;
                    result.Diagnostic = string.IsNullOrEmpty(result.Diagnostic)
                        ? operation + ": restore failed: " + exception.Message
                        : result.Diagnostic + " | restore failed: " + exception.Message;
                }
                backup.Dispose();
            }

            return result;
        }
    }
}
