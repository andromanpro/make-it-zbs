using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;




namespace ZBSWindowsComponent
{
    /// <summary>
    /// Polling-based hotkey обработчик для OneScript
    /// Альтернатива callback механизму
    /// </summary>
    [ContextClass("ОбработчикГорячихКлавиш", "HotkeyPoller")]
    public sealed class HotkeyPoller : AutoContext<HotkeyPoller>, IDisposable
    {
        private readonly HotkeyManager _hotkeyManager;
        private readonly HotkeyAdmissionQueue _eventQueue;
        private readonly List<string> _registeredKeys;

        [ScriptConstructor]
        public static HotkeyPoller Constructor()
        {
            return new HotkeyPoller();
        }

        public HotkeyPoller()
            : this(HotkeyManager.Constructor())
        {
        }

        internal HotkeyPoller(HotkeyManager hotkeyManager)
        {
            _hotkeyManager = hotkeyManager
                ?? throw new ArgumentNullException(nameof(hotkeyManager));
            _eventQueue = new HotkeyAdmissionQueue(ReadSourceIdentity);
            _registeredKeys = new List<string>();
        }

        /// <summary>
        /// Зарегистрировать горячую клавишу для polling
        /// </summary>
        [ContextMethod("Зарегистрировать", "Register")]
        public void Register(string keyString)
        {
            // Используем HotkeyManager, но с внутренним callback
            // который добавляет события в очередь
            var dummyCallback = new DummyCallback(this, keyString);
            _hotkeyManager.RegisterHotkey(keyString, dummyCallback);

            // _lastRegisteredKey ставим ТОЛЬКО ПОСЛЕ успеха RegisterHotkey
            // (Codex+DeepSeek contrast Phase 13): раньше ставился ДО, и при
            // провале регистрации _lastRegisteredKey указывал на
            // незарегистрированный ключ → рассинхрон с _hotkeys → следующий
            // Отменить() бросал "не найдена" и блокировал смену hotkey.
            _registeredKeys.Add(keyString);
        }

        /// <summary>
        /// Проверить, было ли нажатие (polling)
        /// </summary>
        [ContextMethod("ПроверитьНажатие", "CheckPressed")]
        public bool CheckPressed()
        {
            // Пытаемся извлечь событие из очереди
            string key;
            return _eventQueue.TryDequeue(out key);
        }

        /// <summary>
        /// Ожидать нажатие с таймаутом (блокирующий вызов)
        /// </summary>
        [ContextMethod("ОжидатьНажатие", "WaitForPress")]
        public bool WaitForPress(int timeoutMs = 1000)
        {
            var stopwatch = Stopwatch.StartNew();

            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (CheckPressed())
                {
                    return true;
                }

                System.Threading.Thread.Sleep(10);
            }

            return false;
        }

        [ContextMethod("ОжидатьКлавишу", "WaitForPressedKey")]
        public string WaitForPressedKey(int timeoutMs = 1000)
        {
            var stopwatch = Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                if (_eventQueue.TryDequeue(out var key))
                {
                    return key ?? string.Empty;
                }

                System.Threading.Thread.Sleep(10);
            }

