using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

using ScriptEngine.HostedScript.Library;

namespace ZBSWindowsComponent
{
    /// <summary>
    /// Информация об окнах Windows
    /// </summary>
    [ContextClass("ИнформацияОбОкне", "WindowInfo")]
    public partial class WindowInfo : AutoContext<WindowInfo>
    {
        private const string DiagnosticKey = "Диагностика";
        private const int MaxExcelSelectionCells = 10000;

        [ScriptConstructor]
        public static WindowInfo Constructor()
        {
            return new WindowInfo();
        }

        // Последняя причина, по которой захват текста не удался.
        // Раньше исключения молча глотались (catch {}), и понять,
        // почему приходит пустой текст, было невозможно.
        private string _lastDiagnostic = "";

        /// <summary>
        /// Диагностика последней попытки захвата текста (для логов OneScript)
        /// </summary>
        [ContextMethod("ПолучитьДиагностикуЗахвата", "GetLastCaptureDiagnostic")]
        public string GetLastCaptureDiagnostic()
        {
            return _lastDiagnostic ?? "";
        }

        [ContextMethod("РазмерPNG", "GetPngSize")]
        public IValue GetPngSize(string path)
        {
            var result = new StructureImpl();

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                string diagnostic = "РазмерPNG: файл не найден: " + path;
                _lastDiagnostic = diagnostic;
                result.Insert("Ширина", ValueFactory.Create(0));
                result.Insert("Высота", ValueFactory.Create(0));
                result.Insert(DiagnosticKey, ValueFactory.Create(diagnostic));
                return result;
            }

            try
            {
                using (var fs = File.OpenRead(path))
                using (var img = Image.FromStream(fs, false, false))
                {
                    _lastDiagnostic = "";
                    result.Insert("Ширина", ValueFactory.Create(img.Width));
                    result.Insert("Высота", ValueFactory.Create(img.Height));
                    result.Insert(DiagnosticKey, ValueFactory.Create(""));
                    return result;
                }
            }
            catch (Exception ex)
            {
                string diagnostic = "РазмерPNG: " + ex.GetType().Name + ": " + ex.Message;
                _lastDiagnostic = diagnostic;
                result.Insert("Ширина", ValueFactory.Create(0));
                result.Insert("Высота", ValueFactory.Create(0));
                result.Insert(DiagnosticKey, ValueFactory.Create(diagnostic));
                return result;
            }
        }

        [ContextMethod("ОбрезатьPNG", "CropPng")]
        public bool CropPng(string srcPath, int x, int y, int w, int h, string dstPath)
        {
            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
            {
                _lastDiagnostic = "ОбрезатьPNG: нет файла " + srcPath;
                return false;
            }

            try
            {
                using (var fs = File.OpenRead(srcPath))
                using (var tmp = Image.FromStream(fs))
                using (var src = new Bitmap(tmp))
                {
                    int cx = Math.Max(0, Math.Min(x, src.Width - 1));
                    int cy = Math.Max(0, Math.Min(y, src.Height - 1));
                    int cw = Math.Max(1, Math.Min(w, src.Width - cx));
                    int ch = Math.Max(1, Math.Min(h, src.Height - cy));

                    string directory = Path.GetDirectoryName(dstPath);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        Directory.CreateDirectory(directory);
                    }

                    using (var crop = src.Clone(new Rectangle(cx, cy, cw, ch), src.PixelFormat))
                    {
                        crop.Save(dstPath, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }

                _lastDiagnostic = "";
                return true;
            }
            catch (Exception ex)
            {
                _lastDiagnostic = "ОбрезатьPNG: " + ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // Clipboard and Office COM require STA. UIA reads use a separate,
        // persistent MTA apartment and return only managed snapshots.
        private T RunInSta<T>(string tag, Func<T> action)
        {
            try { return ClipboardDispatcher.Shared.Run(action); }
            catch (Exception error)
            {
                _lastDiagnostic = tag + " STA: " + error.GetType().Name + ": " + error.Message;
                return default(T);
            }
        }

        private T RunInUia<T>(string tag, Func<T> read, System.Threading.CancellationToken? cancellation = null)
        {
            try
            {
                return UiaDispatcher.Shared.Run(read, cancellation ?? Operations.Current?.Token ?? System.Threading.CancellationToken.None);
            }
            catch (Exception error)
            {
                _lastDiagnostic = tag + ": " + error.GetType().Name;
                return default(T);
            }
        }

        /// <summary>
        /// Получить информацию об активном окне
        /// </summary>
        [ContextMethod("ПолучитьАктивноеОкно", "GetActiveWindow")]
        public IValue GetActiveWindow()
        {
            IntPtr hWnd = NativeMethods.GetForegroundWindow();

            if (hWnd == IntPtr.Zero)
            {
                return ValueFactory.Create(); // Неопределено
            }

            return CreateWindowInfoStruct(hWnd);
        }

        /// <summary>
        /// Найти окно по заголовку (частичное совпадение)
        /// </summary>
        [ContextMethod("НайтиОкноПоЗаголовку", "FindWindowByTitle")]
        public IValue FindWindowByTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return ValueFactory.Create();
            }

            // Поиск через EnumWindows
            IntPtr foundWindow = IntPtr.Zero;
            NativeMethods.EnumWindowsProc callback = (hWnd, lParam) =>
            {
                if (!NativeMethods.IsWindowVisible(hWnd))
                    return true;

                StringBuilder windowTitle = new StringBuilder(256);
                NativeMethods.GetWindowText(hWnd, windowTitle, windowTitle.Capacity);

                if (windowTitle.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    foundWindow = hWnd;
                    return false; // Остановить перебор
                }

                return true;
            };

            NativeMethods.EnumWindows(callback, IntPtr.Zero);

            if (foundWindow == IntPtr.Zero)
            {
                return ValueFactory.Create(); // Неопределено
            }

            return CreateWindowInfoStruct(foundWindow);
        }

        /// <summary>
        /// Найти окно по точному заголовку
        /// </summary>
        [ContextMethod("НайтиОкноПоТочномуЗаголовку", "FindWindowByExactTitle")]
        public IValue FindWindowByExactTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
            {
                return ValueFactory.Create();
            }

            IntPtr hWnd = NativeMethods.FindWindow(null, title);

            if (hWnd == IntPtr.Zero)
            {
                return ValueFactory.Create();
            }

            return CreateWindowInfoStruct(hWnd);
        }

