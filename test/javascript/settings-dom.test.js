"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const process = require("node:process");
const test = require("node:test");
const { JSDOM } = require("jsdom");
const core = require("../../ui/settings-core.js");

function loadSettingsPage() {
    const html = fs.readFileSync(path.join(process.cwd(), "ui", "settings.html"), "utf8");
    const dom = new JSDOM(html, { url: "https://zbs.ui/settings.html" });
    const posted = [];
    let messageHandler = null;
    const webview = {
        addEventListener: function (name, handler) {
            if (name === "message") {
                messageHandler = handler;
            }
        },
        postMessage: function (message) {
            posted.push(JSON.parse(message));
        }
    };

    dom.window.ZbsSettingsCore = core;
    dom.window.chrome = { webview: webview };
    globalThis.window = dom.window;
    globalThis.document = dom.window.document;
    globalThis.confirm = function () { return true; };
    delete require.cache[require.resolve("../../ui/i18n.js")];
    dom.window.ZbsI18n = require("../../ui/i18n.js");
    delete require.cache[require.resolve("../../ui/settings.js")];
    require("../../ui/settings.js");

    return {
        dom: dom,
        posted: posted,
        send: function (data) { messageHandler({ data: data }); }
    };
}

function fixture() {
    return {
        configError: true,
        configErrorMessage: "broken config",
        general: { hotkey: "Alt+F10", language: "ru", background: false, autostart: true },
        codexStatus: {
            installed: true,
            authenticated: true,
            subscription: true,
            version: "codex-cli 1.0",
            models: [
                {
                    slug: "gpt-5.6-sol", label: "GPT-5.6-Sol",
                    defaultReasoningEffort: "low",
                    reasoningEfforts: ["low", "medium", "high", "xhigh", "max", "ultra"]
                },
                {
                    slug: "gpt-5.6-luna", label: "GPT-5.6-Luna",
                    defaultReasoningEffort: "medium",
                    reasoningEfforts: ["low", "medium", "high", "xhigh", "max"]
                }
            ]
        },
        token: "secret",
        aiChat: {
            profiles: {
                codex: {
                    label: "Codex",
                    enabled: true,
                    processes: ["codex.exe"],
                    hotkey: "Ctrl+F12",
                    mode: "continue",
                    continueText: "Дальше",
                    sendMode: "submit",
                    analysisSource: "localText"
                }
            }
        },
        catalog: [
            { slug: "known/model", label: "Known", kind: "text" },
            { slug: "vision/model", label: "Vision", kind: "" },
            { slug: "fallback/model", label: "", kind: "text" }
        ],
        contexts: {
            Code: {
                provider: "codex", model: "known/model", codexModel: "gpt-5.6-sol",
                codexReasoningEffort: "high",
                temperature: 0.2, maxTokens: 1000
            },
            Browser: {
                provider: "openrouter", model: "vision/model", codexModel: "",
                codexReasoningEffort: "", temperature: 0.2, maxTokens: 1000
            },
            Image: {
                provider: "openrouter", model: "vision/model", codexModel: "",
                codexReasoningEffort: "", temperature: 0.7, maxTokens: 2048
            },
            Text: { model: "custom/model", temperature: null, maxTokens: null }
        },
        prompts: {
            Code: { system: "system", user: "missing placeholders" },
            Image: { system: "image", user: "Improve {text}" }
        }
    };
}

