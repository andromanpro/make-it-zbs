using System;
using System.Threading;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

namespace ZBSWindowsComponent
{
    /// <summary>
    /// Single-instance guard через именованный kernel-Mutex.
    /// </summary>
    [ContextClass("ЕдинственныйЭкземпляр", "SingleInstance")]
    public class SingleInstance : AutoContext<SingleInstance>
    {
        // Per-сессия (Local\), НЕ Global\: один экземпляр на логин-сессию
        // пользователя — для RDP/нескольких пользователей это правильно.
        private const string MutexName = @"Local\MakeItZBS-SingleInstance";
        private static readonly object MutexSync = new object();

        // СТАТИК и держим всё время жизни процесса: GC не финализирует,
        // мьютекс не освобождается явно. ОС сама закрывает kernel-handle
        // при ЛЮБОМ завершении процесса (graceful / Ctrl+C / taskkill /
        // краш) → именованный объект исчезает, следующий запуск получит
        // createdNew=true. Этим Mutex принципиально лучше lock-файла:
        // нет залипшего замка («призрака»), от которого мы и уходим.
        private static Mutex _mutex;
        private static bool _acquired;

        [ScriptConstructor]
        public static SingleInstance Constructor()
        {
            return new SingleInstance();
        }

        /// <summary>
        /// Истина — мы первый/единственный экземпляр (захватили).
        /// Ложь — другой экземпляр уже запущен (нужно тихо выйти).
        /// </summary>
        [ContextMethod("Захватить", "TryAcquire")]
        public bool TryAcquire()
        {
            lock (MutexSync)
            {
                return TryAcquireProcessMutex();
            }
        }

        private static bool TryAcquireProcessMutex()
        {
            try
            {
                if (_mutex != null)
                {
                    return _acquired;
                }

                bool createdNew;
                _mutex = new Mutex(true, MutexName, out createdNew);
                _acquired = createdNew;
                return _acquired;
            }
            catch (Exception)
            {
                // Fail-open: сбой Mutex (права/коллизия имени) НЕ должен
                // мешать единственному экземпляру стартовать. Лучше
                // редкий дубль, чем отказ запуска из-за глюка guard'а.
                return true;
            }
        }
    }
}