        /// <summary>
        /// Получить список всех окон
        /// </summary>
        [ContextMethod("ПолучитьВсеОкна", "GetAllWindows")]
        public ArrayImpl GetAllWindows()
        {
            var result = new ArrayImpl();

            NativeMethods.EnumWindowsProc callback = (hWnd, lParam) =>
            {
                if (NativeMethods.IsWindowVisible(hWnd))
                {
                    StringBuilder title = new StringBuilder(256);
                    NativeMethods.GetWindowText(hWnd, title, title.Capacity);

                    // Пропускаем окна без заголовка
                    if (title.Length > 0)
                    {
                        result.Add(CreateWindowInfoStruct(hWnd));
                    }
                }
                return true;
            };

            NativeMethods.EnumWindows(callback, IntPtr.Zero);

            return result;
        }

        /// <summary>
        /// Проверить, активно ли окно
        /// </summary>
        [ContextMethod("ОкноАктивно", "IsWindowActive")]
        public bool IsWindowActive(int handle)
        {
            IntPtr hWnd = new IntPtr(handle);
            if (hWnd == IntPtr.Zero)
            {
                return false;
            }

            IntPtr activeWindow = NativeMethods.GetForegroundWindow();
            return hWnd == activeWindow;
        }

        /// <summary>
        /// Активировать окно
        /// </summary>
        [ContextMethod("АктивироватьОкно", "ActivateWindow")]
        public bool ActivateWindow(int handle)
        {
            IntPtr hWnd = new IntPtr(handle);
            return NativeMethods.SetForegroundWindow(hWnd);
        }

        /// <summary>
        /// Получить выделенный текст из активного окна
        /// </summary>
        [ContextMethod("ПолучитьВыделенныйТекст", "GetSelectedText")]
        public string GetSelectedText(int targetHandle = 0, int targetProcessId = 0, string targetTitle = null)
        {
            IntPtr hWnd = targetHandle == 0 ? NativeMethods.GetForegroundWindow() : new IntPtr(targetHandle);

            if (hWnd == IntPtr.Zero)
            {
                return string.Empty;
            }

            _lastDiagnostic = "";
            var source = new CaptureSource(hWnd, targetProcessId, targetTitle);

            uint foregroundProcessId;
            NativeMethods.GetWindowThreadProcessId(hWnd, out foregroundProcessId);
            if (string.Equals(TryGetProcessName(foregroundProcessId), "EXCEL",
                StringComparison.OrdinalIgnoreCase))
            {
                string excelText = RunInSta(
                    "Excel COM", () => CaptureExcelSelection(hWnd, foregroundProcessId));
                source.Validate();
                if (!string.IsNullOrEmpty(excelText))
                {
                    return excelText;
                }
            }

            // Попытка 1: буфер обмена через Ctrl+C — надёжнее для
            // VS Code/Chrome/Electron, где UIA TextPattern недоступен
            // (см. аудит: UIA не работает для большинства целевых окон).
            string text = TryGetTextViaClipboard(source);
            source.Validate();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            // UIA reads stay on the same windowless MTA apartment.
            IntPtr hWndForUia = hWnd;
            text = RunInUia("UIA", () => TryGetTextViaUIAutomation(hWndForUia));
            source.Validate();
            if (!string.IsNullOrEmpty(text))
            {
                return text;
            }

            if (string.IsNullOrEmpty(_lastDiagnostic))
                _lastDiagnostic = "и буфер, и UIA вернули пусто (нет выделения?)";
            return string.Empty;
        }

        private string CaptureExcelSelection(IntPtr foregroundWindow, uint foregroundProcessId)
        {
            object application = null;
            try
            {
                application = Marshal.GetActiveObject("Excel.Application");
                return CaptureExcelSelectionFromApplication(
                    application,
                    foregroundWindow,
                    foregroundProcessId,
                    GetWindowProcessId,
                    NativeMethods.GetForegroundWindow);
            }
            catch (Exception ex)
            {
                _lastDiagnostic = "Excel COM: " + ex.GetType().Name + ": " + ex.Message;
                return string.Empty;
            }
            finally
            {
                ReleaseComObject(application);
            }
        }

        internal string CaptureExcelSelectionFromApplication(
            object application,
            IntPtr foregroundWindow,
            uint foregroundProcessId,
            Func<IntPtr, uint> getProcessId,
            Func<IntPtr> getForegroundWindow)
        {
            object selection = null;
            try
            {
                var excelWindow = new IntPtr(Convert.ToInt64(GetComProperty(application, "Hwnd")));
                var excelProcessId = getProcessId(excelWindow);
                if (excelProcessId != foregroundProcessId
                    || foregroundWindow != getForegroundWindow())
                {
                    _lastDiagnostic = "Excel COM: активная книга не совпадает с foreground PID/HWND";
                    return string.Empty;
                }

                selection = GetComProperty(application, "Selection");
                if (selection == null)
                {
                    _lastDiagnostic = "Excel COM: выделенный диапазон не найден";
                    return string.Empty;
                }

                var result = SerializeExcelRange(GetComProperty(selection, "Value2"));
                if (string.IsNullOrEmpty(result))
                {
                    _lastDiagnostic = "Excel COM: выделенный диапазон пуст";
                }
                return result;
            }
            catch (Exception ex)
            {
                _lastDiagnostic = "Excel COM: " + ex.GetType().Name + ": " + ex.Message;
                return string.Empty;
            }
            finally
            {
                ReleaseComObject(selection);
            }
        }

