(function (root, factory) {
    "use strict";

    const translations = factory();
    if (root) {
        root.ZbsI18n = translations;
    }
    if (typeof module === "object" && module.exports) {
        module.exports = translations;
    }
}(typeof window === "undefined" ? globalThis : window, function () {
    "use strict";

    const pairs = {
        "settings.title": ["Настройки", "Settings"],
        "settings.sections": ["Разделы настроек", "Settings sections"],
        "general.title": ["Общие", "General"],
        "general.hotkey": ["Горячая клавиша", "Keyboard shortcut"],
        "hotkey.change": ["Изменить", "Change"],
        "hotkey.press": ["Нажмите новое сочетание", "Press the new shortcut"],
        "hotkey.unsupported": ["Эта клавиша не поддерживается", "This key is not supported"],
        "general.language": ["Язык интерфейса", "Interface language"],
        "general.background": ["Фоновый режим", "Run in background"],
        "general.autostart": ["Автозапуск Windows", "Start with Windows"],
        "general.logging": ["Технический журнал без содержимого", "Content-free diagnostic log"],
        "general.openLogs": ["Открыть журнал", "Open log folder"],
        "general.operationFeedback": ["Индикация работы ИИ", "AI activity indicator"],
        "feedback.both": ["Иконка и плашка", "Tray icon and popup"],
        "feedback.icon": ["Только иконка", "Tray icon only"],
        "feedback.popup": ["Только плашка", "Popup only"],
        "feedback.none": ["Выключено", "Off"],
        "providers.title": ["Провайдеры ИИ", "AI providers"],
        "editor.connect": ["Подключить", "Connect"],
        "editor.update": ["Обновить", "Update"],
        "editor.installing": ["Установка...", "Installing..."],
        "editor.missing": ["VS Code не найден", "VS Code not found"],
        "editor.required": ["Адаптер не установлен", "Adapter not installed"],
        "editor.updateRequired": ["Доступно обновление адаптера", "Adapter update available"],
        "editor.installed": ["Адаптер установлен", "Adapter installed"],
        "editor.reload": ["Требуется перезапуск окна VS Code", "VS Code window reload required"],
        "editor.failed": ["Не удалось подключить адаптер", "Could not connect the adapter"],
        "editor.check-failed": ["Не удалось проверить адаптер", "Could not check the adapter"],
        "editor.install-failed": ["Установка адаптера не выполнена", "Adapter installation failed"],
        "editor.verification-failed": ["Редактор не подтвердил установку", "Editor did not confirm the installation"],
        "editor.busy": ["Подключение уже выполняется", "Setup is already running"],
        "providers.openRouter": ["OpenRouter", "OpenRouter"],
        "providers.codexHint": [
            "Локальный Codex CLI с авторизацией через ChatGPT.",
            "Local Codex CLI signed in with ChatGPT."
        ],
        "providers.openRouterToken": ["Токен OpenRouter", "OpenRouter token"],
        "providers.check": ["Проверить", "Check"],
        "providers.useCodex": ["Использовать Codex", "Use Codex"],
        "providers.checking": ["Проверяю...", "Checking..."],
        "providers.unknown": ["Не проверен", "Not checked"],
        "providers.installed": ["Установлен, вход не проверен", "Installed, sign-in not checked"],
        "providers.notInstalled": ["Codex CLI не установлен", "Codex CLI is not installed"],
        "providers.signIn": ["Требуется вход в Codex", "Sign in to Codex"],
        "providers.subscription": ["Подписка ChatGPT подключена", "ChatGPT subscription connected"],
        "providers.apiAuth": ["Вход выполнен через API, не подписку", "Signed in with API billing, not subscription"],
        "chat.title": ["Чаты с ИИ", "AI chats"],
        "chat.enabled": ["Включён", "Enabled"],
        "chat.hotkey": ["Горячая клавиша", "Keyboard shortcut"],
        "chat.action": ["Действие", "Action"],
        "chat.afterConfirm": ["После подтверждения", "After confirmation"],
        "chat.analysisSource": ["Источник анализа", "Analysis source"],
        "chat.continueText": ["Текст продолжения", "Continuation text"],
        "chat.processes": ["Разрешённые процессы", "Allowed processes"],
        "chat.continue": ["Продолжить без анализа", "Continue without analysis"],
        "chat.nextStep": ["Предложить следующий шаг", "Suggest the next step"],
        "chat.submit": ["Отправить сообщением", "Send message"],
        "chat.draft": ["Оставить черновиком", "Leave as draft"],
        "chat.localText": ["Локальный текст (UI Automation)", "Local text (UI Automation)"],
        "chat.screenshot": ["Снимок окна", "Window screenshot"],
        "models.title": ["Модели по контекстам", "Models by context"],
        "models.context": ["Контекст", "Context"],
        "models.provider": ["Провайдер", "Provider"],
        "models.model": ["Модель", "Model"],
        "models.temperature": ["Температура", "Temperature"],
        "models.maxTokens": ["Макс. токенов", "Max tokens"],
        "models.codexSubscription": ["Codex CLI", "Codex CLI"],
        "models.custom": ["(своя...)", "(custom...)"],
        "models.codexDefault": ["По умолчанию из Codex CLI", "Default from Codex CLI"],
        "models.reasoningDefault": ["Reasoning: по умолчанию", "Reasoning: default"],
        "models.reasoningMinimal": ["Reasoning: минимальный", "Reasoning: minimal"],
        "models.reasoningLow": ["Reasoning: низкий", "Reasoning: low"],
        "models.reasoningMedium": ["Reasoning: средний", "Reasoning: medium"],
        "models.reasoningHigh": ["Reasoning: высокий", "Reasoning: high"],
        "models.reasoningXhigh": ["Reasoning: очень высокий", "Reasoning: extra high"],
        "models.reasoningMax": ["Reasoning: максимальный", "Reasoning: maximum"],
        "models.reasoningUltra": ["Reasoning: максимальный + делегирование", "Reasoning: maximum + delegation"],
        "prompts.title": ["Промпты", "Prompts"],
        "prompts.context": ["Контекст промпта", "Prompt context"],
        "prompts.system": ["Системный", "System"],
        "prompts.user": ["Пользовательский", "User"],
        "prompts.placeholders": ["Плейсхолдеры: {text} {language}", "Placeholders: {text} {language}"],
        "about.title": ["Об авторе", "About the author"],
        "about.author": ["Автор", "Author"],
        "about.links": ["Ссылки автора", "Author links"],
        "about.openWebsite": ["Открыть сайт автора", "Open the author's website"],
        "about.openTelegram": ["Открыть личный Telegram автора", "Open the author's personal Telegram"],
        "common.show": ["Показать", "Show"],
        "common.hide": ["Скрыть", "Hide"],
        "common.openConfig": ["Открыть папку конфигов", "Open config folder"],
        "common.cancel": ["Отмена", "Cancel"],
        "common.save": ["Сохранить", "Save"],
        "errors.config": ["Не удалось прочитать часть конфигов.", "Some configuration files could not be read."],
        "confirm.screenshot": [
            "Выбранный профиль отправляет снимок активного AI-чата выбранному провайдеру. Включить?",
            "This profile sends a screenshot of the active AI chat to the selected provider. Enable it?"
        ],
        "confirm.codePrompt": [
            "Промпт Code без {text}/{language} сломает подстановку. Сохранить всё равно?",
            "The Code prompt is missing {text}/{language}, so substitution will fail. Save anyway?"
        ],
        "confirm.imagePrompt": [
            "{text} в Image-промпте не подставляется (изображение в изображение). Оставить?",
            "{text} is not substituted in the Image prompt (image-to-image). Keep it?"
        ],
        "preview.title": ["Предпросмотр", "Preview"],
        "preview.context": ["Контекст", "Context"],
        "preview.model": ["Модель", "Model"],
        "preview.views": ["Режимы предпросмотра", "Preview views"],
        "preview.reasoning": ["Ход обработки", "Processing"],
        "preview.result": ["Результат", "Result"],
        "preview.diff": ["Изменения", "Changes"],
        "preview.simplifiedDiff": ["Упрощённое сравнение: большой изменённый блок", "Simplified comparison: large changed block"],
        "preview.integrityTitle": ["Изменены числа или цифровые даты", "Numbers or numeric dates changed"],
        "preview.integrityUnavailable": ["Числа и даты не удалось проверить", "Numbers and dates could not be checked"],
        "preview.integrityConsent": ["Числа и даты проверены, изменения намеренные", "I checked the numbers and dates; the changes are intentional"],
        "preview.integrityRemoved": ["Удалены или встречаются реже: ", "Removed or less frequent: "],
        "preview.integrityAdded": ["Добавлены или встречаются чаще: ", "Added or more frequent: "],
        "preview.original": ["Оригинал", "Original"],
        "preview.apply": ["Применить", "Apply"],
        "preview.send": ["Отправить", "Send"],
        "preview.copy": ["Копировать", "Copy"],
        "preview.cancel": ["Отмена", "Cancel"],
        "preview.processing": ["Обрабатываю...", "Processing..."],
        "preview.error": ["ошибка", "error"]
    };
    const translations = { ru: {}, en: {} };
    for (const [key, values] of Object.entries(pairs)) {
        translations.ru[key] = values[0];
        translations.en[key] = values[1];
    }
    return translations;
}));
