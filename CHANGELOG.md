# Changelog

## v0.5.0-beta.4 - Candidate, 2026-10-18

- MIT for own code; explicit privacy/security policy and dependency notices.
- Standalone verified OneScript runtime: no global OVM or external script packages.
- Native JSON settings maps and minimal own console diagnostics.
- Windows GitHub verification with pinned tools/actions and portable-package smoke.
- Reproducible component/helper binaries, retained cancellation and source checks.
- Temporary-file leases/retention, native result fallback and DPAPI emergency result.
- A successful paste is not reported failed merely because clipboard restore failed.
- Unsigned beta policy retained; checksums do not replace publisher authentication.

This section describes a candidate, not an already published GitHub release.

## Unreleased - Codex imagegen

- Контекст Image теперь может работать через локальный Codex CLI и подписку
  ChatGPT: встроенный `$imagegen` создаёт новый PNG через `gpt-image-2`.
- Microsoft Paint распознаётся как Image-контекст; перед генерацией Codex
  выделяет границы холста и исключает панели редактора.
- Промпт Image перестроен под сценарий «грубый набросок → фотореалистичная
  сцена» с сохранением композиции.
- Путь результата принимается только из свежего валидного PNG внутри
  `Codex/generated_images`, после чего приложение само сохраняет безопасную копию.

### P1 owner-smoke

- Excel foreground-выделение читается через `Selection.Value2` с проверкой
  PID/HWND и передаётся модели как TSV; больше не зависит от нестабильного
  Ctrl+C в Excel.
- Buffered OpenRouter переведён на системный WinHTTP: исправлены дублирование
  `/api/v1`, UTF-8 ответа, attribution URL и редактирование сетевых ошибок.
- `SM-13..SM-19` пройдены; добавлены доказательства Excel, Email, Browser,
  OpenRouter image-to-image, auth/fallback, privacy и автозапуска.

## [Unreleased]

### AI-чаты
- Добавлен контекст **AIChat** для Codex/ChatGPT, Claude и OpenCode, включая
  их CLI в поддерживаемых терминалах.
- Безопасный режим по умолчанию показывает настраиваемое продолжение и не
  обращается к OpenRouter. После подтверждения текст вставляется в исходный чат
  и отправляется Enter с проверкой HWND/PID и восстановлением буфера обмена.
- Opt-in режим **«Предложить следующий шаг»** анализирует снимок только
  активного AI-чата vision-моделью. Настройки явно предупреждают об отправке
  снимка в OpenRouter; промпт защищён от инструкций внутри содержимого чата.
- Добавлены отдельные профили Codex/Claude/OpenCode: process allowlist,
  профильные hotkey, текст продолжения и режим черновика без Enter.
- Следующий шаг по умолчанию получает текст локально через UI Automation и
  маскирует секреты до запроса; снимок сохранён как явный opt-in fallback.

### Настройки и поставка
- Введена `schemaVersion: 2` с валидацией, атомарной миграцией профилей и
  резервной копией старого `settings.json`.
- Общая горячая клавиша и сочетания AI-профилей теперь меняются захватом
  фактического нажатия, с отменой по `Esc` и нормализацией поддерживаемых клавиш.
- В настройках и метаданных сборок указаны автор Роман Андриянов, сайт
  `androman.pro` и личный Telegram `@andromanpro`; контакты получили аккуратный
  cyberpunk-glitch при наведении с поддержкой `prefers-reduced-motion`.
- Добавлены Windows CI для Gitea Actions и signed-release workflow:
  Authenticode EXE/DLL, manifest SHA-256, воспроизводимый ZIP и Gitea Release.

### Проверки
- Добавлены unit- и pipeline-тесты детекта AI-окон, прямого продолжения и
  vision-режима; GUI-отправка проверена в изолированном WinForms smoke-тесте.
- Добавлены NUnit/coverlet для C#, `node:test`/c8 и ESLint для JavaScript,
  secret scan и два SonarQube-прохода.

## [0.4.0] - 2026-07-08