            return string.Empty;
        }

        /// <summary>
        /// Получить количество ожидающих событий
        /// </summary>
        [ContextMethod("КоличествоСобытий", "GetEventCount")]
        public int GetEventCount()
        {
            return _eventQueue.Count;
        }

        /// <summary>
        /// Очистить очередь событий
        /// </summary>
        [ContextMethod("ОчиститьОчередь", "ClearQueue")]
        public void ClearQueue()
        {
            _eventQueue.Clear();
        }

        /// <summary>
        /// Отменить регистрацию
        /// </summary>
        [ContextMethod("Отменить", "Unregister")]
        public void Unregister()
        {
            foreach (var key in _registeredKeys.ToArray())
            {
                // Идемпотентность (Codex+DeepSeek contrast Phase 13): даже если
                // ключа уже нет в _hotkeys (рассинхрон после неудачного
                // re-register), состояние всё равно чистим — иначе следующий
                // цикл смены hotkey упал бы на этом вызове.
                try
                {
                    _hotkeyManager.UnregisterHotkey(key);
                }
                catch
                {
                    // ключ не был зарегистрирован — не критично
                }
            }

            _registeredKeys.Clear();
        }

        // Passthrough к внутреннему HotkeyManager: oscript держит Поллер,
        // а не HotkeyManager напрямую, поэтому делегируем эти методы.
        [ContextMethod("ПроверитьИСброситьФлагНастроек", "ConsumeSettingsDirty")]
        public bool ConsumeSettingsDirty()
        {
            return _hotkeyManager.ConsumeSettingsDirty();
        }

        [ContextMethod("ПометитьНастройкиИзмененными", "MarkSettingsDirty")]
        public void MarkSettingsDirty()
        {
            SettingsState.MarkDirty();
        }

        [ContextMethod("ДиагностикаЭлевации", "GetElevationDiagnostic")]
        public string GetElevationDiagnostic()
        {
            return _hotkeyManager.GetElevationDiagnostic();
        }

        [ContextMethod("СкрытьКонсоль", "HideConsole")]
        public void HideConsole()
        {
            _hotkeyManager.HideConsole();
        }

        [ContextMethod("ПоказатьПревью", "ShowPreview")]
        public string ShowPreview(string original, string result, string context, string model, int timeoutSec, bool allowApply, string notice = "")
        {
            return _hotkeyManager.ShowPreview(original, result, context, model, timeoutSec, allowApply, notice);
        }

        [ContextMethod("ПоказатьРезервныйРезультат", "ShowResultFallback")]
        public void ShowResultFallback(string result, string notice) => _hotkeyManager.ShowResultFallback(result, notice);

        [ContextMethod("ПоказатьИзображение", "ShowImagePreview")]
        public string ShowImagePreview(string imagePath, string context, string model, int timeoutSec)
        {
            return _hotkeyManager.ShowImagePreview(imagePath, context, model, timeoutSec);
        }

        [ContextMethod("ПоказатьПревьюСтрим", "ShowPreviewStreaming")]
        public IValue ShowPreviewStreaming(
            IRuntimeContextInstance request,
            string original,
            string context,
            string model,
            int timeoutSec,
            bool allowApply)
        {
            var options = StreamPreviewOptions.FromRuntimeContext(
                request, original, context, model, timeoutSec, allowApply);
            return _hotkeyManager.ShowPreviewStreaming(options);
        }

        [ContextMethod("ПоказатьИндикациюЗанят", "ShowBusyIndicator")]
        public void ShowBusyIndicator(string message)
        {
            _hotkeyManager.ShowBusyIndicator(message);
        }

        [ContextMethod("СброситьИндикацию", "ResetIndicator")]
        public void ResetIndicator(string message)
        {
            _hotkeyManager.ResetIndicator(message);
        }

        [ContextMethod("СкрытьИндикацию", "HideIndicator")]
        public void HideIndicator() => _hotkeyManager.HideIndicator();

        // Phase 3: подтверждение «обработать весь модуль» когда в
        // Конфигураторе нет выделения. STA-MessageBox живёт в
        // HotkeyManager (владеет message-loop окном), oscript держит
        // Поллер — поэтому делегируем как ShowPreview/индикацию.
        [ContextMethod("ПодтвердитьВесьМодуль", "ConfirmFullModule")]
        public bool ConfirmFullModule(int moduleLength)
        {
            return _hotkeyManager.ConfirmFullModule(moduleLength);
        }

        [ContextMethod("ПодтвердитьЗаменуВсегоМодуля", "ConfirmFullModuleReplace")]
        public bool ConfirmFullModuleReplace(int moduleLength, string backupPath)
        {
            return _hotkeyManager.ConfirmFullModuleReplace(moduleLength, backupPath);
        }

        /// <summary>
        /// Внутренний метод для добавления события в очередь
        /// </summary>
        internal void OnHotkeyPressed(string key)
        {
            if (Operations.IsBusy) return;
            var handle = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            if (pid == NativeMethods.GetCurrentProcessId())
            {
                _hotkeyManager.ResetIndicator(UiText.Current(
                    "Переключитесь в документ или редактор", "Switch to a document or editor"));
                return;
            }
            _eventQueue.Enqueue(key, Operations.IsBusy);
        }

        private static string ReadSourceIdentity()
        {
            var handle = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            var title = new StringBuilder(2048);
            NativeMethods.GetWindowText(handle, title, title.Capacity);
            return handle.ToInt64() + ":" + pid + ":" + title;
        }

        public void Dispose()
        {
            Unregister();
            _hotkeyManager?.Dispose();
        }

        /// <summary>
        /// Dummy callback для HotkeyManager
        /// </summary>
        private sealed class DummyCallback : IRuntimeContextInstance, IHotkeyEventSink
        {
            private readonly HotkeyPoller _poller;
            private readonly string _key;

            public DummyCallback(HotkeyPoller poller, string key)
            {
                _poller = poller;
                _key = key;
            }

            public bool IsIndexed => false;
            public bool DynamicMethodSignatures => false;

            public void Enqueue() => _poller.OnHotkeyPressed(_key);

            public IValue GetIndexedValue(IValue index)
            {
                throw new NotImplementedException();
            }

            public void SetIndexedValue(IValue index, IValue val)
            {
                throw new NotImplementedException();
            }

            public int FindProperty(string name)
            {
                return -1;
            }

            public bool IsPropReadable(int propNum)
            {
                return false;
            }

            public bool IsPropWritable(int propNum)
            {
                return false;
            }

            public IValue GetPropValue(int propNum)
            {
                throw new NotImplementedException();
            }

            public void SetPropValue(int propNum, IValue newVal)
            {
                throw new NotImplementedException();
            }

            public int FindMethod(string name)
            {
                return -1;
            }

            public MethodInfo GetMethodInfo(int methodNumber)
            {
                throw new NotImplementedException();
            }

            public void CallAsProcedure(int methodNumber, IValue[] arguments)
            {
                // Это вызывается при нажатии hotkey
                _poller.OnHotkeyPressed(_key);
            }

            public void CallAsFunction(int methodNumber, IValue[] arguments, out IValue retValue)
            {
                retValue = ValueFactory.Create();
            }
            public int GetPropCount()
            {
                return 0;
            }

            public string GetPropName(int propNum)
            {
                throw new NotImplementedException();
            }

            public int GetMethodsCount()
            {
                return 0;
            }
        }
    }
}
