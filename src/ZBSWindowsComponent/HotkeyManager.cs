using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Automation.Text;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

using ScriptEngine.HostedScript.Library;
using ZBSWindowsComponent.Helpers;

namespace ZBSWindowsComponent
{
    /// <summary>
    /// Управление глобальными горячими клавишами Windows
    /// </summary>
    [ContextClass("УправлениеГорячимиКлавишами", "HotkeyManager")]
    public sealed class HotkeyManager : AutoContext<HotkeyManager>, IDisposable
    {
        private const string CancelledOutcome = "cancelled";
        private HiddenWindow _window;
        private TrayApplicationContext _trayContext;
        private readonly Dictionary<int, HotkeyInfo> _hotkeys;
        private int _nextHotkeyId = 1;
        private bool _isRunning = false;
        private readonly object _lock = new object();
        private readonly Func<SettingsWebForm> _settingsFormFactory;
        // Сигнал готовности окна. HiddenWindow невидимое (SetVisibleCore=false),
        // поэтому handle НЕ создаётся через CreateControl/Application.Run —
        // его надо форсить явно и дождаться, иначе _window.Invoke() падает
        // с "Невозможно вызвать Invoke до создания дескриптора окна".
        private readonly ManualResetEventSlim _windowReady = new ManualResetEventSlim(false);

        [ScriptConstructor]
        public static HotkeyManager Constructor()
        {
            return new HotkeyManager();
        }

        public HotkeyManager()
            : this(() => new SettingsWebForm())
        {
        }

        internal HotkeyManager(Func<SettingsWebForm> settingsFormFactory)
        {
            _settingsFormFactory = settingsFormFactory
                ?? throw new ArgumentNullException(nameof(settingsFormFactory));
            _hotkeys = new Dictionary<int, HotkeyInfo>();
            WindowsAppIdentity.TryInitializeForCurrentProcess();
            StartMessageLoop();
        }

        internal bool IsMessageLoopReady => _windowReady.IsSet;

        internal bool IsRunning => _isRunning;

        /// <summary>
        /// Запуск потока обработки сообщений Windows
        /// </summary>
        private void StartMessageLoop()
        {
            if (_isRunning)
                return;

            _isRunning = true;

            var messageLoopThread = new Thread(() =>
            {
                DpiAwareness.TryEnablePerMonitorV2ForCurrentThread();
                _window = new HiddenWindow(this);
                // Доступ к .Handle синхронно создаёт HWND на ЭТОМ STA-потоке
                // даже для невидимой формы (CreateControl для невидимой Form
                // дескриптор не создаёт). Только после этого Invoke валиден.
                var forceHandle = _window.Handle;
                GC.KeepAlive(forceHandle);
                _windowReady.Set();
                WindowsAppIdentity.TryEnsureShortcut();
                // Phase 11 pre-warm: вместо лёгкого Environment-only (Phase 10)
                // — скрытая SettingsWebForm с полной инициализацией WebView2
                // и Navigate(settings.html). settings.js идемпотентен (render
                // на каждое webMessage), форма Hide вместо Dispose. Открытие
                // из трея = Show() уже готовой → instant. Цена: ~30MB RAM +
                // ~300-1500ms init фоном (юзер не видит). BeginPrewarm
                // вешает EnsureCoreWebView2Async в очередь STA через async/await
                // — Application.Run ниже пампит и выполняет.
                var settingsForm = _settingsFormFactory();
                if (settingsForm != null)
                {
                    try
                    {
                        settingsForm.BeginPrewarm();
                    }
                    catch (Exception ex)
                    {
                        // Fail-open: trayContext получит .IsDisposed форму или
                        // pre-warm-провал → fallback на старое поведение
                        // (new SettingsWebForm + ShowDialog) в SettingsMenuItem_Click.
                        Console.WriteLine("[zbs.warmup] Settings pre-warm failed: " + ex.Message);
                    }
                }
                // Run-root — ApplicationContext (трей), НЕ _window. WM_HOTKEY
                // продолжает доходить до _window, потому что message pump един
                // на STA-поток (качает сообщения всех окон потока), а не привязан
                // к run-root. Инвариант хрупкий: НЕ прерывать loop в обход
                // ExitThread() (см. Dispose) — иначе hotkey-окно молча умрёт.
                _trayContext = new TrayApplicationContext(null, settingsForm);
                Application.Run(_trayContext);
            });

            messageLoopThread.SetApartmentState(ApartmentState.STA);
            messageLoopThread.IsBackground = true;
            messageLoopThread.Start();

            // Ждём РЕАЛЬНОЙ готовности (handle создан), не просто != null
            int timeout = 5000;
            if (!_windowReady.Wait(timeout))
            {
                throw new RuntimeException(
                    "Не удалось создать окно для обработки hotkeys: дескриптор не создан за " + timeout + " мс");
            }
        }