Повторный полный аудит проекта (6 углов) после 0.3.0 вскрыл кластер
дефектов приватности и надёжности — закрыт в Phase 15 (PR #39).

### Приватность
- **`logging.enabled=false` — жёсткий OFF на ВСЕХ каналах** (ошибки и сессии,
  не только инфо; регресс 0.3.0 устранён). Модульный флаг `ЛогированиеВключено`
- **Ретеншн захваченного контента**: чистка logs/images, logs/backups и
  временных скриншотов (макс 20); удаление старейших логов по количеству
  (сортировка по дате — закрыт давний TODO)
- **`textSource.priority`**: fallback на буфер обмена только при
  `priority="clipboard"` — утилита не хватает чужой старый буфер (напр. пароль)
- Полный ответ модели больше не утекает в logos-канал в обход гейта

### Функциональность
- **Live-reload API-ключа**: смена ключа в Настройках подхватывается без
  перезапуска (клиент реинициализируется, не только модульная переменная)
- **Тумблер «Стрим»** реально выключает стрим (был плейсбо)
- Устранён мёртвый второй `УправлениеГорячимиКлавишами` — источник **двух
  иконок в трее** и двойного pre-warm WebView2 (~60 МБ). Настоящая причина;
  в 0.3.0 лечился симптом

### Надёжность
- **Стрим**: idle-таймаут (60с тишины сервера) вместо зависания навсегда;
  оборванный стрим больше не выдаётся за успех — уход в buffered fallback
- **Буфер обмена**: STA-маршалинг `ClipboardEx` (MTA-поток oscript ронял
  `Clipboard.*`); устранена гонка восстановления буфера при replace-in-place;
  закрыта утечка GDI при чтении картинки
- Settings-форма при сбое init не показывает модалку за кадром; трей не
  переиспользует битую форму
- Exit закрывает WebView2 gracefully (через `Dispose`)

### Гейты
- dotnet 0/0, oscript-регресс: pipeline 90 + logger 34; adversarial-ревью
  обоих диффов — регрессий нет

## [0.3.0] - 2026-06-09

Крупный этап: из консольного прототипа (`test`/`exit`, F13) — в трей-утилиту с
GUI, портируемым дистрибутивом и закрытым аудитом. Свод по фазам ROADMAP-v2 и
последующим:

### Ядро и захват
- Горячая клавиша **Ctrl+F12** реально работает (глобальный hotkey + HotkeyPoller); F13 из прототипа больше не используется
- Контексты доведены до 7: Code, **OneCCode** (1С Конфигуратор через UIA), Text, Word, **Email**, **Excel** (ячейки → TSV), Browser, Image
- Replace-in-place: кнопка «Применить» вставляет результат в исходное окно (opt-in, обязательный бэкап до замены)

### GUI (WinForms + WebView2)
- **Preview-окно на WebView2** с построчным diff и SSE-стримом «Ход мыслей» (транспорт стрима — C# `HttpWebRequest`, из oscript SSE недостижим)
- **image→image**: улучшение картинки vision-моделью; для скриншота окна — двухшаговый vision-bbox → детерминированный кроп
- **Настройки v2** на WebView2 (модели/промпты/токен в GUI); прогрев формы при старте → мгновенное открытие
- Тёмный заголовок окон (DWM), своя иконка (красная кнопка), тёмное меню трея

### Трей и упаковка
- **Быстрые тумблеры** в меню трея: автозапуск, replace, стрим, подтверждение модуля 1С
- Single-instance (named Mutex)
- **Портируемый дистрибутив** `dist/MakeItZBS/` (~58 МБ): нативный лаунчер `MakeItZBS.exe` + бандленный OneScript — конечному пользователю ставить ничего не нужно

### Приватность и надёжность
- `logging.enabled` — реальный hard-off; по умолчанию логирование **выключено** (raw prompt/response = данные пользователя)
- Autostart в portable чинит stale HKCU-запись даже при потерянном лаунчере
- Live-reload настроек, включая смену hotkey без перезапуска (с rollback)

### Аудит (Codex 2026-06-09, все закрыто)
- Deadlock в HotkeyManager (lock во время Invoke) → snapshot/Invoke-вне-lock/commit
- Docs INSTALL/TESTING приведены к 1.9.4/net472; live re-registration hotkey; logging privacy

## [0.1.0] - 2026-02-14

### Добавлено
- ✅ Интеграция с OpenRouter API
- ✅ Поддержка HTTPS через библиотеку connector (1connector)
- ✅ Консольный интерфейс с командами `test` и `exit`
- ✅ Определение 6 контекстов: Code, Text, Email, Browser, Image, Excel
- ✅ Настраиваемые AI модели для каждого контекста
- ✅ Настраиваемые промпты через JSON конфигурацию
- ✅ Полное логирование запросов и ответов
- ✅ ZBSWindowsComponent (.NET 4.7.2) с Windows API функциями
- ✅ Поддержка Vision API для изображений и скриншотов

### Исправлено
- ✅ Использование правильной функции консольного ввода (`Консоль.ПрочитатьСтроку()`)
- ✅ HTTPS запросы через КоннекторHTTP вместо встроенного HTTPСоединение
- ✅ Правильный путь API: `/api/v1/chat/completions`
- ✅ Корректная передача заголовков через ДополнительныеПараметры
- ✅ Обработка ответов connector через `.Текст()` метод

### Технические детали
- OneScript 1.9.4 (не 2.0!)
- .NET Framework 4.7.2
- Библиотеки: connector, json, logos, tempfiles

### В разработке
- ⚠️ Горячая клавиша F13 (требуется callback)
- ⚠️ GUI Preview окно
- ⚠️ Автоматическое применение результата