test("integrated editor setup preserves edits, prevents duplicate requests and reports verified status in both languages", function () {
    const page = loadSettingsPage();
    const document = page.dom.window.document;
    try {
        page.send(fixture());
        const install = document.getElementById("installEditorAdapterButton");
        const check = document.getElementById("checkEditorAdapterButton");
        const status = document.getElementById("editorAdapterStatus");
        const update = value => page.send({ type: "editorAdapter", editorAdapter: { checked: true, ...value } });
        assert.equal(install.disabled, true);
        check.click();
        assert.equal(status.textContent, "Проверяю...");
        assert.equal(check.disabled, true);
        assert.equal(page.posted.at(-1).cmd, "checkEditorAdapter");
        update({ editorFound: false });
        assert.equal(status.textContent, "VS Code не найден");
        assert.equal(install.disabled, true);
        update({ editorFound: true, installed: false });
        assert.equal(install.disabled, false);
        document.getElementById("tokenInput").value = "unsaved-value";
        install.click();
        const count = page.posted.length;
        install.click();
        check.click();
        assert.equal(page.posted.length, count);
        assert.equal(page.posted.at(-1).cmd, "installEditorAdapter");
        assert.equal(status.textContent, "Установка...");
        update({ editorFound: true, error: "install-failed" });
        assert.equal(status.dataset.state, "error");
        assert.equal(status.textContent, "Установка адаптера не выполнена");
        assert.equal(check.disabled, false);
        assert.equal(install.disabled, true);
        update({ editorFound: true, error: "unexpected-code" });
        assert.equal(status.textContent, "Не удалось подключить адаптер");
        update({ editorFound: true, installed: true, current: false });
        assert.equal(install.textContent, "Обновить");
        assert.equal(status.textContent, "Доступно обновление адаптера");
        update({ editorFound: true, installed: true, current: true, reloadRequired: true });
        assert.equal(status.textContent, "Требуется перезапуск окна VS Code");
        assert.equal(install.disabled, true);
        assert.equal(install.hidden, true);
        assert.equal(document.getElementById("tokenInput").value, "unsaved-value");
        const language = document.getElementById("languageSelect");
        language.value = "en";
        language.dispatchEvent(new page.dom.window.Event("change"));
        assert.equal(status.textContent, "VS Code window reload required");
        update({ editorFound: true, installed: true, current: true });
        assert.equal(status.textContent, "Adapter installed");
    } finally { page.dom.window.close(); }
});

test("diagnostic logging is opt-in, survives incremental messages and posts a fixed folder command", function () {
    const page = loadSettingsPage();
    const document = page.dom.window.document;
    try {
        page.send(fixture());
        const input = document.getElementById("loggingInput");
        assert.equal(input.checked, false);
        input.checked = true;
        page.send({ type: "editorAdapter", editorAdapter: { editorFound: true, installed: true, current: true } });
        assert.equal(input.checked, true);
        document.getElementById("openLogsButton").click();
        assert.equal(page.posted[0].cmd, "openLogsFolder");
        document.getElementById("saveButton").click();
        assert.deepEqual(page.posted[1].payload.logging, { enabled: true });
        page.send({ ...fixture(), logging: { enabled: true } });
        assert.equal(input.checked, true);
    } finally { page.dom.window.close(); }
});

test("AI activity modes persist in both languages and old settings default to icon plus popup", function () {
    const page = loadSettingsPage();
    const document = page.dom.window.document;
    try {
        page.send(fixture());
        const select = document.getElementById("operationFeedbackSelect");
        assert.equal(select.value, "both");
        for (const language of ["ru", "en"]) {
            document.getElementById("languageSelect").value = language;
            document.getElementById("languageSelect").dispatchEvent(new page.dom.window.Event("change"));
            assert.equal(select.options[1].textContent, language === "en" ? "Tray icon only" : "Только иконка");
            for (const mode of ["both", "icon", "popup", "none"]) {
                select.value = mode;
                document.getElementById("saveButton").click();
                assert.equal(page.posted.at(-1).payload.general.operationFeedback, mode);
                const data = fixture();
                data.general.operationFeedback = mode;
                data.general.language = language;
                page.send(data);
                assert.equal(select.value, mode);
            }
        }
        const invalid = fixture();
        invalid.general.operationFeedback = "unknown-mode";
        page.send(invalid);
        assert.equal(select.value, "both");
    } finally { page.dom.window.close(); }
});