        /// <summary>
        /// Установить обработчик для горячей клавиши
        /// </summary>
        /// <param name="keyString">Строка клавиши (например "F13" или "Ctrl+Shift+F13")</param>
        /// <param name="callback">Делегат для вызова при нажатии</param>
        [ContextMethod("УстановитьОбработчик", "RegisterHotkey")]
        public void RegisterHotkey(string keyString, IRuntimeContextInstance callback)
        {
            if (string.IsNullOrWhiteSpace(keyString))
            {
                throw new RuntimeException("Строка клавиши не может быть пустой");
            }

            if (callback == null)
            {
                throw new RuntimeException("Callback не может быть Неопределено");
            }

            // Парсинг строки клавиши
            var (modifiers, vkCode) = KeyConverter.Parse(keyString);

            // Codex audit P1: РАНЬШЕ _lock держался во время _window.Invoke
            // (Win32 на STA). Если в этот момент STA обрабатывал WM_HOTKEY →
            // OnHotkeyPressed брал _lock → взаимная блокировка (MTA ждёт STA
            // в Invoke, STA ждёт _lock). Стало почти безопасно (регистрация
            // только при старте), но live re-registration (Phase 13.2) делает
            // путь ГОРЯЧИМ. Паттерн: snapshot/prepare под lock → Win32 Invoke
            // ВНЕ lock → commit под lock.
            int hotkeyId;
            lock (_lock)
            {
                // Проверить, не зарегистрирована ли уже такая комбинация
                foreach (var existing in _hotkeys.Values)
                {
                    if (existing.Modifiers == modifiers && existing.VkCode == vkCode)
                    {
                        throw new RuntimeException($"Горячая клавиша '{keyString}' уже зарегистрирована");
                    }
                }

                // Defense-in-depth: понятная ошибка вместо системного
                // InvalidOperationException, если handle всё же не готов.
                if (_window == null || !_window.IsHandleCreated)
                {
                    throw new RuntimeException(
                        "Окно hotkey не готово (дескриптор не создан) — регистрация невозможна");
                }

                hotkeyId = _nextHotkeyId++;
            }

            // Регистрация через Win32 API в потоке окна — ВНЕ lock.
            // errorCode захватываем ВНУТРИ делегата (на STA): GetLastError
            // thread-local, после возврата Invoke на MTA-поток он вернёт чужую
            // ошибку (латентный баг оригинала — заодно фикс).
            bool success = false;
            int errorCode = 0;
            _window.Invoke(new Action(() =>
            {
                success = NativeMethods.RegisterHotKey(
                    _window.Handle,
                    hotkeyId,
                    modifiers,
                    vkCode
                );
                if (!success)
                {
                    errorCode = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                }
            }));

            if (!success)
            {
                throw new RuntimeException($"Не удалось зарегистрировать hotkey '{keyString}'. Код ошибки: {errorCode}. Возможно, клавиша уже используется другим приложением.");
            }

            // Сохранить информацию — commit под lock.
            var hotkeyInfo = new HotkeyInfo
            {
                Id = hotkeyId,
                KeyString = keyString,
                Modifiers = modifiers,
                VkCode = vkCode,
                Callback = callback
            };

            lock (_lock)
            {
                _hotkeys[hotkeyId] = hotkeyInfo;
            }
        }

        /// <summary>
        /// Удалить обработчик горячей клавиши
        /// </summary>
        /// <param name="keyString">Строка клавиши</param>
        [ContextMethod("УдалитьОбработчик", "UnregisterHotkey")]
        public void UnregisterHotkey(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString))
            {
                throw new RuntimeException("Строка клавиши не может быть пустой");
            }

            var (modifiers, vkCode) = KeyConverter.Parse(keyString);

