"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const process = require("node:process");
const test = require("node:test");
const { JSDOM } = require("jsdom");

function loadPreviewPage() {
    const html = fs.readFileSync(path.join(process.cwd(), "ui", "preview.html"), "utf8");
    const dom = new JSDOM(html, { url: "https://zbs.ui/preview.html" });
    const posted = [];
    let messageHandler = null;
    dom.window.chrome = {
        webview: {
            addEventListener: function (name, handler) {
                if (name === "message") {
                    messageHandler = handler;
                }
            },
            postMessage: function (message) {
                posted.push(JSON.parse(message));
            }
        }
    };
    globalThis.window = dom.window;
    globalThis.document = dom.window.document;
    delete require.cache[require.resolve("../../ui/i18n.js")];
    dom.window.ZbsI18n = require("../../ui/i18n.js");
    delete require.cache[require.resolve("../../ui/preview.js")];
    require("../../ui/preview.js");
    return {
        dom: dom,
        posted: posted,
        send: function (data) { messageHandler({ data: data }); }
    };
}

test("preview page handles buffered and streaming host messages", function () {
    const page = loadPreviewPage();
    const document = page.dom.window.document;
    const event = page.dom.window.Event;
    try {
        page.send({
            original: "same\nold",
            result: "same\nnew",
            context: "Code",
            model: "model-a",
            allowApply: true
        });
        assert.equal(document.querySelector("#codeBlock code").textContent, "same\nnew");
        assert.equal(document.getElementById("codeBlock").classList.contains("code-content"), true);
        assert.equal(document.getElementById("codeBlock").classList.contains("prose-content"), false);
        assert.equal(document.getElementById("contextValue").textContent, "Code");
        assert.equal(document.getElementById("applyButton").hidden, false);

        document.querySelector('[data-view="diff"]').click();
        const diff = Array.from(document.querySelectorAll(".diff-line"));
        assert.deepEqual(diff.map(function (line) { return line.textContent; }),
            ["  same", "- old", "+ new"]);
        document.querySelector('[data-view="original"]').click();
        assert.equal(document.querySelector("#codeBlock code").textContent, "same\nold");

        page.send({ original: "chat", result: "answer", context: "AIChat", model: null });
        assert.equal(document.getElementById("applyButton").textContent, "Отправить");
        assert.equal(document.querySelector('[data-view="diff"]').hidden, true);
        assert.equal(document.getElementById("codeBlock").classList.contains("code-content"), false);
        assert.equal(document.getElementById("codeBlock").classList.contains("prose-content"), true);

        page.send({
            original: "chat", result: "answer", context: "AIChat", model: "Codex",
            language: "en", allowApply: true
        });
        assert.equal(document.getElementById("applyButton").textContent, "Send");
        assert.equal(document.querySelector('[data-view="result"]').textContent, "Result");
        assert.equal(document.documentElement.lang, "en");

        page.send({
            streaming: true,
            original: null,
            context: "Code",
            model: "stream-model",
            allowApply: true
        });
        assert.equal(document.getElementById("spinner").hidden, false);
        assert.equal(document.getElementById("reasoningTab").hidden, false);
        page.send({ type: "reasoning", text: "thinking" });
        page.send({ type: "error", text: "" });
        page.send({ type: "error", text: "temporary" });
        assert.match(document.querySelector("#reasoningBlock code").textContent, /temporary/);
        page.send({ type: "content", text: "part 1" });
        document.querySelector('[data-view="result"]').click();
        page.send({ type: "content", text: " + part 2" });
        assert.equal(document.querySelector("#codeBlock code").textContent, "part 1 + part 2");
        page.send({ type: "done" });
        assert.equal(document.getElementById("spinner").hidden, true);
        assert.equal(document.getElementById("applyButton").hidden, false);

        document.getElementById("copyButton").click();
        document.getElementById("applyButton").click();
        document.getElementById("cancelButton").click();
        document.dispatchEvent(new page.dom.window.KeyboardEvent("keydown", { key: "Escape" }));
        assert.deepEqual(page.posted.map(function (item) { return item.cmd; }),
            ["copy", "apply", "cancel", "cancel"]);

        delete page.dom.window.chrome;
        document.getElementById("copyButton").dispatchEvent(new event("click"));
    } finally {
        page.dom.window.close();
        delete globalThis.window;
        delete globalThis.document;
    }
});