test("settings page renders, validates and posts edited payload", function () {
    const page = loadSettingsPage();
    const document = page.dom.window.document;
    try {
        page.send(fixture());

        assert.equal(document.getElementById("hotkeyInput").value, "Alt+F10");
        assert.equal(document.getElementById("hotkeyInput").readOnly, true);
        assert.equal(document.querySelectorAll(".hotkey-editor").length, 2);
        assert.equal(document.getElementById("backgroundInput").checked, false);
        assert.equal(document.getElementById("autostartInput").checked, true);
        assert.equal(document.getElementById("configAlert").hidden, false);
        assert.equal(document.getElementById("codexStatus").textContent,
            "Подписка ChatGPT подключена");
        assert.equal(document.querySelectorAll(".section-nav a").length, 6);
        assert.equal(document.querySelector(".about-identity"), null);
        assert.equal(document.body.textContent.includes("Роман Андриянов"), false);
        assert.equal(document.getElementById("authorTelegramButton").dataset.label,
            "Telegram @andromanpro");
        assert.equal(document.querySelectorAll(".ai-profile").length, 1);
        assert.equal(document.querySelector(".ai-profile").tagName, "DETAILS");
        assert.equal(document.querySelector(".ai-profile").open, true);
        assert.equal(document.querySelectorAll(".model-row").length, 4);
        assert.equal(document.querySelectorAll('[data-context="Code"] .model-cell-label').length, 4);
        assert.equal(document.getElementById("tokenToggleButton").classList.contains("secondary"), true);
        assert.equal(document.querySelector('[data-context="Code"] .provider-select').value, "codex");
        assert.equal(document.querySelector('[data-context="Code"] .codex-model-controls').hidden, false);
        assert.equal(document.querySelector('[data-context="Code"] .codex-model-select').value,
            "gpt-5.6-sol");
        assert.equal(document.querySelector('[data-context="Code"] .codex-reasoning-select').value,
            "high");
        assert.equal(document.querySelector('option[value="fallback/model"]').textContent,
            "fallback/model [text]");

        const generalHotkey = document.getElementById("hotkeyInput");
        generalHotkey.click();
        assert.equal(document.getElementById("hotkeyCaptureButton").textContent, "Отмена");
        document.dispatchEvent(new page.dom.window.KeyboardEvent("keydown", {
            key: "k", code: "KeyK", ctrlKey: true, shiftKey: true
        }));
        assert.equal(generalHotkey.value, "Ctrl+Shift+K");
        assert.equal(document.getElementById("hotkeyCaptureButton").textContent, "Изменить");

        const profile = document.querySelector(".ai-profile");
        const profileHotkey = profile.querySelector(".profile-hotkey");
        profileHotkey.click();
        document.dispatchEvent(new page.dom.window.KeyboardEvent("keydown", {
            key: "Control", code: "ControlLeft", ctrlKey: true
        }));
        assert.equal(profileHotkey.value, "Ctrl+");
        document.dispatchEvent(new page.dom.window.KeyboardEvent("keydown", { key: "Escape" }));
        assert.equal(profileHotkey.value, "Ctrl+F12");
        profileHotkey.click();
        document.dispatchEvent(new page.dom.window.KeyboardEvent("keydown", {
            key: "Dead", code: ""
        }));
        assert.equal(profile.querySelector(".hotkey-feedback").textContent,
            "Эта клавиша не поддерживается");
        profile.querySelector(".hotkey-capture-button").click();
        assert.equal(profileHotkey.value, "Ctrl+F12");

        document.getElementById("useCodexButton").click();
        assert.equal(document.querySelector('[data-context="Browser"] .provider-select').value,
            "codex");
        assert.equal(document.querySelector('[data-context="Image"] .provider-select').value,
            "codex");

        const tokenToggle = document.getElementById("tokenToggleButton");
        tokenToggle.click();
        assert.equal(document.getElementById("tokenInput").type, "text");
        tokenToggle.click();
        assert.equal(document.getElementById("tokenInput").type, "password");

        const mode = profile.querySelector(".profile-mode");
        mode.value = "nextStep";
        mode.dispatchEvent(new page.dom.window.Event("change"));
        profile.querySelector(".profile-analysis-source").value = "screenshot";
        assert.equal(profile.querySelector(".profile-continue-text").disabled, true);
        assert.equal(profile.querySelector(".profile-analysis-source").disabled, false);

        const customRow = document.querySelector('[data-context="Text"]');
        const customSelect = customRow.querySelector(".model-select");
        customSelect.value = "known/model";
        customSelect.dispatchEvent(new page.dom.window.Event("change"));
        assert.equal(customRow.querySelector(".custom-model-input").hidden, true);
        customSelect.value = "__custom__";
        customSelect.dispatchEvent(new page.dom.window.Event("change"));
        customRow.querySelector(".custom-model-input").value = " changed/model ";
        customRow.querySelector(".temperature-input").value = "not-a-number";
        customRow.querySelector(".max-tokens-input").value = "not-a-number";

        const promptSelect = document.getElementById("promptSelect");
        promptSelect.value = "Image";
        promptSelect.dispatchEvent(new page.dom.window.Event("change"));
        assert.equal(document.getElementById("userPromptInput").value, "Improve {text}");

        const save = document.getElementById("saveButton");
        let answers = [false];
        globalThis.confirm = function () { return answers.shift(); };
        save.click();
        assert.equal(page.posted.length, 0);

        answers = [true, false];
        save.click();
        assert.equal(page.posted.length, 0);

        answers = [true, true, false];
        save.click();
        assert.equal(page.posted.length, 0);

        answers = [true, true, true];
        const language = document.getElementById("languageSelect");
        language.value = "en";
        language.dispatchEvent(new page.dom.window.Event("change"));
        assert.equal(document.querySelector("h1").textContent, "Settings");
        assert.equal(document.querySelector("#aboutSection h2").textContent,
            "About the author");
        save.click();
        assert.equal(page.posted.length, 1);
        assert.equal(page.posted[0].cmd, "save");
        assert.equal(page.posted[0].payload.contexts.Text.model, "changed/model");
        assert.equal(page.posted[0].payload.contexts.Text.temperature, 0.5);
        assert.equal(page.posted[0].payload.contexts.Text.maxTokens, 2048);
        assert.equal(page.posted[0].payload.contexts.Code.provider, "codex");
        assert.equal(page.posted[0].payload.contexts.Code.codexModel, "gpt-5.6-sol");
        assert.equal(page.posted[0].payload.contexts.Code.codexReasoningEffort, "high");
        assert.equal(page.posted[0].payload.contexts.Browser.provider, "codex");
        assert.equal(page.posted[0].payload.general.language, "en");
        assert.equal(page.posted[0].payload.general.hotkey, "Ctrl+Shift+K");
        assert.equal(page.posted[0].payload.aiChat.profiles.codex.analysisSource, "screenshot");

        document.getElementById("cancelButton").click();
        document.getElementById("openConfigButton").click();
        document.getElementById("authorWebsiteButton").click();
        document.getElementById("authorTelegramButton").click();
        document.dispatchEvent(new page.dom.window.KeyboardEvent("keydown", { key: "Escape" }));
        assert.deepEqual(page.posted.slice(1).map(function (item) { return item.cmd; }),
            ["cancel", "openConfigFolder", "openAuthorWebsite", "openAuthorTelegram", "cancel"]);

        page.send(JSON.stringify({
            configError: false,
            general: {},
            aiChat: {},
            token: null,
            contexts: {},
            prompts: {},
            catalog: null
        }));
        assert.equal(document.getElementById("configAlert").hidden, true);
        assert.equal(document.getElementById("promptSelect").value, "");

        page.send(null);
        assert.equal(document.getElementById("promptSelect").value, "");

        delete page.dom.window.chrome;
        document.getElementById("cancelButton").click();
    } finally {
        page.dom.window.close();
        delete globalThis.window;
        delete globalThis.document;
        delete globalThis.confirm;
    }
});