        private static uint GetWindowProcessId(IntPtr window)
        {
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);
            return processId;
        }

        internal static string SerializeExcelRange(object value)
        {
            var values = value as Array;
            if (values == null)
            {
                return FormatExcelValue(value);
            }
            if (values.Rank != 2)
            {
                throw new InvalidOperationException("Excel вернул диапазон неожиданной размерности");
            }

            var rows = values.GetLength(0);
            var columns = values.GetLength(1);
            if ((long)rows * columns > MaxExcelSelectionCells)
            {
                throw new InvalidOperationException(
                    "Выделено больше " + MaxExcelSelectionCells + " ячеек");
            }

            var firstRow = values.GetLowerBound(0);
            var firstColumn = values.GetLowerBound(1);
            var result = new StringBuilder();
            for (var row = 0; row < rows; row++)
            {
                if (row > 0)
                {
                    result.AppendLine();
                }
                for (var column = 0; column < columns; column++)
                {
                    if (column > 0)
                    {
                        result.Append('\t');
                    }
                    result.Append(FormatExcelValue(
                        values.GetValue(firstRow + row, firstColumn + column)));
                }
            }
            return result.ToString();
        }

        private static string FormatExcelValue(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            var formattable = value as IFormattable;
            var text = formattable == null
                ? Convert.ToString(value)
                : formattable.ToString(null, CultureInfo.InvariantCulture);
            return (text ?? string.Empty)
                .Replace('\t', ' ')
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }

        private static object GetComProperty(object target, string name)
        {
            return target.GetType().InvokeMember(
                name, BindingFlags.GetProperty, null, target, null);
        }

        private static void ReleaseComObject(object value)
        {
            if (value != null && Marshal.IsComObject(value))
            {
                Marshal.FinalReleaseComObject(value);
            }
        }

        /// <summary>
        /// Получить выделенный текст из редактора модуля 1С через UI Automation.
        /// Если выделения нет, возвращает длину всего модуля для подтверждения.
        /// </summary>
        [ContextMethod("ПолучитьТекстМодуля1С", "GetOneCModuleText")]
        public IValue GetOneCModuleText(int targetHandle = 0, int targetProcessId = 0, string targetTitle = null)
        {
            _lastDiagnostic = "";
            var source = targetHandle == 0 ? null : new CaptureSource(new IntPtr(targetHandle), targetProcessId, targetTitle);
            var capture = RunInUia("OneC GetText", () => CaptureOneCModuleText(source));
            source?.Validate();
            if (capture == null) capture = new OneCModuleCapture { Diagnostic = _lastDiagnostic };

            if (!string.IsNullOrEmpty(capture.Diagnostic))
            {
                _lastDiagnostic = capture.Diagnostic;
            }

            var result = new StructureImpl();
            result.Insert("ЕстьВыделение", ValueFactory.Create(capture.HasSelection));
            result.Insert("Текст", ValueFactory.Create(capture.Text));
            result.Insert("ДлинаМодуля", ValueFactory.Create(capture.ModuleLength));
            result.Insert(DiagnosticKey, ValueFactory.Create(capture.Diagnostic));
            return result;
        }

        private static OneCModuleCapture CaptureOneCModuleText(CaptureSource source)
        {
            var capture = new OneCModuleCapture();
            try
            {
                AutomationElement focusedElement = AutomationElement.FocusedElement;
                source?.Validate();
                if (source != null && !IsWithinAutomationRoot(AutomationElement.FromHandle(source.Handle), focusedElement))
                    return new OneCModuleCapture { Diagnostic = "OneC: фокус вне исходного окна" };
                object pattern;
                if (focusedElement == null
                    || !focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    capture.Diagnostic = "OneC: фокусный элемент без TextPattern";
                    return capture;
                }

                var textPattern = pattern as TextPattern;
                if (textPattern == null)
                {
                    capture.Diagnostic = "OneC: фокусный элемент без TextPattern";
                    return capture;
                }

                var selections = textPattern.GetSelection();
                if (selections != null && selections.Length > 0)
                {
                    capture.Text = selections[0].GetText(-1) ?? string.Empty;
                    capture.HasSelection = capture.Text.Length > 0;
                }

                ReadOneCModuleLength(textPattern, capture);
            }
            catch (Exception ex)
            {
                capture.Reset("OneC GetText: " + ex.GetType().Name + ": " + ex.Message);
            }

            return capture;
        }

        private static void ReadOneCModuleLength(TextPattern textPattern, OneCModuleCapture capture)
        {
            try
            {
                string whole = textPattern.DocumentRange.GetText(-1);
                capture.ModuleLength = whole == null ? 0 : whole.Length;
            }
            catch (Exception ex)
            {
                capture.ModuleLength = 0;
                capture.Diagnostic = "OneC DocumentRange: " + ex.GetType().Name + ": " + ex.Message;
            }
        }

        private sealed class OneCModuleCapture
        {
            public bool HasSelection { get; set; }
            public string Text { get; set; } = string.Empty;
            public int ModuleLength { get; set; }
            public string Diagnostic { get; set; } = string.Empty;

            public void Reset(string diagnostic)
            {
                HasSelection = false;
                Text = string.Empty;
                ModuleLength = 0;
                Diagnostic = diagnostic;
            }
        }

        /// <summary>
        /// Получить весь текст модуля 1С через UI Automation TextPattern.
        /// Вызывать только после подтверждения пользователя.
        /// </summary>
        [ContextMethod("ПолучитьВесьМодуль1С", "GetOneCModuleFullText")]
        public string GetOneCModuleFullText(int targetHandle = 0, int targetProcessId = 0, string targetTitle = null)
        {
            _lastDiagnostic = "";
            var source = targetHandle == 0 ? null : new CaptureSource(new IntPtr(targetHandle), targetProcessId, targetTitle);

            string text = RunInUia("OneCFull", () => ReadOneCModuleAfterConfirmation(source));

            source?.Validate();
            return text ?? string.Empty;
        }

        private string ReadOneCModuleAfterConfirmation(CaptureSource source)
        {
            // Native activation can precede UIA focus after a modal confirmation.
            // Wait for the same source, never activate or read another window.
            for (int attempt = 0; attempt < 20; attempt++)
            {
                source?.Validate();
                try
                {
                    var text = ReadFocusedOneCModule(source);
                    if (!string.IsNullOrEmpty(text))
                    {
                        _lastDiagnostic = string.Empty;
                        return text;
                    }
                }
                catch (ElementNotAvailableException)
                {
                    _lastDiagnostic = "OneCFull: UIA-фокус ещё не восстановлен после диалога";
                }
                if (attempt < 19) Thread.Sleep(25);
            }
            return string.Empty;
        }

        private string ReadFocusedOneCModule(CaptureSource source)
        {
            var element = AutomationElement.FocusedElement;
            if (element == null || (source != null && !IsWithinAutomationRoot(AutomationElement.FromHandle(source.Handle), element)))
            {
                _lastDiagnostic = "OneCFull: фокусный UIA-элемент ещё не принадлежит исходному окну";
                return string.Empty;
            }
            if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern) || !(pattern is TextPattern text))
            {
                _lastDiagnostic = "OneCFull: фокусный элемент без TextPattern (не редактор модуля?)";
                return string.Empty;
            }
            var content = text.DocumentRange.GetText(-1) ?? string.Empty;
            if (content.Length == 0) _lastDiagnostic = "OneCFull: редактор вернул пустой модуль";
            return content;
        }

        [ContextMethod("ВставитьТекстВОкно", "InsertTextIntoWindow")]
        public IValue InsertTextIntoWindow(int targetHandle, string text, string mode)
        {
            return CreatePasteResult(new PasteExecutionResult().Fail(
                "Replace: требуется привязка исходника через CaptureReplacement"));
        }

        [ContextMethod("ОтправитьСообщениеВAIЧат", "SendAiChatMessage")]
        public IValue SendAiChatMessage(int targetHandle, int targetProcessId, string text)
        {
            return PasteTextIntoWindow(targetHandle, targetProcessId, text, "chat", true);
        }

        [ContextMethod("ВставитьЧерновикВAIЧат", "InsertAiChatDraft")]
        public IValue InsertAiChatDraft(int targetHandle, int targetProcessId, string text)
        {
            return PasteTextIntoWindow(targetHandle, targetProcessId, text, "chat", false);
        }

        [ContextMethod("ПолучитьТекстAIЧата", "GetAiChatText")]
        public IValue GetAiChatText(int targetHandle, int expectedProcessId, int maxCharacters = 24000)
        {
            var capture = RunInUia("AIChat UIA", () => CaptureAiChatText(
                new IntPtr(targetHandle), expectedProcessId, Math.Max(1000, Math.Min(maxCharacters, 100000))));
            if (capture == null)
            {
                capture = new AiChatTextCapture
                {
                    Diagnostic = string.IsNullOrEmpty(_lastDiagnostic)
                        ? "AIChat UIA: захват завершился с ошибкой"
                        : _lastDiagnostic
                };
            }
            if (!string.IsNullOrEmpty(capture.Diagnostic))
            {
                _lastDiagnostic = capture.Diagnostic;
            }

            var result = new StructureImpl();
            result.Insert("Успех", ValueFactory.Create(capture.Success));
            result.Insert("Текст", ValueFactory.Create(capture.Text ?? string.Empty));
            result.Insert(DiagnosticKey, ValueFactory.Create(capture.Diagnostic ?? string.Empty));
            result.Insert("СкрытоСекретов", ValueFactory.Create(capture.RedactionCount));
            return result;
        }

        private IValue PasteTextIntoWindow(int targetHandle, int expectedProcessId, string text, string mode,
            bool submitAfterPaste, Func<bool> validateSource = null)
        {
            PasteExecutionResult operationResult = null;
            var operation = Operations.Current;
            Func<bool> validate = () => operation?.Token.IsCancellationRequested != true
                && (validateSource == null || validateSource());
            try
            {
                operationResult = ClipboardDispatcher.Shared.Run(() =>
                {
                    if (operation?.Token.IsCancellationRequested == true
                        || (operation != null && !ReferenceEquals(Operations.Active, operation)))
                        return new PasteExecutionResult().Fail("операция вставки отменена до начала");
                    return ExecutePaste(targetHandle, expectedProcessId, text, mode, submitAfterPaste, validate);
                });
            }
            catch (Exception error) { operationResult = new PasteExecutionResult().Fail("Clipboard STA: " + error.GetType().Name); }
            if (operationResult == null)
            {
                operationResult = new PasteExecutionResult().Fail(
                    "операция вставки завершилась без результата");
            }

            return CreatePasteResult(operationResult);
        }

        internal IValue CreatePasteResult(PasteExecutionResult operationResult)
        {
            if (operationResult == null)
                operationResult = new PasteExecutionResult().Fail("операция вставки завершилась без результата");
            if (!string.IsNullOrEmpty(operationResult.Diagnostic))
            {
                _lastDiagnostic = operationResult.Diagnostic;
            }

            var r = new StructureImpl();
            r.Insert("Успех", ValueFactory.Create(operationResult.Success));
            r.Insert("БуферНеВосстановлен", ValueFactory.Create(operationResult.ClipboardRestoreFailed));
            r.Insert(DiagnosticKey, ValueFactory.Create(operationResult.Diagnostic));
            return r;
        }

        private static PasteExecutionResult ExecutePaste(
            int targetHandle, int expectedProcessId, string text, string mode, bool submitAfterPaste,
            Func<bool> validateSource)
        {
            return PasteCoordinator.Execute(
                new WindowPastePlatform(validateSource),
                targetHandle,
                expectedProcessId,
                text,
                mode,
                submitAfterPaste);
        }

        private static AiChatTextCapture CaptureAiChatText(
            IntPtr targetWindow, int expectedProcessId, int maxCharacters)
        {
            var result = new AiChatTextCapture();
            if (!NativeMethods.IsWindow(targetWindow))
            {
                result.Diagnostic = "AIChat UIA: целевое окно больше не существует";
                return result;
            }

            uint actualProcessId;
            NativeMethods.GetWindowThreadProcessId(targetWindow, out actualProcessId);
            if (expectedProcessId > 0 && actualProcessId != (uint)expectedProcessId)
            {
                result.Diagnostic = "AIChat UIA: окно теперь принадлежит другому процессу";
                return result;
            }

            var root = AutomationElement.FromHandle(targetWindow);
            if (root == null)
            {
                result.Diagnostic = "AIChat UIA: корневой элемент окна недоступен";
                return result;
            }

            return AiChatTextCaptureBuilder.Capture(
                new AutomationTextNode(root),
                (int)actualProcessId,
                root.Current.ProcessId,
                maxCharacters);
        }

        private sealed class AutomationTextNode : IAiChatTextNode
        {
            private readonly AutomationElement _element;

            public AutomationTextNode(AutomationElement element)
            {
                _element = element;
            }

            public int ProcessId => _element.Current.ProcessId;

            public AiChatNodeText ReadText(int maxCharacters)
            {
                var controlType = _element.Current.ControlType;
                var value = string.Empty;
                var capturedDocumentRange = false;
                if (controlType == ControlType.Document
                    && _element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)
                    && pattern is TextPattern textPattern)
                {
                    value = textPattern.DocumentRange.GetText(Math.Max(0, maxCharacters));
                    capturedDocumentRange = !string.IsNullOrWhiteSpace(value);
                }

                if (string.IsNullOrWhiteSpace(value)
                    && (controlType == ControlType.Text
                        || controlType == ControlType.Edit
                        || controlType == ControlType.ListItem
                        || controlType == ControlType.Document))
                {
                    value = _element.Current.Name;
                }

                return new AiChatNodeText(value, capturedDocumentRange);
            }

            public IReadOnlyList<IAiChatTextNode> GetChildren()
            {
                var result = new List<IAiChatTextNode>();
                var walker = TreeWalker.ControlViewWalker;
                for (var child = walker.GetFirstChild(_element);
                    child != null;
                    child = walker.GetNextSibling(child))
                {
                    result.Add(new AutomationTextNode(child));
                }

                return result;
            }
        }

        private static bool ValidatePasteTarget(
            IntPtr targetWindow, int expectedProcessId, string operation, PasteExecutionResult result)
        {
            if (!NativeMethods.IsWindow(targetWindow))
            {
                result.Fail(operation + " отменён: целевое окно больше не существует");
                return false;
            }

            uint actualProcessId;
            NativeMethods.GetWindowThreadProcessId(targetWindow, out actualProcessId);
            if (expectedProcessId > 0 && actualProcessId != (uint)expectedProcessId)
            {
                result.Fail(operation + " отменён: целевое окно теперь принадлежит другому процессу");
                return false;
            }

            return true;
        }

        private static bool FocusPasteTarget(
            IntPtr targetWindow, string operation, PasteExecutionResult result)
        {
            NativeMethods.SetForegroundWindow(targetWindow);
            for (int i = 0; i < 28 && NativeMethods.GetForegroundWindow() != targetWindow; i++)
            {
                ClipboardDispatcher.PumpWait(25);
            }

            if (NativeMethods.GetForegroundWindow() == targetWindow)
            {
                return true;
            }

            result.Fail(operation
                + " отменён: целевое окно не получило фокус "
                + "(вставка не выполнена во избежание попадания в чужое окно)");
            return false;
        }

        private static bool SendChatEnter(IntPtr targetWindow, PasteExecutionResult result)
        {
            if (NativeMethods.GetForegroundWindow() != targetWindow)
            {
                result.Fail("AIChat отменён: окно потеряло фокус перед отправкой");
                return false;
            }

            if (!SendEnter())
            {
                result.Fail("AIChat отменён: WinAPI не принял Enter");
                return false;
            }

            ClipboardDispatcher.PumpWait(150);
            return true;
        }

        private sealed class WindowPastePlatform : IPastePlatform, ITargetPasteActions
        {
            private readonly Func<bool> _validateSource;
            private uint _clipboardSequence;

            public WindowPastePlatform(Func<bool> validateSource)
            {
                _validateSource = validateSource;
            }

            public PasteClipboardSnapshot CaptureClipboard()
            {
                Trace.TraceInformation("zbs.paste: clipboard-capture");
                var snapshot = CaptureClipboardSnapshot();
                _clipboardSequence = GetClipboardSequence();
                Trace.TraceInformation("zbs.paste: clipboard-captured");
                return snapshot;
            }

            public uint GetClipboardSequence() => NativeMethods.GetClipboardSequenceNumber();

            public void SetClipboardText(string text)
            {
                Trace.TraceInformation("zbs.paste: clipboard-set");
                System.Windows.Forms.Clipboard.SetText(text);
                _clipboardSequence = GetClipboardSequence();
                Trace.TraceInformation("zbs.paste: clipboard-set-done");
            }

            public bool ValidateTarget(
                int targetHandle, int expectedProcessId, string operation, PasteExecutionResult result)
            {
                return ValidatePasteTarget(new IntPtr(targetHandle), expectedProcessId, operation, result);
            }

            public bool FocusTarget(int targetHandle, string operation, PasteExecutionResult result)
            {
                Trace.TraceInformation("zbs.paste: focus");
                return FocusPasteTarget(new IntPtr(targetHandle), operation, result);
            }

            public bool PasteText(
                int targetHandle,
                string text,
                string mode,
                string operation,
                PasteExecutionResult result)
            {
                Trace.TraceInformation("zbs.paste: input");
                return TargetPasteRouter.Paste(
                    this, targetHandle, text, mode, operation, result);
            }

            public bool TargetIsTerminal(int targetHandle)
            {
                var targetWindow = new IntPtr(targetHandle);
                var className = new StringBuilder(128);
                NativeMethods.GetClassName(targetWindow, className, className.Capacity);

                uint processId;
                NativeMethods.GetWindowThreadProcessId(targetWindow, out processId);
                var executable = string.Empty;
                try
                {
                    using (var process = Process.GetProcessById((int)processId))
                    {
                        executable = process.ProcessName + ".exe";
                    }
                }
                catch
                {
                    // Window class is sufficient for standard Windows terminal hosts.
                }

                return TerminalChatInput.IsTerminalWindow(executable, className.ToString());
            }

            public bool TargetIsFocused(int targetHandle) =>
                NativeMethods.GetForegroundWindow() == new IntPtr(targetHandle)
                && (_validateSource == null || _validateSource());

            public bool SelectAll() => SendCtrlA();

            public bool PasteClipboard() => GetClipboardSequence() == _clipboardSequence && SendCtrlV();

            public bool SendUnicode(string text)
            {
                var inputs = TerminalChatInput.BuildUnicodeInputs(text);
                return inputs.Length > 0
                    && NativeMethods.SendInput(
                        (uint)inputs.Length,
                        inputs,
                        Marshal.SizeOf(typeof(NativeMethods.Input))) == inputs.Length;
            }

            public void Pause(int milliseconds) =>
                ClipboardDispatcher.PumpWait(milliseconds);

            public bool SubmitChatMessage(int targetHandle, PasteExecutionResult result)
            {
                if (!TargetIsFocused(targetHandle))
                {
                    result.Fail("AIChat: отправка отменена или источник изменился");
                    return false;
                }
                return SendChatEnter(new IntPtr(targetHandle), result);
            }

            public void RestoreClipboard(PasteClipboardSnapshot snapshot)
            {
                Trace.TraceInformation("zbs.paste: clipboard-restore");
                RestoreClipboardSnapshot(snapshot, _clipboardSequence);
                Trace.TraceInformation("zbs.paste: clipboard-restored");
            }
        }

        /// <summary>
        /// Создать структуру с информацией об окне
        /// </summary>
        private static IValue CreateWindowInfoStruct(IntPtr hWnd)
        {
            // Получить заголовок
            StringBuilder title = new StringBuilder(256);
            NativeMethods.GetWindowText(hWnd, title, title.Capacity);

            // Получить класс
            StringBuilder className = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, className, className.Capacity);

            // Получить PID и имя процесса
            uint pid;
            NativeMethods.GetWindowThreadProcessId(hWnd, out pid);

            string exeName = "";
            try
            {
                Process process = Process.GetProcessById((int)pid);
                exeName = process.ProcessName + ".exe";
            }
            catch
            {
                exeName = "unknown.exe";
            }

            // Получить размеры окна
            NativeMethods.Rect rect;
            NativeMethods.GetWindowRect(hWnd, out rect);

            // Создать структуру для OneScript
            var result = new StructureImpl();
            result.Insert("exe", ValueFactory.Create(exeName));
            result.Insert("class", ValueFactory.Create(className.ToString()));
            result.Insert("title", ValueFactory.Create(title.ToString()));
            result.Insert("handle", ValueFactory.Create((int)hWnd));
            result.Insert("pid", ValueFactory.Create((int)pid));
            result.Insert("left", ValueFactory.Create(rect.Left));
            result.Insert("top", ValueFactory.Create(rect.Top));
            result.Insert("width", ValueFactory.Create(rect.Right - rect.Left));
            result.Insert("height", ValueFactory.Create(rect.Bottom - rect.Top));

            return result;
        }

        /// <summary>
        /// Попытка получить выделенный текст через UI Automation
        /// </summary>
        private string TryGetTextViaUIAutomation(IntPtr hWnd)
        {
            try
            {
                AutomationElement element = AutomationElement.FromHandle(hWnd);
                if (element == null)
                    return null;

                // Получить элемент в фокусе
                AutomationElement focusedElement = AutomationElement.FocusedElement;
                if (focusedElement == null || focusedElement.Current.IsPassword
                    || !IsWithinAutomationRoot(element, focusedElement))
                    return null;

                // Попробовать получить TextPattern
                object patternObj;
                if (focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out patternObj))
                {
                    TextPattern textPattern = patternObj as TextPattern;
                    if (textPattern != null)
                    {
                        var selections = textPattern.GetSelection();  // ← ИСПРАВЛЕНО
                        if (selections != null && selections.Length > 0)
                        {
                            return selections[0].GetText(-1);
                        }
                    }
                }


                // ValuePattern exposes the whole field, not the selection.
                // Never silently send an unselected document as a fallback.
            }
            catch (Exception ex)
            {
                _lastDiagnostic = "UIA: " + ex.GetType().Name + ": " + ex.Message;
            }

            return null;
        }

        /// <summary>
        /// Попытка получить выделенный текст через буфер обмена (Ctrl+C)
        /// </summary>
        // Снимок должен быть полным: если хотя бы один нативный формат нельзя
        // материализовать, операция через Clipboard не начинается. Иначе SetText
        // уничтожит данные, которые уже невозможно восстановить.
        private static PasteClipboardSnapshot CaptureClipboardSnapshot()
        {
            try
            {
                Trace.TraceInformation("zbs.clipboard: data-object");
                var src = System.Windows.Forms.Clipboard.GetDataObject();
                if (src == null)
                {
                    return PasteClipboardSnapshot.Empty();
                }

                Trace.TraceInformation("zbs.clipboard: formats");
                var formats = src.GetFormats(false);
                if (formats == null || formats.Length == 0)
                {
                    return PasteClipboardSnapshot.Empty();
                }

                var sequence = NativeMethods.GetClipboardSequenceNumber();
                var ids = Array.ConvertAll(formats, format => System.Windows.Forms.DataFormats.GetFormat(format).Id);
                return PasteClipboardSnapshot.Captured(NativeClipboardSnapshot.Capture(ids, sequence));
            }
            catch (Exception ex)
            {
                return PasteClipboardSnapshot.Failed(ex);
            }
        }

        private static void RestoreClipboardSnapshot(PasteClipboardSnapshot snapshot, uint expectedSequence = 0)
        {
            Trace.TraceInformation("zbs.clipboard: restore-start=" + expectedSequence);
            if (!snapshot.CanRestore)
            {
                throw new InvalidOperationException("clipboard snapshot недоступен");
            }

            if (snapshot.WasEmpty)
            {
                System.Windows.Forms.Clipboard.Clear();
            }
            else
            {
                if (snapshot.Data is NativeClipboardSnapshot native)
                    native.Restore(expectedSequence == 0 ? NativeMethods.GetClipboardSequenceNumber() : expectedSequence);
                else System.Windows.Forms.Clipboard.SetDataObject(snapshot.Data, true);
            }
            Trace.TraceInformation("zbs.clipboard: restore-done=" + NativeMethods.GetClipboardSequenceNumber());
        }

        private static bool IsWithinAutomationRoot(AutomationElement root, AutomationElement focused)
        {
            if (root == null) return false;
            var current = focused;
            for (int depth = 0; current != null && depth < 64; depth++)
            {
                if (root.Equals(current)) return true;
                current = TreeWalker.RawViewWalker.GetParent(current);
            }
            return false;
        }

        private string TryGetTextViaClipboard(CaptureSource source)
        {
            return RunInSta("Clipboard", () => CaptureSelectedTextFromClipboard(source));
        }

        private string CaptureSelectedTextFromClipboard(CaptureSource source)
        {
            source.Validate();
            var backup = CaptureClipboardSnapshot();
            if (!backup.CanRestore)
            {
                _lastDiagnostic = "clipboard capture отменён: не удалось полностью сохранить буфер ("
                    + backup.Error + ")";
                return null;
            }

            const string Sentinel = "\u0001ZBS_CAPTURE_SENTINEL\u0001";
            string selectedText = null;
            uint sentinelSequence = 0;
            uint capturedSequence = 0;
            try
            {
                source.Validate();
                System.Windows.Forms.Clipboard.SetText(Sentinel);
                sentinelSequence = NativeMethods.GetClipboardSequenceNumber();
                source.Validate();
                var copyAccepted = SendCtrlC();
                Trace.TraceInformation("zbs.capture: copy-input-accepted=" + copyAccepted);
                if (!copyAccepted)
                {
                    _lastDiagnostic = "Ctrl+C не принят Windows (проверьте уровень прав исходного окна)";
                    return null;
                }
                selectedText = PollClipboardText(Sentinel, source, out capturedSequence);
                source.Validate();

                if (string.IsNullOrEmpty(selectedText) && string.IsNullOrEmpty(_lastDiagnostic))
                {
                    _lastDiagnostic = "Ctrl+C не дал нового текста за отведенное время "
                        + "(пустое выделение, окно не реагирует, либо UIPI/elevation блокирует SendInput)";
                }
            }
            catch (Exception ex)
            {
                _lastDiagnostic = "clipboard capture: " + ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                try
                {
                    var restoreSequence = NativeMethods.GetClipboardSequenceNumber();
                    Trace.TraceInformation("zbs.capture: clipboard-sequences=" + sentinelSequence + ":" + capturedSequence + ":" + restoreSequence);
                    if (sentinelSequence != 0 && CaptureSource.ShouldRestoreClipboard(
                        sentinelSequence, capturedSequence, restoreSequence))
                        RestoreClipboardSnapshot(backup, restoreSequence);
                }
                catch (Exception ex)
                {
                    _lastDiagnostic += " | restore failed: " + ex.Message;
                }
                backup.Dispose();
            }

            return selectedText;
        }

        private static string PollClipboardText(string sentinel, CaptureSource source, out uint capturedSequence)
        {
            capturedSequence = 0;
            var attempts = string.Equals(TryGetProcessName(source.ProcessId), "WINWORD", StringComparison.OrdinalIgnoreCase) ? 80 : 20;
            string candidate = null;
            uint candidateSequence = 0;
            for (int i = 0; i < attempts; i++)
            {
                ClipboardDispatcher.PumpWait(25);
                source.Validate();
                try
                {
                    if (!System.Windows.Forms.Clipboard.ContainsText())
                    {
                        continue;
                    }

                    uint beforeRead = NativeMethods.GetClipboardSequenceNumber();
                    string current = System.Windows.Forms.Clipboard.GetText();
                    uint afterRead = NativeMethods.GetClipboardSequenceNumber();
                    if (beforeRead != afterRead) continue;
                    if (!string.IsNullOrEmpty(current) && current != sentinel)
                    {
                        if (candidate == current && candidateSequence == afterRead)
                        {
                            capturedSequence = afterRead;
                            return current;
                        }
                        candidate = current;
                        candidateSequence = afterRead;
                    }
                }
                catch
                {
                    // Clipboard ownership can change between polling attempts; retry until timeout.
                }
            }

            return null;
        }

        /// <summary>
        /// Отправить комбинацию Ctrl+C
        /// </summary>
        private static bool SendCtrlC()
        {
            NativeMethods.Input[] inputs = new NativeMethods.Input[4];

            // Нажать Ctrl
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = NativeMethods.VK_CONTROL;
            inputs[0].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYDOWN;

            // Нажать C
            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = NativeMethods.VK_C;
            inputs[1].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYDOWN;

            // Отпустить C
            inputs[2].type = NativeMethods.INPUT_KEYBOARD;
            inputs[2].u.ki.wVk = NativeMethods.VK_C;
            inputs[2].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

            // Отпустить Ctrl
            inputs[3].type = NativeMethods.INPUT_KEYBOARD;
            inputs[3].u.ki.wVk = NativeMethods.VK_CONTROL;
            inputs[3].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

            return NativeMethods.SendInput(4, inputs, Marshal.SizeOf(typeof(NativeMethods.Input))) == 4;
        }

        /// <summary>
        /// Отправить комбинацию Ctrl+V
        /// </summary>
        private static bool SendCtrlV()
        {
            NativeMethods.Input[] inputs = new NativeMethods.Input[4];

            // Нажать Ctrl
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = NativeMethods.VK_CONTROL;
            inputs[0].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYDOWN;

            // Нажать V
            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = NativeMethods.VK_V;
            inputs[1].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYDOWN;

            // Отпустить V
            inputs[2].type = NativeMethods.INPUT_KEYBOARD;
            inputs[2].u.ki.wVk = NativeMethods.VK_V;
            inputs[2].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

            // Отпустить Ctrl
            inputs[3].type = NativeMethods.INPUT_KEYBOARD;
            inputs[3].u.ki.wVk = NativeMethods.VK_CONTROL;
            inputs[3].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

            return NativeMethods.SendInput(4, inputs, Marshal.SizeOf(typeof(NativeMethods.Input))) == 4;
        }

        private static bool SendEnter()
        {
            NativeMethods.Input[] inputs = new NativeMethods.Input[2];

            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = NativeMethods.VK_RETURN;
            inputs[0].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYDOWN;

            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = NativeMethods.VK_RETURN;
            inputs[1].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

            return NativeMethods.SendInput(2, inputs, Marshal.SizeOf(typeof(NativeMethods.Input))) == 2;
        }

        /// <summary>
        /// Отправить комбинацию Ctrl+A
        /// </summary>
        private static bool SendCtrlA()
        {
            NativeMethods.Input[] inputs = new NativeMethods.Input[4];

            // Нажать Ctrl
            inputs[0].type = NativeMethods.INPUT_KEYBOARD;
            inputs[0].u.ki.wVk = NativeMethods.VK_CONTROL;
            inputs[0].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYDOWN;

            // Нажать A
            inputs[1].type = NativeMethods.INPUT_KEYBOARD;
            inputs[1].u.ki.wVk = NativeMethods.VK_A;
            inputs[1].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYDOWN;

            // Отпустить A
            inputs[2].type = NativeMethods.INPUT_KEYBOARD;
            inputs[2].u.ki.wVk = NativeMethods.VK_A;
            inputs[2].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

            // Отпустить Ctrl
            inputs[3].type = NativeMethods.INPUT_KEYBOARD;
            inputs[3].u.ki.wVk = NativeMethods.VK_CONTROL;
            inputs[3].u.ki.dwFlags = NativeMethods.KEYEVENTF_KEYUP;

            return NativeMethods.SendInput(4, inputs, Marshal.SizeOf(typeof(NativeMethods.Input))) == 4;
        }

        private static string Trunc(string s, int n)
        {
            if (s == null) return "<null>";
            s = s.Replace("\r", " ").Replace("\n", "\\n");
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        /// <summary>
        /// РАЗВЕДКА Phase 3: дамп UIA фокусного элемента активного окна
        /// (паттерны + попытки извлечь текст). Для проверки редактора
        /// модуля 1С Конфигуратора — каким API брать код.
        /// </summary>
        [ContextMethod("ДиагностикаUIA", "DiagnoseUia")]
        public string DiagnoseUia()
        {
            return RunInUia("DiagnoseUia", BuildUiaDiagnostic);
        }

        private static string BuildUiaDiagnostic()
        {
            var output = new StringBuilder();
            IntPtr window = NativeMethods.GetForegroundWindow();
            var title = new StringBuilder(256);
            var className = new StringBuilder(256);
            NativeMethods.GetWindowText(window, title, 256);
            NativeMethods.GetClassName(window, className, 256);
            uint processId;
            NativeMethods.GetWindowThreadProcessId(window, out processId);

            output.AppendLine("=== UIA DIAGNOSE ===");
            output.AppendLine("Window: exe=" + TryGetProcessName(processId)
                + " | class=" + className + " | title=" + title);

            var focusedElement = TryGetFocusedElement(output);
            if (focusedElement == null)
            {
                output.AppendLine("FocusedElement = null");
                return output.ToString();
            }

            AppendFocusedElementSummary(output, focusedElement);
            AppendSupportedPatterns(output, focusedElement);
            AppendTextPatternDiagnostic(output, focusedElement);
            AppendValuePatternDiagnostic(output, focusedElement);
            return output.ToString();
        }

        private static string TryGetProcessName(uint processId)
        {
            try
            {
                return Process.GetProcessById((int)processId).ProcessName;
            }
            catch
            {
                return "?";
            }
        }

        private static AutomationElement TryGetFocusedElement(StringBuilder output)
        {
            try
            {
                return AutomationElement.FocusedElement;
            }
            catch (Exception ex)
            {
                output.AppendLine("FocusedElement EX: " + ex.GetType().Name + " " + ex.Message);
                return null;
            }
        }

        private static void AppendFocusedElementSummary(
            StringBuilder output, AutomationElement focusedElement)
        {
            try
            {
                output.AppendLine("Focused.ControlType="
                    + focusedElement.Current.ControlType.ProgrammaticName
                    + " | ClassName=" + focusedElement.Current.ClassName
                    + " | Name=" + Trunc(focusedElement.Current.Name, 80));
            }
            catch (Exception ex)
            {
                output.AppendLine("Focused.Current EX: " + ex.Message);
            }
        }

        private static void AppendSupportedPatterns(
            StringBuilder output, AutomationElement focusedElement)
        {
            try
            {
                var names = new System.Collections.Generic.List<string>();
                foreach (var pattern in focusedElement.GetSupportedPatterns())
                {
                    names.Add(pattern.ProgrammaticName);
                }
                output.AppendLine("SupportedPatterns: " + string.Join(", ", names.ToArray()));
            }
            catch (Exception ex)
            {
                output.AppendLine("GetSupportedPatterns EX: " + ex.Message);
            }
        }

        private static void AppendTextPatternDiagnostic(
            StringBuilder output, AutomationElement focusedElement)
        {
            try
            {
                object pattern;
                if (!focusedElement.TryGetCurrentPattern(TextPattern.Pattern, out pattern))
                {
                    output.AppendLine("TextPattern: no");
                    return;
                }

                var textPattern = (TextPattern)pattern;
                output.AppendLine("TextPattern: YES");
                AppendTextSelection(output, textPattern);
                AppendDocumentRange(output, textPattern);
            }
            catch (Exception ex)
            {
                output.AppendLine("TextPattern probe EX: " + ex.Message);
            }
        }

        private static void AppendTextSelection(StringBuilder output, TextPattern textPattern)
        {
            try
            {
                var selections = textPattern.GetSelection();
                var count = selections == null ? "null" : selections.Length.ToString();
                var text = selections != null && selections.Length > 0
                    ? Trunc(selections[0].GetText(120), 120)
                    : "-";
                output.AppendLine("  GetSelection: count=" + count + " text=" + text);
            }
            catch (Exception ex)
            {
                output.AppendLine("  GetSelection EX: " + ex.Message);
            }
        }

        private static void AppendDocumentRange(StringBuilder output, TextPattern textPattern)
        {
            try
            {
                output.AppendLine("  DocumentRange.GetText(160)="
                    + Trunc(textPattern.DocumentRange.GetText(160), 160));
            }
            catch (Exception ex)
            {
                output.AppendLine("  DocumentRange EX: " + ex.Message);
            }
        }

        private static void AppendValuePatternDiagnostic(
            StringBuilder output, AutomationElement focusedElement)
        {
            try
            {
                object pattern;
                if (focusedElement.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
                {
                    output.AppendLine("ValuePattern: YES value="
                        + Trunc(((ValuePattern)pattern).Current.Value, 160));
                    return;
                }

                output.AppendLine("ValuePattern: no");
            }
            catch (Exception ex)
            {
                output.AppendLine("ValuePattern probe EX: " + ex.Message);
            }
        }
    }
}