test("large replacements use a labelled bounded diff without losing text", function () {
    const page = loadPreviewPage();
    const document = page.dom.window.document;
    const original = Array.from({ length: 1500 }, (_, i) => "before " + i).join("\n");
    const result = Array.from({ length: 1500 }, (_, i) => "after " + i).join("\n");
    try {
        page.send({ original, result, context: "Code", language: "en" });
        document.querySelector('[data-view="diff"]').click();
        assert.match(document.querySelector('#diffBlock [role="status"]')?.textContent || "", /Simplified/);
        assert.equal(document.querySelectorAll(".diff-removed").length, 1500);
        assert.equal(document.querySelectorAll(".diff-added").length, 1500);
        assert.equal(document.querySelector(".diff-removed").textContent, "- before 0");
        assert.equal(document.querySelector(".diff-added:last-child").textContent, "+ after 1499");
    } finally {
        page.dom.window.close();
        delete globalThis.window;
        delete globalThis.document;
    }
});

test("a small edit in a large module retains precise context", function () {
    const page = loadPreviewPage();
    const document = page.dom.window.document;
    const prefix = Array.from({ length: 750 }, (_, i) => "prefix " + i);
    const suffix = Array.from({ length: 750 }, (_, i) => "suffix " + i);
    try {
        page.send({
            original: [...prefix, "old", ...suffix].join("\n"),
            result: [...prefix, "new", ...suffix].join("\n"), context: "OneCCode"
        });
        document.querySelector('[data-view="diff"]').click();
        assert.equal(document.querySelectorAll(".diff-unchanged").length, 1500);
        assert.equal(document.querySelectorAll(".diff-removed").length, 1);
        assert.equal(document.querySelectorAll(".diff-added").length, 1);
        assert.equal(document.querySelector('#diffBlock [role="status"]'), null);
    } finally {
        page.dom.window.close();
        delete globalThis.window;
        delete globalThis.document;
    }
});

test("number review requires fresh consent for buffered and streamed results", function () {
    const page = loadPreviewPage();
    const document = page.dom.window.document;
    const consent = document.getElementById("integrityAcknowledged");
    const apply = document.getElementById("applyButton");
    const review = { requiresReview: true, removed: ["100", "05.09.2026"], added: ["1000", "09.05.2026"] };
    try {
        page.send({ original: "100", result: "1000", context: "Email", allowApply: true, language: "en", integrity: review });
        assert.equal(document.getElementById("integrityReview").hidden, false);
        assert.match(document.getElementById("integrityDetails").textContent, /Removed.*05.09.2026.*Added.*1000/);
        assert.equal(apply.disabled, true);
        apply.click();
        assert.equal(page.posted.length, 0);
        consent.click();
        assert.equal(apply.disabled, false);
        apply.click();
        assert.deepEqual(page.posted[0], { cmd: "apply", integrityAcknowledged: true });
        consent.click();
        assert.equal(apply.disabled, true);

        page.send({ streaming: true, original: "100", context: "Email", allowApply: true });
        assert.equal(document.getElementById("integrityReview").hidden, true);
        assert.equal(consent.checked, false);
        page.send({ type: "content", text: "1000" });
        page.send({ type: "done", integrity: review });
        assert.equal(apply.disabled, true);
        assert.match(document.getElementById("integrityTitle").textContent, /цифровые даты/);
        document.getElementById("copyButton").click();
        assert.equal(page.posted.at(-1).cmd, "copy");

        page.send({ result: "123", context: "Text", integrity: { requiresReview: true, unavailable: true } });
        assert.equal(document.getElementById("integrityConsent").hidden, true);
        assert.match(document.getElementById("integrityTitle").textContent, /не удалось/);
        page.send({ result: "No numbers", context: "Text", allowApply: true });
        assert.equal(apply.disabled, false);
        assert.equal(document.getElementById("integrityReview").hidden, true);
        page.send({ result: "Preserved answer", context: "Code", allowApply: false, notice: "Source changed" });
        assert.equal(apply.hidden, true);
        assert.equal(document.getElementById("previewNotice").hidden, false);
        assert.equal(document.getElementById("previewNotice").textContent, "Source changed");
    } finally {
        page.dom.window.close();
        delete globalThis.window;
        delete globalThis.document;
    }
});
