"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { createEditorSessions } = require("../../integrations/vscode/editor-sessions");

function fixture() {
    let text = "one two three";
    let time = 100;
    const document = { version: 1, isClosed: false, uri: { toString: () => "file:///test.bsl" },
        getText: () => text, offsetAt: value => value, positionAt: value => value };
    const editor = { document, selection: { start: 4, end: 7 }, selections: [{}],
        async edit(callback, options) {
            assert.deepEqual(options, { undoStopBefore: true, undoStopAfter: true });
            callback({ replace(range, value) {
                text = text.slice(0, range.start) + value + text.slice(range.end);
                document.version++;
            } });
            return true;
        } };
    const vscode = { window: { state: { focused: true }, activeTextEditor: editor },
        Range: class Range { constructor(start, end) { this.start = start; this.end = end; } } };
    const sessions = createEditorSessions(vscode, () => time);
    return { document, editor, vscode, sessions, setText(value) { text = value; }, advance() { time += 600001; },
        capture: mode => sessions.handle({ cmd: "capture", mode: mode || "selection" }),
        apply: (saved, value = "new") => sessions.handle({ cmd: "apply", captureId: saved.captureId, text: value }) };
}

test("VS Code replaces exactly the captured range, with one undo transaction and one-use admission", async () => {
    const f = fixture();
    const saved = await f.capture();
    assert.equal(saved.ok, true);
    assert.equal(saved.uri, "file:///test.bsl");
    assert.equal(saved.version, 1);
    assert.equal(Object.values(saved).includes("one two three"), false);
    assert.equal((await f.apply(saved)).ok, true);
    assert.equal(f.document.getText(), "one new three");
    assert.equal((await f.apply(saved)).error, "capture-expired");
});

const conflicts = {
    "another tab": f => { f.document.uri = { toString: () => "file:///other.bsl" }; },
    "edit and undo with identical text": f => { f.document.version += 2; },
    "closed document": f => { f.document.isClosed = true; },
    "lost window focus": f => { f.vscode.window.state.focused = false; },
    "no editor": f => { f.vscode.window.activeTextEditor = null; },
    "moved selection": f => { f.editor.selection = { start: 0, end: 3 }; },
    "multiple selections": f => { f.editor.selections.push({}); },
    "unversioned content change": f => { f.setText("one bad three"); },
    "expired lease": f => f.advance()
};
for (const [name, mutate] of Object.entries(conflicts)) {
    test("VS Code refuses " + name + " without changing text", async () => {
        const f = fixture();
        const saved = await f.capture();
        mutate(f);
        const before = f.document.getText();
        assert.equal((await f.apply(saved)).ok, false);
        assert.equal(f.document.getText(), before);
        assert.equal((await f.apply(saved)).error, "capture-expired");
    });
}

test("VS Code validates capture modes, size and selection", async () => {
    const f = fixture();
    assert.equal((await f.capture("other")).error, "unsupported-mode");
    f.editor.selection.end = 4;
    assert.equal((await f.capture()).error, "unsupported-selection");
    f.setText("x".repeat(1000001));
    assert.equal((await f.capture("fullModule")).ok, false);
    f.vscode.window.activeTextEditor = null;
    assert.equal((await f.capture()).error, "editor-unavailable");
});

test("VS Code full module mode ignores selection moves and normalizes Unicode/newlines", async () => {
    const f = fixture();
    f.setText("Процедура Тест()\r\nКонецПроцедуры");
    const saved = await f.capture("fullModule");
    f.editor.selection = { start: 0, end: 0 };
    assert.equal((await f.apply(saved, "Процедура Новая()\nКонецПроцедуры")).ok, true);
    assert.equal(f.document.getText(), "Процедура Новая()\nКонецПроцедуры");
});

test("VS Code rejects invalid results, cancelled work, failed edits and unconfirmed edits", async () => {
    const f = fixture();
    for (const text of [null, "", "x".repeat(1000001)]) {
        assert.equal((await f.apply(await f.capture(), text)).error, "invalid-result");
    }
    const saved = await f.capture();
    assert.equal((await f.sessions.handle({ cmd: "apply", captureId: saved.captureId, text: "new" }, () => true)).ok, false);
    f.editor.edit = async () => false;
    assert.equal((await f.apply(await f.capture())).error, "edit-not-confirmed");
    f.editor.edit = async () => true;
    assert.equal((await f.apply(await f.capture())).error, "edit-not-confirmed");
});

test("VS Code bounds, expires and releases capture hashes", async () => {
    const f = fixture();
    const first = await f.capture();
    for (let i = 0; i < 32; i++) { await f.capture(); }
    assert.equal((await f.apply(first)).ok, false);
    const second = await f.capture();
    f.advance();
    const third = await f.capture();
    assert.equal((await f.apply(second)).ok, false);
    assert.equal((await f.sessions.handle({ cmd: "release", captureId: third.captureId })).ok, true);
    assert.equal((await f.apply(third)).ok, false);
    const fourth = await f.capture();
    f.sessions.dispose();
    assert.equal((await f.apply(fourth)).ok, false);
    assert.equal((await f.sessions.handle({ cmd: "unknown" })).error, "unknown-command");
});
