"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const test = require("node:test");
const vm = require("node:vm");
const core = require("../../ui/settings-core.js");

test("normalizeProfile applies defaults and validates choices", function () {
    assert.deepEqual(core.normalizeProfile("codex", {
        enabled: false,
        processes: "CODEX.EXE, codex.exe, chatgpt.exe",
        mode: "bad",
        sendMode: "bad",
        analysisSource: "bad"
    }), {
        label: "codex",
        enabled: false,
        processes: ["codex.exe", "chatgpt.exe"],
        hotkey: "Ctrl+F12",
        mode: "continue",
        continueText: "Давай дальше.",
        sendMode: "submit",
        analysisSource: "localText"
    });
});

test("normalizeProfile preserves valid profile settings", function () {
    const profile = core.normalizeProfile("claude", {
        label: " Claude ",
        enabled: true,
        processes: ["claude.exe"],
        hotkey: " Alt+F11 ",
        mode: "nextStep",
        continueText: " Продолжай ",
        sendMode: "draft",
        analysisSource: "screenshot"
    });
    assert.equal(profile.label, "Claude");
    assert.equal(profile.hotkey, "Alt+F11");
    assert.equal(profile.mode, "nextStep");
    assert.equal(profile.continueText, "Продолжай");
    assert.equal(profile.sendMode, "draft");
    assert.equal(profile.analysisSource, "screenshot");
});

test("screenshot confirmation is required only for newly enabled vision mode", function () {
    const local = { codex: { mode: "nextStep", analysisSource: "localText" } };
    const screenshot = { codex: { mode: "nextStep", analysisSource: "screenshot" } };
    assert.equal(core.requiresScreenshotConfirmation(local, screenshot), true);
    assert.equal(core.requiresScreenshotConfirmation(screenshot, screenshot), false);
    assert.equal(core.requiresScreenshotConfirmation({}, {
        codex: { enabled: false, mode: "nextStep", analysisSource: "screenshot" }
    }), false);
});

test("re-enabling a disabled screenshot profile requires consent again", function () {
    const profile = { enabled: false, mode: "nextStep", analysisSource: "screenshot" };
    assert.equal(core.requiresScreenshotConfirmation(
        { codex: profile }, { codex: { ...profile, enabled: true } }), true);
    assert.equal(core.requiresScreenshotConfirmation(
        { codex: profile }, { codex: profile }), false);
});

test("prompt validation detects both guardrail violations", function () {
    assert.deepEqual(core.validatePrompts({
        Code: { user: "Refactor {text}" },
        Image: { user: "Improve {text}" }
    }), {
        codeMissingPlaceholders: true,
        imageContainsTextPlaceholder: true
    });
    assert.deepEqual(core.validatePrompts({
        Code: { user: "Refactor {text} as {language}" },
        Image: { user: "Improve image" }
    }), {
        codeMissingPlaceholders: false,
        imageContainsTextPlaceholder: false
    });
});

test("keyboard events are normalized to supported hotkey strings", function () {
    assert.deepEqual(core.captureHotkeyEvent({
        key: "k", code: "KeyK", ctrlKey: true, altKey: true, shiftKey: true, metaKey: true
    }), { status: "complete", value: "Ctrl+Alt+Shift+Win+K" });
    assert.deepEqual(core.captureHotkeyEvent({
        key: "F11", code: "F11", altKey: true
    }), { status: "complete", value: "Alt+F11" });
    assert.deepEqual(core.captureHotkeyEvent({
        key: "2", code: "Digit2"
    }), { status: "complete", value: "2" });
    assert.deepEqual(core.captureHotkeyEvent({
        key: "2", code: "Numpad2", metaKey: true
    }), { status: "complete", value: "Win+Numpad2" });
    assert.deepEqual(core.captureHotkeyEvent({
        key: ";", code: "Semicolon"
    }), { status: "complete", value: "Semicolon" });
    assert.deepEqual(core.captureHotkeyEvent({
        key: "ArrowLeft", code: ""
    }), { status: "complete", value: "Left" });
    assert.deepEqual(core.captureHotkeyEvent({
        key: "z", code: ""
    }), { status: "complete", value: "Z" });
    assert.deepEqual(core.captureHotkeyEvent({
        key: "F24", code: ""
    }), { status: "complete", value: "F24" });
    assert.deepEqual(core.captureHotkeyEvent({
        key: "Control", code: "ControlLeft", ctrlKey: true
    }), { status: "pending", value: "Ctrl+" });
    assert.deepEqual(core.captureHotkeyEvent({
        key: "Shift", code: "ShiftLeft"
    }), { status: "pending", value: "" });
    assert.deepEqual(core.captureHotkeyEvent({ key: "Dead", code: "" }), {
        status: "unsupported", value: ""
    });
    assert.deepEqual(core.captureHotkeyEvent(null), {
        status: "unsupported", value: ""
    });
});

test("cloneObject isolates objects and handles non-objects", function () {
    const source = { nested: { value: 1 } };
    const clone = core.cloneObject(source);
    clone.nested.value = 2;
    assert.equal(source.nested.value, 1);
    assert.deepEqual(core.cloneObject(null), {});
    assert.deepEqual(core.cloneObject("text"), {});
    assert.equal(core.normalize(null), "");
    assert.equal(core.normalize(undefined), "");
    assert.equal(core.normalize(42), "42");
});

test("normalizers handle empty and primitive profile collections", function () {
    assert.deepEqual(core.normalizeProfile("custom", null), {
        label: "custom",
        enabled: true,
        processes: [],
        hotkey: "Ctrl+F12",
        mode: "continue",
        continueText: "Давай дальше.",
        sendMode: "submit",
        analysisSource: "localText"
    });
    assert.deepEqual(core.normalizeProfiles(null), {});
    assert.deepEqual(core.normalizeProfiles("invalid"), {});
    assert.deepEqual(core.validatePrompts(null), {
        codeMissingPlaceholders: false,
        imageContainsTextPlaceholder: false
    });
    assert.equal(core.requiresScreenshotConfirmation({}, {
        codex: { enabled: true, mode: "continue", analysisSource: "screenshot" }
    }), false);
    assert.equal(core.requiresScreenshotConfirmation({}, {
        codex: { enabled: true, mode: "nextStep", analysisSource: "localText" }
    }), false);
});

test("browser UMD branch publishes settings core globally", function () {
    const file = path.resolve(__dirname, "../../ui/settings-core.js");
    const source = fs.readFileSync(file, "utf8");
    const browser = {};
    vm.runInNewContext(source, browser, { filename: file });
    assert.equal(browser.ZbsSettingsCore.defaultHotkey, "Ctrl+F12");
    assert.deepEqual(
        JSON.parse(JSON.stringify(browser.ZbsSettingsCore.normalizeProfiles(null))), {});
});
