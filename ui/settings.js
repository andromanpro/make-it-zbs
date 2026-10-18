(function () {
    "use strict";

    const core = window.ZbsSettingsCore;
    const customModelValue = "__custom__";
    const translations = window.ZbsI18n;
    const state = {
        general: {},
        aiChat: {},
        token: "",
        contexts: {},
        prompts: {},
        catalog: [],
        activePromptName: "",
        codexStatus: {},
        editorAdapter: {}
    };

    const hotkeyInput = document.getElementById("hotkeyInput");
    const languageSelect = document.getElementById("languageSelect");
    const backgroundInput = document.getElementById("backgroundInput");
    const autostartInput = document.getElementById("autostartInput");
    const operationFeedbackSelect = document.getElementById("operationFeedbackSelect");
    const loggingInput = document.getElementById("loggingInput");
    const openLogsButton = document.getElementById("openLogsButton");
    const aiProfilesContainer = document.getElementById("aiProfilesContainer");
    const tokenInput = document.getElementById("tokenInput");
    const tokenToggleButton = document.getElementById("tokenToggleButton");
    const modelsContainer = document.getElementById("modelsContainer");
    const promptSelect = document.getElementById("promptSelect");
    const systemPromptInput = document.getElementById("systemPromptInput");
    const userPromptInput = document.getElementById("userPromptInput");
    const configAlert = document.getElementById("configAlert");
    const saveButton = document.getElementById("saveButton");
    const cancelButton = document.getElementById("cancelButton");
    const openConfigButton = document.getElementById("openConfigButton");
    const authorWebsiteButton = document.getElementById("authorWebsiteButton");
    const authorTelegramButton = document.getElementById("authorTelegramButton");
    const codexStatus = document.getElementById("codexStatus");
    const checkCodexButton = document.getElementById("checkCodexButton");
    const useCodexButton = document.getElementById("useCodexButton");
    const editorAdapterStatus = document.getElementById("editorAdapterStatus");
    const checkEditorAdapterButton = document.getElementById("checkEditorAdapterButton");
    const installEditorAdapterButton = document.getElementById("installEditorAdapterButton");
    let activeHotkeyCapture = null;

    function currentLanguage() {
        return normalize(state.general.language).toLowerCase() === "en" ? "en" : "ru";
    }

    function t(key) {
        const language = currentLanguage();
        return translations[language][key] || translations.ru[key] || key;
    }

    function applyTranslations() {
        const language = currentLanguage();
        document.documentElement.lang = language;
        document.title = language === "en" ? "Make it ZBS Settings" : "Настройки Make it ZBS";
        for (const element of document.querySelectorAll("[data-i18n]")) {
            element.textContent = t(element.dataset.i18n);
        }
        for (const element of document.querySelectorAll("[data-i18n-aria-label]")) {
            element.setAttribute("aria-label", t(element.dataset.i18nAriaLabel));
        }
        for (const element of document.querySelectorAll("[data-i18n-placeholder]")) {
            element.setAttribute("placeholder", t(element.dataset.i18nPlaceholder));
        }
        tokenInput.placeholder = language === "en"
            ? "OPENROUTER_API_KEY is not set"
            : "OPENROUTER_API_KEY не задан";
        tokenToggleButton.textContent = tokenInput.type === "password" ? t("common.show") : t("common.hide");
        if (activeHotkeyCapture) {
            activeHotkeyCapture.button.textContent = t("common.cancel");
            activeHotkeyCapture.feedback.textContent = t(
                activeHotkeyCapture.feedback.dataset.state === "unsupported"
                    ? "hotkey.unsupported"
                    : "hotkey.press");
        }
        renderCodexStatus(state.codexStatus);
        renderEditorAdapter();
        updateCodexCatalogControls();
    }

    function normalize(value) {
        return core.normalize(value);
    }

    function renderEditorAdapter() {
        const status = state.editorAdapter;
        let key = "providers.unknown";
        if (status.checking) {
            key = status.installing ? "editor.installing" : "providers.checking";
        } else if (status.error) {
            const errorKey = "editor." + status.error;
            key = Object.hasOwn(translations.ru, errorKey) ? errorKey : "editor.failed";
        } else if (status.checked) {
            key = editorAdapterLabel(status);
        }
        editorAdapterStatus.textContent = t(key);
        let displayState = status.current ? "ok" : "unknown";
        if (status.error) {
            displayState = "error";
        }
        editorAdapterStatus.dataset.state = displayState;
        installEditorAdapterButton.textContent = t(status.installed ? "editor.update" : "editor.connect");
        installEditorAdapterButton.hidden = status.current === true;
        installEditorAdapterButton.disabled = !status.editorFound || status.current === true
            || status.checking === true || !!status.error;
        checkEditorAdapterButton.disabled = status.checking === true;
    }

    function editorAdapterLabel(status) {
        if (!status.editorFound) {
            return "editor.missing";
        }
        if (status.reloadRequired) {
            return "editor.reload";
        }
        if (status.current) {
            return "editor.installed";
        }
        return status.installed ? "editor.updateRequired" : "editor.required";
    }

    function cloneObject(value) {
        return core.cloneObject(value);
    }

    function stopHotkeyCapture(value) {
        const capture = activeHotkeyCapture;
        if (!capture) {
            return;
        }

        document.removeEventListener("keydown", handleHotkeyCaptureKeydown, true);
        capture.input.value = value === null ? capture.originalValue : value;
        capture.input.placeholder = "";
        capture.editor.classList.remove("is-capturing");
        capture.feedback.textContent = "";
        capture.feedback.dataset.state = "";
        capture.button.dataset.i18n = "hotkey.change";
        capture.button.textContent = t("hotkey.change");
        activeHotkeyCapture = null;
    }

    function handleHotkeyCaptureKeydown(event) {
        if (!activeHotkeyCapture) {
            return;
        }

        event.preventDefault();
        event.stopImmediatePropagation();
        if (event.repeat) {
            return;
        }

        if (event.key === "Escape"
            && !event.ctrlKey && !event.altKey && !event.shiftKey && !event.metaKey) {
            stopHotkeyCapture(null);
            return;
        }

        const result = core.captureHotkeyEvent(event);
        if (result.status === "pending") {
            activeHotkeyCapture.input.value = result.value;
            return;
        }

        if (result.status === "unsupported") {
            activeHotkeyCapture.input.value = "";
            activeHotkeyCapture.feedback.dataset.state = "unsupported";
            activeHotkeyCapture.feedback.textContent = t("hotkey.unsupported");
            return;
        }

        stopHotkeyCapture(result.value);
    }

    function startHotkeyCapture(binding) {
        if (activeHotkeyCapture === binding) {
            return;
        }
        if (activeHotkeyCapture) {
            stopHotkeyCapture(null);
        }

        binding.originalValue = binding.input.value;
        binding.input.value = "";
        binding.input.placeholder = t("hotkey.press");
        binding.editor.classList.add("is-capturing");
        binding.feedback.dataset.state = "capturing";
        binding.feedback.textContent = t("hotkey.press");
        binding.button.dataset.i18n = "common.cancel";
        binding.button.textContent = t("common.cancel");
        activeHotkeyCapture = binding;
        document.addEventListener("keydown", handleHotkeyCaptureKeydown, true);
        binding.input.focus();
    }

    function bindHotkeyEditor(editor) {
        const binding = {
            editor: editor,
            input: editor.querySelector("input"),
            button: editor.querySelector(".hotkey-capture-button"),
            feedback: editor.querySelector(".hotkey-feedback"),
            originalValue: ""
        };
        binding.input.readOnly = true;
        binding.input.addEventListener("click", function () {
            startHotkeyCapture(binding);
        });
        binding.button.addEventListener("click", function () {
            if (activeHotkeyCapture === binding) {
                stopHotkeyCapture(null);
            } else {
                startHotkeyCapture(binding);
            }
        });
        return editor;
    }

    function createHotkeyEditor(input) {
        const editor = document.createElement("div");
        editor.className = "hotkey-editor";
        const control = document.createElement("div");
        control.className = "hotkey-control";
        const button = document.createElement("button");
        button.className = "button secondary compact hotkey-capture-button";
        button.type = "button";
        button.dataset.i18n = "hotkey.change";
        button.textContent = t("hotkey.change");
        const feedback = document.createElement("span");
        feedback.className = "hotkey-feedback";
        feedback.setAttribute("aria-live", "polite");
        control.appendChild(input);
        control.appendChild(button);
        editor.appendChild(control);
        editor.appendChild(feedback);
        return bindHotkeyEditor(editor);
    }

    function catalogContains(slug) {
        for (const model of state.catalog) {
            if (model && model.slug === slug) {
                return true;
            }
        }

        return false;
    }

    function postCommand(command, payload) {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage(JSON.stringify({
                cmd: command,
                payload: payload || {}
            }));
        }
    }

    function renderGeneral() {
        hotkeyInput.value = normalize(state.general.hotkey);
        languageSelect.value = currentLanguage();
        backgroundInput.checked = state.general.background !== false;
        autostartInput.checked = state.general.autostart === true;
        operationFeedbackSelect.value = ["icon", "popup", "none"].includes(state.general.operationFeedback)
            ? state.general.operationFeedback : "both";
    }

    function renderToken() {
        tokenInput.type = "password";
        tokenInput.value = normalize(state.token);
        tokenToggleButton.textContent = t("common.show");
    }

    function renderCodexStatus(status) {
        status = status && typeof status === "object" ? status : {};
        let key = "providers.unknown";
        let visualState = "unknown";
        if (status.checking === true) {
            key = "providers.checking";
        } else if (status.installed === false) {
            key = "providers.notInstalled";
            visualState = "error";
        } else if (status.subscription === true) {
            key = "providers.subscription";
            visualState = "ok";
        } else if (status.authenticated === true) {
            key = "providers.apiAuth";
            visualState = "warning";
        } else if (status.installed === true && status.checked === true) {
            key = "providers.signIn";
            visualState = "warning";
        } else if (status.installed === true) {
            key = "providers.installed";
        }

        codexStatus.textContent = t(key);
        codexStatus.dataset.state = visualState;
        codexStatus.title = [normalize(status.version), normalize(status.diagnostic)]
            .filter(Boolean).join("\n");
        checkCodexButton.disabled = status.checking === true;
        useCodexButton.disabled = status.subscription !== true;
    }

    function renderAiChat() {
        const profiles = core.normalizeProfiles(state.aiChat.profiles);
        const fragment = document.createDocumentFragment();
        aiProfilesContainer.textContent = "";

        Object.keys(profiles).forEach(function (name, index) {
            fragment.appendChild(createAiProfile(name, profiles[name], index === 0));
        });

        aiProfilesContainer.appendChild(fragment);
    }

    function createSelect(className, values, selectedValue) {
        const select = document.createElement("select");
        select.className = className;
        for (const value of values) {
            const option = document.createElement("option");
            option.value = value.value;
            option.dataset.i18n = value.key;
            option.textContent = t(value.key);
            select.appendChild(option);
        }
        select.value = selectedValue;
        return select;
    }

    function createProfileField(labelKey, control) {
        const label = document.createElement("label");
        label.className = "field";
        const text = document.createElement("span");
        text.dataset.i18n = labelKey;
        text.textContent = t(labelKey);
        label.appendChild(text);
        label.appendChild(control);
        return label;
    }

    function createModelCell(labelKey, controls) {
        const cell = document.createElement("div");
        cell.className = "model-cell";
        const label = document.createElement("span");
        label.className = "model-cell-label";
        label.dataset.i18n = labelKey;
        label.textContent = t(labelKey);
        cell.appendChild(label);
        for (const control of controls) {
            cell.appendChild(control);
        }
        return cell;
    }

    function createAiProfile(name, profile, openByDefault) {
        const section = document.createElement("details");
        section.className = "ai-profile";
        section.dataset.profile = name;
        section.open = openByDefault;

        const header = document.createElement("summary");
        header.className = "ai-profile-header";
        const title = document.createElement("h3");
        title.textContent = profile.label;
        const enabledLabel = document.createElement("label");
        enabledLabel.className = "check-field switch-field compact-check";
        const enabled = document.createElement("input");
        enabled.type = "checkbox";
        enabled.className = "profile-enabled";
        enabled.checked = profile.enabled;
        const enabledText = document.createElement("span");
        enabledText.dataset.i18n = "chat.enabled";
        enabledText.textContent = t("chat.enabled");
        enabledLabel.appendChild(enabled);
        enabledLabel.appendChild(enabledText);
        enabledLabel.addEventListener("click", function (event) {
            event.stopPropagation();
        });
        enabled.addEventListener("change", function () {
            if (enabled.checked) {
                section.open = true;
            }
        });
        header.appendChild(title);
        header.appendChild(enabledLabel);

        const grid = document.createElement("div");
        grid.className = "profile-grid";
        const hotkey = document.createElement("input");
        hotkey.className = "profile-hotkey";
        hotkey.type = "text";
        hotkey.value = profile.hotkey;
        hotkey.id = `profileHotkey-${name}`;
        const mode = createSelect("profile-mode", [
            { value: "continue", key: "chat.continue" },
            { value: "nextStep", key: "chat.nextStep" }
        ], profile.mode);
        const sendMode = createSelect("profile-send-mode", [
            { value: "submit", key: "chat.submit" },
            { value: "draft", key: "chat.draft" }
        ], profile.sendMode);
        const analysisSource = createSelect("profile-analysis-source", [
            { value: "localText", key: "chat.localText" },
            { value: "screenshot", key: "chat.screenshot" }
        ], profile.analysisSource);
        const continueText = document.createElement("input");
        continueText.className = "profile-continue-text";
        continueText.type = "text";
        continueText.maxLength = 1000;
        continueText.value = profile.continueText;
        const processes = document.createElement("input");
        processes.className = "profile-processes";
        processes.type = "text";
        processes.value = profile.processes.join(", ");

        const hotkeyField = document.createElement("div");
        hotkeyField.className = "field";
        const hotkeyLabel = document.createElement("label");
        hotkeyLabel.htmlFor = hotkey.id;
        hotkeyLabel.dataset.i18n = "chat.hotkey";
        hotkeyLabel.textContent = t("chat.hotkey");
        hotkeyField.appendChild(hotkeyLabel);
        hotkeyField.appendChild(createHotkeyEditor(hotkey));
        grid.appendChild(hotkeyField);
        grid.appendChild(createProfileField("chat.action", mode));
        grid.appendChild(createProfileField("chat.afterConfirm", sendMode));
        grid.appendChild(createProfileField("chat.analysisSource", analysisSource));
        grid.appendChild(createProfileField("chat.continueText", continueText));
        grid.appendChild(createProfileField("chat.processes", processes));
        section.appendChild(header);
        section.appendChild(grid);

        function updateAvailability() {
            continueText.disabled = mode.value !== "continue";
            analysisSource.disabled = mode.value !== "nextStep";
        }

        mode.addEventListener("change", updateAvailability);
        updateAvailability();
        return section;
    }

    function createCatalogOption(model) {
        const option = document.createElement("option");
        const kind = normalize(model.kind);
        option.value = normalize(model.slug);
        option.textContent = normalize(model.label) || option.value;

        if (kind.length > 0) {
            option.textContent += " [" + kind + "]";
        }

        return option;
    }

    function updateCustomModelVisibility(row) {
        const select = row.querySelector(".model-select");
        const customInput = row.querySelector(".custom-model-input");
        customInput.hidden = select.value !== customModelValue;
    }

    function updateProviderControls(row) {
        const provider = row.querySelector(".provider-select").value;
        const openRouterControls = row.querySelector(".openrouter-model-controls");
        const codexControls = row.querySelector(".codex-model-controls");
        const temperature = row.querySelector(".temperature-input");
        const maxTokens = row.querySelector(".max-tokens-input");
        openRouterControls.hidden = provider === "codex";
        codexControls.hidden = provider !== "codex";
        temperature.disabled = provider === "codex";
        maxTokens.disabled = provider === "codex";
    }

    function supportsCodex(contextName) {
        return ["AIChat", "Browser", "Code", "Email", "Excel", "Image", "Text"]
            .includes(contextName);
    }

    function findCodexModel(slug) {
        const models = Array.isArray(state.codexStatus.models) ? state.codexStatus.models : [];
        for (const model of models) {
            if (normalize(model?.slug) === slug) {
                return model;
            }
        }
        return null;
    }

    function createPlainOption(value, label) {
        const option = document.createElement("option");
        option.value = value;
        option.textContent = label;
        return option;
    }

    function reasoningLabel(effort) {
        const suffix = effort.charAt(0).toUpperCase() + effort.slice(1);
        return t("models.reasoning" + suffix);
    }

    function populateCodexModelSelect(select, selectedModel) {
        select.textContent = "";
        select.appendChild(createPlainOption("", t("models.codexDefault")));
        const models = Array.isArray(state.codexStatus.models) ? state.codexStatus.models : [];
        for (const model of models) {
            const slug = normalize(model?.slug);
            if (slug.length > 0) {
                select.appendChild(createPlainOption(slug, normalize(model.label) || slug));
            }
        }
        if (selectedModel.length > 0 && !findCodexModel(selectedModel)) {
            select.appendChild(createPlainOption(selectedModel, selectedModel));
        }
        select.value = selectedModel;
    }

    function populateReasoningSelect(select, selectedModel, selectedEffort) {
        const model = findCodexModel(selectedModel);
        const defaultEffort = normalize(model?.defaultReasoningEffort);
        const defaultLabel = defaultEffort.length > 0
            ? t("models.reasoningDefault") + " (" + defaultEffort + ")"
            : t("models.reasoningDefault");
        select.textContent = "";
        select.appendChild(createPlainOption("", defaultLabel));

        const fallback = ["minimal", "low", "medium", "high", "xhigh", "max", "ultra"];
        const levels = model && Array.isArray(model.reasoningEfforts)
            ? model.reasoningEfforts
            : fallback;
        for (const rawEffort of levels) {
            const effort = normalize(rawEffort).toLowerCase();
            if (fallback.includes(effort)) {
                select.appendChild(createPlainOption(effort, reasoningLabel(effort)));
            }
        }
        if (selectedEffort.length > 0
            && !Array.from(select.options).some(function (option) { return option.value === selectedEffort; })) {
            select.appendChild(createPlainOption(selectedEffort, "Reasoning: " + selectedEffort));
        }
        select.value = selectedEffort;
    }

    function updateCodexCatalogControls() {
        for (const row of modelsContainer.querySelectorAll(".model-row")) {
            const modelSelect = row.querySelector(".codex-model-select");
            const reasoningSelect = row.querySelector(".codex-reasoning-select");
            if (!modelSelect || !reasoningSelect) {
                continue;
            }
            const selectedModel = modelSelect.value;
            const selectedEffort = reasoningSelect.value;
            populateCodexModelSelect(modelSelect, selectedModel);
            populateReasoningSelect(reasoningSelect, selectedModel, selectedEffort);
        }
    }

    function createModelRow(contextName, context) {
        const row = document.createElement("div");
        row.className = "model-row";
        row.dataset.context = contextName;

        const name = document.createElement("div");
        name.className = "context-name";
        name.textContent = contextName;
        name.title = contextName;

        const codexSupported = supportsCodex(contextName);
        const provider = createSelect("provider-select", [
            { value: "openrouter", key: "providers.openRouter" },
            { value: "codex", key: "models.codexSubscription" }
        ], codexSupported && context.provider === "codex" ? "codex" : "openrouter");
        if (!codexSupported) {
            provider.querySelector('option[value="codex"]').disabled = true;
        }

        const modelControls = document.createElement("div");
        modelControls.className = "model-controls openrouter-model-controls";

        const select = document.createElement("select");
        select.className = "model-select";

        for (const model of state.catalog) {
            select.appendChild(createCatalogOption(model));
        }

        const customOption = document.createElement("option");
        customOption.value = customModelValue;
        customOption.dataset.i18n = "models.custom";
        customOption.textContent = t("models.custom");
        select.appendChild(customOption);

        const customInput = document.createElement("input");
        customInput.className = "custom-model-input";
        customInput.type = "text";
        customInput.autocomplete = "off";
        customInput.placeholder = "custom/model-slug";

        const modelSlug = normalize(context.model);
        if (modelSlug.length > 0 && catalogContains(modelSlug)) {
            select.value = modelSlug;
            customInput.value = "";
            customInput.hidden = true;
        } else {
            select.value = customModelValue;
            customInput.value = modelSlug;
            customInput.hidden = false;
        }

        select.addEventListener("change", function () {
            updateCustomModelVisibility(row);
            if (select.value === customModelValue) {
                customInput.focus();
            }
        });

        modelControls.appendChild(select);
        modelControls.appendChild(customInput);

        const codexControls = document.createElement("div");
        codexControls.className = "model-controls codex-model-controls";
        const codexModel = document.createElement("select");
        codexModel.className = "codex-model-input codex-model-select";
        codexModel.setAttribute("aria-label", t("models.model"));
        const codexReasoning = document.createElement("select");
        codexReasoning.className = "codex-reasoning-select";
        codexReasoning.setAttribute("aria-label", "Reasoning");
        populateCodexModelSelect(codexModel, normalize(context.codexModel));
        populateReasoningSelect(
            codexReasoning,
            normalize(context.codexModel),
            normalize(context.codexReasoningEffort).toLowerCase());
        codexModel.addEventListener("change", function () {
            populateReasoningSelect(codexReasoning, codexModel.value, "");
        });
        codexControls.appendChild(codexModel);
        codexControls.appendChild(codexReasoning);

        const temperature = document.createElement("input");
        temperature.className = "temperature-input";
        temperature.type = "number";
        temperature.min = "0";
        temperature.max = "2";
        temperature.step = "0.1";
        temperature.value = context.temperature === null || context.temperature === undefined
            ? "0.5"
            : String(context.temperature);

        const maxTokens = document.createElement("input");
        maxTokens.className = "max-tokens-input";
        maxTokens.type = "number";
        maxTokens.min = "1";
        maxTokens.step = "1";
        maxTokens.value = context.maxTokens === null || context.maxTokens === undefined
            ? "2048"
            : String(context.maxTokens);

        row.appendChild(name);
        const providerCell = createModelCell("models.provider", [provider]);
        const modelCell = createModelCell("models.model", [modelControls, codexControls]);
        modelCell.classList.add("model-choice-cell");
        const temperatureCell = createModelCell("models.temperature", [temperature]);
        const maxTokensCell = createModelCell("models.maxTokens", [maxTokens]);

        row.appendChild(providerCell);
        row.appendChild(modelCell);
        row.appendChild(temperatureCell);
        row.appendChild(maxTokensCell);

        provider.addEventListener("change", function () {
            updateProviderControls(row);
        });
        updateProviderControls(row);

        return row;
    }

    function renderModels() {
        const names = Object.keys(state.contexts);
        const fragment = document.createDocumentFragment();

        modelsContainer.textContent = "";

        for (const name of names) {
            fragment.appendChild(createModelRow(name, state.contexts[name] || {}));
        }

        modelsContainer.appendChild(fragment);
    }

    function syncPromptEditors() {
        if (!state.activePromptName) {
            return;
        }

        if (!state.prompts[state.activePromptName]) {
            state.prompts[state.activePromptName] = {};
        }

        state.prompts[state.activePromptName].system = systemPromptInput.value;
        state.prompts[state.activePromptName].user = userPromptInput.value;
    }

    function renderPromptEditors() {
        const prompt = state.prompts[state.activePromptName] || {};
        systemPromptInput.value = normalize(prompt.system);
        userPromptInput.value = normalize(prompt.user);
    }

    function renderPrompts() {
        const names = Object.keys(state.prompts);
        const fragment = document.createDocumentFragment();

        promptSelect.textContent = "";

        for (const name of names) {
            const option = document.createElement("option");
            option.value = name;
            option.textContent = name;
            fragment.appendChild(option);
        }

        promptSelect.appendChild(fragment);

        if (!names.includes(state.activePromptName)) {
            state.activePromptName = names.length > 0 ? names[0] : "";
        }

        promptSelect.value = state.activePromptName;
        renderPromptEditors();
    }

    function renderConfigAlert(data) {
        if (data.configError === true) {
            configAlert.textContent = normalize(data.configErrorMessage) || t("errors.config");
            configAlert.hidden = false;
        } else {
            configAlert.textContent = "";
            configAlert.hidden = true;
        }
    }

    function render(data) {
        stopHotkeyCapture(null);
        state.general = cloneObject(data.general);
        state.aiChat = cloneObject(data.aiChat);
        state.token = normalize(data.token);
        state.contexts = cloneObject(data.contexts);
        state.prompts = cloneObject(data.prompts);
        state.catalog = Array.isArray(data.catalog) ? data.catalog.slice(0) : [];
        state.codexStatus = cloneObject(data.codexStatus);
        state.editorAdapter = cloneObject(data.editorAdapter);

        renderGeneral();
        loggingInput.checked = data.logging?.enabled === true;
        applyTranslations();
        renderConfigAlert(data);
        renderAiChat();
        renderToken();
        renderModels();
        renderPrompts();
    }

    function readModelRows() {
        const contexts = {};
        const rows = modelsContainer.querySelectorAll(".model-row");

        for (const row of rows) {
            const contextName = row.dataset.context;
            const select = row.querySelector(".model-select");
            const customInput = row.querySelector(".custom-model-input");
            const provider = row.querySelector(".provider-select").value;
            const codexModel = row.querySelector(".codex-model-select").value.trim();
            const codexReasoningEffort = row.querySelector(".codex-reasoning-select").value;
            const temperatureInput = row.querySelector(".temperature-input");
            const maxTokensInput = row.querySelector(".max-tokens-input");
            const temperature = Number.parseFloat(temperatureInput.value);
            const maxTokens = Number.parseInt(maxTokensInput.value, 10);

            contexts[contextName] = {
                provider: provider,
                model: select.value === customModelValue ? customInput.value.trim() : select.value,
                codexModel: codexModel,
                codexReasoningEffort: codexReasoningEffort,
                temperature: Number.isNaN(temperature) ? 0.5 : temperature,
                maxTokens: Number.isNaN(maxTokens) ? 2048 : maxTokens
            };
        }

        return contexts;
    }

    function readAiProfiles() {
        const profiles = {};
        const rows = aiProfilesContainer.querySelectorAll(".ai-profile");
        for (const row of rows) {
            const name = row.dataset.profile;
            profiles[name] = core.normalizeProfile(name, {
                label: row.querySelector("h3").textContent,
                enabled: row.querySelector(".profile-enabled").checked,
                processes: row.querySelector(".profile-processes").value,
                hotkey: row.querySelector(".profile-hotkey").value,
                mode: row.querySelector(".profile-mode").value,
                continueText: row.querySelector(".profile-continue-text").value,
                sendMode: row.querySelector(".profile-send-mode").value,
                analysisSource: row.querySelector(".profile-analysis-source").value
            });
        }
        return profiles;
    }

    function buildPayload() {
        syncPromptEditors();

        return {
            general: {
                hotkey: hotkeyInput.value.trim(),
                language: currentLanguage(),
                background: backgroundInput.checked,
                autostart: autostartInput.checked,
                operationFeedback: operationFeedbackSelect.value
            },
            aiChat: {
                profiles: readAiProfiles()
            },
            logging: { enabled: loggingInput.checked },
            token: tokenInput.value,
            contexts: readModelRows(),
            prompts: cloneObject(state.prompts),
            catalog: state.catalog.slice(0)
        };
    }

    function confirmPromptGuardrails(payload) {
        if (core.requiresScreenshotConfirmation(state.aiChat.profiles, payload.aiChat.profiles)) {
            if (!confirm(t("confirm.screenshot"))) {
                return false;
            }
        }

        const promptValidation = core.validatePrompts(payload.prompts);
        if (promptValidation.codeMissingPlaceholders) {
            if (!confirm(t("confirm.codePrompt"))) {
                return false;
            }
        }

        if (promptValidation.imageContainsTextPlaceholder) {
            if (!confirm(t("confirm.imagePrompt"))) {
                return false;
            }
        }

        return true;
    }

    function handleMessage(event) {
        let data = event.data || {};
        if (typeof data === "string") {
            data = JSON.parse(data);
        }

        if (data.type === "codexStatus") {
            state.codexStatus = cloneObject(data.codexStatus);
            state.codexStatus.checked = true;
            renderCodexStatus(state.codexStatus);
            updateCodexCatalogControls();
            return;
        }
        if (data.type === "editorAdapter") {
            state.editorAdapter = cloneObject(data.editorAdapter);
            renderEditorAdapter();
            return;
        }

        render(data);
    }

    bindHotkeyEditor(document.getElementById("generalHotkeyEditor"));
    window.chrome.webview.addEventListener("message", handleMessage); // NOSONAR: host-only WebView2 channel; native side validates e.Source.

    tokenToggleButton.addEventListener("click", function () {
        const isPassword = tokenInput.type === "password";
        tokenInput.type = isPassword ? "text" : "password";
        tokenToggleButton.textContent = isPassword ? t("common.hide") : t("common.show");
    });

    languageSelect.addEventListener("change", function () {
        state.general.language = languageSelect.value;
        applyTranslations();
    });

    checkCodexButton.addEventListener("click", function () {
        state.codexStatus = {
            checking: true,
            models: Array.isArray(state.codexStatus.models) ? state.codexStatus.models : []
        };
        renderCodexStatus(state.codexStatus);
        postCommand("checkCodexStatus");
    });

    function requestEditorAdapter(installing) {
        state.editorAdapter = { ...state.editorAdapter, checking: true, installing: installing, error: "" };
        renderEditorAdapter();
        postCommand(installing ? "installEditorAdapter" : "checkEditorAdapter");
    }
    checkEditorAdapterButton.addEventListener("click", function () { requestEditorAdapter(false); });
    openLogsButton.addEventListener("click", function () { postCommand("openLogsFolder"); });
    installEditorAdapterButton.addEventListener("click", function () { requestEditorAdapter(true); });

    useCodexButton.addEventListener("click", function () {
        for (const row of modelsContainer.querySelectorAll(".model-row")) {
            const provider = row.querySelector(".provider-select");
            const codexOption = provider.querySelector('option[value="codex"]');
            if (codexOption && !codexOption.disabled) {
                provider.value = "codex";
                updateProviderControls(row);
            }
        }
    });

    promptSelect.addEventListener("change", function () {
        syncPromptEditors();
        state.activePromptName = promptSelect.value;
        renderPromptEditors();
    });

    saveButton.addEventListener("click", function () {
        const payload = buildPayload();
        if (!confirmPromptGuardrails(payload)) {
            return;
        }

        postCommand("save", payload);
    });

    cancelButton.addEventListener("click", function () {
        postCommand("cancel");
    });

    openConfigButton.addEventListener("click", function () {
        postCommand("openConfigFolder");
    });

    authorWebsiteButton.addEventListener("click", function () {
        postCommand("openAuthorWebsite");
    });

    authorTelegramButton.addEventListener("click", function () {
        postCommand("openAuthorTelegram");
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            postCommand("cancel");
        }
    });
})();
