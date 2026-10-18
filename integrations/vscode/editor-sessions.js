"use strict";

const crypto = require("node:crypto");
const MAX_TEXT = 1000000;
const EXPIRY_MS = 10 * 60 * 1000;

function fingerprint(text) {
    return crypto.createHash("sha256").update(text.replaceAll("\r\n", "\n").replaceAll("\r", "\n")).digest("base64");
}

function sameSelection(editor, saved) {
    return saved.mode === "fullModule" || (editor.selections.length === 1
        && editor.document.offsetAt(editor.selection.start) === saved.start
        && editor.document.offsetAt(editor.selection.end) === saved.end);
}

function createEditorSessions(vscode, now = Date.now) {
    const captures = new Map();
    function capture(mode) {
        const editor = vscode.window.activeTextEditor;
        if (!vscode.window.state.focused || !editor || editor.document.isClosed) {
            return { ok: false, error: "editor-unavailable" };
        }
        if (mode !== "selection" && mode !== "fullModule") {
            return { ok: false, error: "unsupported-mode" };
        }
        const text = editor.document.getText();
        const start = mode === "fullModule" ? 0 : editor.document.offsetAt(editor.selection.start);
        const end = mode === "fullModule" ? text.length : editor.document.offsetAt(editor.selection.end);
        if (text.length > MAX_TEXT || start === end || editor.selections.length !== 1) {
            return { ok: false, error: "unsupported-selection" };
        }
        for (const [id, saved] of captures) {
            if (now() - saved.created > EXPIRY_MS) {
                captures.delete(id);
            }
        }
        if (captures.size >= 32) {
            captures.delete(captures.keys().next().value);
        }
        const saved = { captureId: crypto.randomUUID(), uri: editor.document.uri.toString(),
            version: editor.document.version, start, end, mode, document: fingerprint(text),
            selection: fingerprint(text.slice(start, end)), created: now() };
        captures.set(saved.captureId, saved);
        return { ok: true, ...saved };
    }

    async function apply(message, cancelled) {
        const saved = captures.get(message.captureId);
        captures.delete(message.captureId);
        if (!saved || now() - saved.created > EXPIRY_MS || cancelled()) {
            return { ok: false, error: "capture-expired" };
        }
        const editor = vscode.window.activeTextEditor;
        if (!vscode.window.state.focused || !editor || editor.document.isClosed
            || editor.document.uri.toString() !== saved.uri || editor.document.version !== saved.version) {
            return { ok: false, error: "document-changed" };
        }
        if (typeof message.text !== "string" || message.text.length === 0 || message.text.length > MAX_TEXT) {
            return { ok: false, error: "invalid-result" };
        }
        const document = editor.document;
        const before = document.getText();
        if (fingerprint(before) !== saved.document || !sameSelection(editor, saved)) {
            return { ok: false, error: "source-changed" };
        }
        const range = new vscode.Range(document.positionAt(saved.start), document.positionAt(saved.end));
        const expected = before.slice(0, saved.start) + message.text + before.slice(saved.end);
        const success = await editor.edit(builder => builder.replace(range, message.text),
            { undoStopBefore: true, undoStopAfter: true });
        return success && fingerprint(document.getText()) === fingerprint(expected)
            ? { ok: true } : { ok: false, error: "edit-not-confirmed" };
    }

    return {
        async handle(message, cancelled = () => false) {
            if (message.cmd === "capture") {
                return capture(message.mode);
            }
            if (message.cmd === "apply") {
                return apply(message, cancelled);
            }
            if (message.cmd === "release") {
                captures.delete(message.captureId);
                return { ok: true };
            }
            return { ok: false, error: "unknown-command" };
        },
        dispose() { captures.clear(); }
    };
}

module.exports = { createEditorSessions };