            // snapshot под lock (см. RegisterHotkey — deadlock fix Phase 13.3)
            HotkeyInfo hotkeyToRemove = null;
            lock (_lock)
            {
                foreach (var hotkey in _hotkeys.Values)
                {
                    if (hotkey.Modifiers == modifiers && hotkey.VkCode == vkCode)
                    {
                        hotkeyToRemove = hotkey;
                        break;
                    }
                }

                if (hotkeyToRemove == null)
                {
                    throw new RuntimeException($"Горячая клавиша '{keyString}' не найдена");
                }
            }

            // Win32 ВНЕ lock
            if (_window != null && _window.IsHandleCreated)
            {
                _window.Invoke(new Action(() =>
                {
                    NativeMethods.UnregisterHotKey(_window.Handle, hotkeyToRemove.Id);
                }));
            }

            // commit под lock
            lock (_lock)
            {
                _hotkeys.Remove(hotkeyToRemove.Id);
            }
        }

        /// <summary>
        /// Остановить прослушивание всех горячих клавиш
        /// </summary>
        [ContextMethod("ОстановитьПрослушивание", "StopListening")]
        public void StopListening()
        {
            // snapshot ids под lock → Win32 вне lock → clear под lock
            // (deadlock fix Phase 13.3: см. RegisterHotkey).
            int[] ids;
            lock (_lock)
            {
                ids = new int[_hotkeys.Count];
                _hotkeys.Keys.CopyTo(ids, 0);
            }

            if (_window != null && _window.IsHandleCreated && ids.Length > 0)
            {
                _window.Invoke(new Action(() =>
                {
                    foreach (var hotkeyId in ids)
                    {
                        NativeMethods.UnregisterHotKey(_window.Handle, hotkeyId);
                    }
                }));
            }

            lock (_lock)
            {
                _hotkeys.Clear();
            }
        }

        /// <summary>
        /// Получить список зарегистрированных горячих клавиш
        /// </summary>
        [ContextMethod("ПолучитьСписокКлавиш", "GetRegisteredHotkeys")]
        public ArrayImpl GetRegisteredHotkeys()
        {
            var result = new ArrayImpl();

            lock (_lock)
            {
                foreach (var hotkey in _hotkeys.Values)
                {
                    result.Add(ValueFactory.Create(hotkey.KeyString));
                }
            }

            return result;
        }

        /// <summary>
        /// Проверить, зарегистрирована ли клавиша
        /// </summary>
        [ContextMethod("КлавишаЗарегистрирована", "IsHotkeyRegistered")]
        public bool IsHotkeyRegistered(string keyString)
        {
            if (string.IsNullOrWhiteSpace(keyString))
            {
                return false;
            }

            try
            {
                var (modifiers, vkCode) = KeyConverter.Parse(keyString);

                lock (_lock)
                {
                    foreach (var hotkey in _hotkeys.Values)
                    {
                        if (hotkey.Modifiers == modifiers && hotkey.VkCode == vkCode)
                        {
                            return true;
                        }
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// Скрыть консольное окно текущего процесса.
        /// </summary>
        [ContextMethod("СкрытьКонсоль", "HideConsole")]
        public void HideConsole()
        {
            var hWnd = NativeMethods.GetConsoleWindow();
            if (hWnd != IntPtr.Zero)
            {
                NativeMethods.ShowWindow(hWnd, NativeMethods.SW_HIDE);
            }
        }

        [ContextMethod("ПроверитьИСброситьФлагНастроек", "ConsumeSettingsDirty")]
        public bool ConsumeSettingsDirty()
        {
            return SettingsState.ConsumeDirty();
        }

        [ContextMethod("ПоказатьПревью", "ShowPreview")]
        public string ShowPreview(string original, string result, string context, string model, int timeoutSec, bool allowApply, string notice = "")
        {
            var operation = Operations.Current;
            var outcome = CancelledOutcome;

            try
            {
                if (_window == null || !_window.IsHandleCreated)
                {
                    return _isRunning ? "error" : CancelledOutcome;
                }

                Action showPreview = () =>
                {
                    using (var form = new PreviewForm(original, result, context, model, timeoutSec, allowApply, notice))
                    {
                        form.BindOperation(operation);
                        form.ShowDialog();
                        outcome = form.Outcome;
                    }
                };

                if (_window.InvokeRequired)
                {
                    _window.Invoke(showPreview);
                }
                else
                {
                    showPreview();
                }
            }
            catch
            {
                outcome = _isRunning && operation?.Token.IsCancellationRequested != true ? "error" : CancelledOutcome;
            }

            return outcome;
        }

        public void ShowResultFallback(string result, string notice)
        {
            var operation = Operations.Current;
            operation?.Token.ThrowIfCancellationRequested();
            if (_window == null || !_window.IsHandleCreated)
                throw new InvalidOperationException("The result window is unavailable.");
            Action show = () =>
            {
                operation?.Token.ThrowIfCancellationRequested();
                using (var form = new ResultFallbackForm(result, notice, operation)) form.ShowDialog();
            };
            if (_window.InvokeRequired) _window.Invoke(show);
            else show();
        }

        [ContextMethod("ПоказатьИзображение", "ShowImagePreview")]
        public string ShowImagePreview(string imagePath, string context, string model, int timeoutSec)
        {
            var operation = Operations.Current;
            var outcome = CancelledOutcome;

            try
            {
                if (_window == null || !_window.IsHandleCreated)
                {
                    return outcome;
                }

                Action showImagePreview = () =>
                {
                    using (var form = new ImagePreviewForm(imagePath, context, model, timeoutSec))
                    {
                        form.BindOperation(operation);
                        form.ShowDialog();
                        outcome = form.Outcome;
                    }
                };

                if (_window.InvokeRequired)
                {
                    _window.Invoke(showImagePreview);
                }
                else
                {
                    showImagePreview();
                }
            }
            catch
            {
                outcome = CancelledOutcome;
            }

            return outcome;
        }

        internal IValue ShowPreviewStreaming(StreamPreviewOptions options)
        {
            var operation = Operations.Current;
            var outcome = CancelledOutcome;
            var content = string.Empty;
            var error = string.Empty;

            try
            {
                if (_window == null || !_window.IsHandleCreated)
                {
                    error = "окно не готово";
                    return BuildStreamingPreviewResult(outcome, content, error);
                }

                Action showPreview = () =>
                {
                    using (var form = PreviewForm.ДляСтрима(options))
                    {
                        form.BindOperation(operation);
                        form.ShowDialog();
                        outcome = form.Outcome;
                        content = form.StreamedContent;
                        error = form.StreamError;
                    }
                };

                if (_window.InvokeRequired)
                {
                    _window.Invoke(showPreview);
                }
                else
                {
                    showPreview();
                }
            }
            catch (Exception ex)
            {
                outcome = CancelledOutcome;
                content = string.Empty;
                error = ex.Message;
            }

            return BuildStreamingPreviewResult(outcome, content, error);
        }

        private static StructureImpl BuildStreamingPreviewResult(string outcome, string content, string error)
        {
            var r = new StructureImpl();
            r.Insert("Исход", ValueFactory.Create(outcome ?? CancelledOutcome));
            r.Insert("Контент", ValueFactory.Create(content ?? string.Empty));
            r.Insert("ОшибкаСтрима", ValueFactory.Create(error ?? string.Empty));
            return r;
        }

        [ContextMethod("ПодтвердитьВесьМодуль", "ConfirmFullModule")]
        public bool ConfirmFullModule(int moduleLength)
        {
            bool confirmed = false;

            try
            {
                if (_window == null || !_window.IsHandleCreated)
                {
                    return false;
                }

                Action ask = () =>
                {
                    string language = UiText.CurrentLanguage();
                    var r = MessageBox.Show(
                        UiText.Get(language,
                            "Выделенный фрагмент не найден.\n\nОбработать весь модуль целиком (~" + moduleLength + " символов)?",
                            "No selected fragment was found.\n\nProcess the entire module (~" + moduleLength + " characters)?"),
                        "Make it ZBS",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question,
                        MessageBoxDefaultButton.Button2);
                    confirmed = (r == DialogResult.Yes);
                };

                if (_window.InvokeRequired)
                {
                    _window.Invoke(ask);
                }
                else
                {
                    ask();
                }
            }
            catch
            {
                confirmed = false;
            }

            return confirmed;
        }

        [ContextMethod("ПодтвердитьЗаменуВсегоМодуля", "ConfirmFullModuleReplace")]
        public bool ConfirmFullModuleReplace(int moduleLength, string backupPath)
        {
            bool confirmed = false;

            try
            {
                if (_window == null || !_window.IsHandleCreated)
                {
                    return false;
                }

                Action ask = () =>
                {
                    string language = UiText.CurrentLanguage();
                    var r = MessageBox.Show(
                        UiText.Get(language,
                            "ВНИМАНИЕ: будет ПОЛНОСТЬЮ заменён весь модуль (~" + moduleLength + " символов) в активном окне.\n\nРезервная копия оригинала:\n" + backupPath + "\n\nЗаменить весь модуль?",
                            "WARNING: the entire module (~" + moduleLength + " characters) in the active window will be replaced.\n\nOriginal backup:\n" + backupPath + "\n\nReplace the entire module?"),
                        UiText.Get(language, "Make it ZBS - замена всего модуля", "Make it ZBS - replace entire module"),
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning,
                        MessageBoxDefaultButton.Button2);
                    confirmed = (r == DialogResult.Yes);
                };

                if (_window.InvokeRequired)
                {
                    _window.Invoke(ask);
                }
                else
                {
                    ask();
                }
            }
            catch
            {
                confirmed = false;
            }

            return confirmed;
        }

        [ContextMethod("ПоказатьИндикациюЗанят", "ShowBusyIndicator")]
        public void ShowBusyIndicator(string message)
        {
            try
            {
                if (_window == null || !_window.IsHandleCreated)
                {
                    return;
                }

                Action showBusy = () =>
                {
                    if (_trayContext != null)
                    {
                        _trayContext.ShowBusy(message);
                    }
                };

                if (_window.InvokeRequired)
                {
                    _window.Invoke(showBusy);
                }
                else
                {
                    showBusy();
                }
            }
            catch
            {
                // Busy indication is best effort; processing continues without tray feedback.
            }
        }

        [ContextMethod("СброситьИндикацию", "ResetIndicator")]
        public void ResetIndicator(string message)
        {
            try
            {
                if (_window == null || !_window.IsHandleCreated)
                {
                    return;
                }

                Action resetReady = () =>
                {
                    if (_trayContext != null)
                    {
                        _trayContext.ResetReady(message);
                    }
                };

                if (_window.InvokeRequired)
                {
                    _window.Invoke(resetReady);
                }
                else
                {
                    resetReady();
                }
            }
            catch
            {
                // Ready indication is best effort during shutdown or a disposed tray window.
            }
        }

        [ContextMethod("СкрытьИндикацию", "HideIndicator")]
        public void HideIndicator()
        {
            if (_window == null || !_window.IsHandleCreated) return;
            Action hide = () => _trayContext?.HideFeedback();
            if (_window.InvokeRequired) _window.Invoke(hide);
            else hide();
        }

        [ContextMethod("ДиагностикаЭлевации", "GetElevationDiagnostic")]
        public string GetElevationDiagnostic()
        {
            try
            {
                bool currentProcessElevated;
                if (!TryGetProcessElevation(NativeMethods.GetCurrentProcess(), out currentProcessElevated))
                {
                    return string.Empty;
                }

                var foregroundWindow = NativeMethods.GetForegroundWindow();
                if (foregroundWindow == IntPtr.Zero)
                {
                    return string.Empty;
                }

                uint foregroundProcessId;
                NativeMethods.GetWindowThreadProcessId(foregroundWindow, out foregroundProcessId);
                if (foregroundProcessId == 0)
                {
                    return string.Empty;
                }

                bool foregroundProcessElevated;
                if (!TryGetProcessElevation(foregroundProcessId, out foregroundProcessElevated))
                {
                    return string.Empty;
                }

                if (foregroundProcessElevated && !currentProcessElevated)
                {
                    return "Активное окно запущено от администратора, а Make it ZBS — нет. Запустите Make it ZBS от имени администратора, иначе захват/вставка в это окно не сработают.";
                }
            }
            catch
            {
                return string.Empty;
            }

            return string.Empty;
        }

        // Открывает СВОЙ handle процесса по pid и сам его закрывает (finally).
        private static bool TryGetProcessElevation(uint processId, out bool isElevated)
        {
            isElevated = false;

            var processHandle = NativeMethods.OpenProcess(
                NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION,
                false,
                processId);

            if (processHandle == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                return TryGetProcessElevation(processHandle, out isElevated);
            }
            finally
            {
                NativeMethods.CloseHandle(processHandle);
            }
        }

        // processHandle принадлежит вызывающему (closes сам или псевдо-handle
        // GetCurrentProcess закрывать не надо). Здесь закрываем только token.
        private static bool TryGetProcessElevation(IntPtr processHandle, out bool isElevated)
        {
            isElevated = false;
            var tokenHandle = IntPtr.Zero;

            try
            {
                if (!NativeMethods.OpenProcessToken(processHandle, NativeMethods.TOKEN_QUERY, out tokenHandle))
                {
                    return false;
                }

                NativeMethods.TokenElevationInfo elevation;
                int elevationSize = Marshal.SizeOf(typeof(NativeMethods.TokenElevationInfo));
                int returnedSize;

                if (!NativeMethods.GetTokenInformation(
                    tokenHandle,
                    NativeMethods.TokenElevation,
                    out elevation,
                    elevationSize,
                    out returnedSize))
                {
                    return false;
                }

                isElevated = elevation.TokenIsElevated != 0;
                return true;
            }
            finally
            {
                if (tokenHandle != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(tokenHandle);
                }
            }
        }

        /// <summary>
        /// Обработка нажатия горячей клавиши (вызывается из HiddenWindow)
        /// </summary>
        internal void OnHotkeyPressed(int hotkeyId)
        {
            HotkeyInfo hotkey;

            lock (_lock)
            {
                if (!_hotkeys.TryGetValue(hotkeyId, out hotkey))
                {
                    return;
                }
            }

            // Polling callbacks only enqueue. Do not let delayed thread-pool callbacks replay old presses.
            if (hotkey.Callback is IHotkeyEventSink sink)
            {
                sink.Enqueue();
                return;
            }

            // Вызвать callback в отдельном потоке, чтобы не блокировать message loop
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    // Вызов метода OneScript через IRuntimeContextInstance
                    var callback = hotkey.Callback;
                    if (callback != null)
                    {
                        try
                        {
                            // Найти метод по имени или использовать метод по умолчанию
                            int methodIndex = callback.FindMethod("Call");
                            if (methodIndex == -1)
                            {
                                // Если нет метода Call, попробовать первый доступный метод
                                methodIndex = 0;
                            }

                            // Вызвать как процедуру без параметров
                            callback.CallAsProcedure(methodIndex, new IValue[0]);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"Ошибка при вызове callback для {hotkey.KeyString}: {ex.Message}");
                        }
                    }

                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при вызове callback для {hotkey.KeyString}: {ex.Message}");
                }
            });
        }

        public void Dispose()
        {
            StopListening();

            if (_window != null && _window.IsHandleCreated)
            {
                _window.Invoke(new Action(() =>
                {
                    if (_trayContext != null)
                    {
                        _trayContext.Dispose();   // снять иконку трея

                        // Run-root — ApplicationContext, поэтому _window.Close()
                        // НЕ завершает Application.Run(_trayContext). Без явного
                        // ExitThread() STA-поток навсегда останется в message
                        // loop (утечка потока). ExitThread даёт Run вернуться.
                        _trayContext.ExitThread();
                    }

                    _window.Close();
                }));
            }

            _isRunning = false;
        }

        /// <summary>
        /// Информация о зарегистрированной горячей клавише
        /// </summary>
        private sealed class HotkeyInfo
        {
            public int Id { get; set; }
            public string KeyString { get; set; }
            public uint Modifiers { get; set; }
            public uint VkCode { get; set; }
            public IRuntimeContextInstance Callback { get; set; }
        }

        /// <summary>
        /// Скрытое окно для получения сообщений Windows
        /// </summary>
        private sealed class HiddenWindow : Form
        {
            private readonly HotkeyManager _manager;

            public HiddenWindow(HotkeyManager manager)
            {
                _manager = manager;

                // Настройка скрытого окна
                this.ShowInTaskbar = false;
                this.FormBorderStyle = FormBorderStyle.None;
                this.WindowState = FormWindowState.Minimized;
                this.Size = new System.Drawing.Size(0, 0);
                this.Opacity = 0;
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == NativeMethods.WM_HOTKEY)
                {
                    int hotkeyId = m.WParam.ToInt32();
                    _manager.OnHotkeyPressed(hotkeyId);
                }

                base.WndProc(ref m);
            }

            protected override void SetVisibleCore(bool value)
            {
                // Окно всегда невидимое
                base.SetVisibleCore(false);
            }
        }
    }
}
