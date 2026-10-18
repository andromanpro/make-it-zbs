"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { EventEmitter } = require("node:events");
const { Buffer } = require("node:buffer");
const sourcePath = path.resolve(__dirname, "../../integrations/vscode/extension.js");

function fixture(platform = "win32", denied = false) {
    const server = new EventEmitter();
    server.listening = false;
    server.close = () => { server.listening = false; };
    server.listen = (_name, ready) => { server.listening = true; ready(); };
    const record = { disposed: 0, removed: 0, handled: 0 };
    const module = { exports: {} };
    const dependencies = {
        "node:os": { platform: () => platform, homedir: () => "F:/fixture" },
        "node:fs": { mkdirSync() {}, writeFileSync(_file, value) {
            if (denied) { throw new Error("fixture access denied"); }
            record.descriptor = JSON.parse(value);
        }, rmSync() { record.removed++; } },
        "node:net": { createServer(handler) { record.connect = handler; return server; } },
        "node:process": { pid: 1, env: {} },
        "./editor-sessions": { createEditorSessions() { return { dispose() { record.disposed++; },
            async handle(message, cancelled) {
                record.handled++;
                if (message.cmd === "fail") { throw new Error("fixture"); }
                if (message.cmd === "disconnect") { record.last.destroy(); }
                return { ok: !cancelled() };
            } }; } },
        "vscode": {}
    };
    vm.runInNewContext(fs.readFileSync(sourcePath, "utf8"), {
        module, require: name => dependencies[name] || require(name)
    }, { filename: sourcePath });
    const context = { subscriptions: [] };
    module.exports.activate(context);
    function socket() {
        const value = new EventEmitter();
        value.destroyed = false;
        value.setEncoding = () => {};
        value.setTimeout = (_time, action) => { value.timeout = action; };
        value.destroy = () => { value.destroyed = true; value.emit("close"); };
        value.end = reply => { value.reply = JSON.parse(reply); value.destroy(); };
        record.connect(value);
        record.last = value;
        return value;
    }
    return { record, server, context, socket };
}

async function send(socket, data) {
    socket.emit("data", data);
    await new Promise(resolve => globalThis.setImmediate(resolve));
}

test("VS Code extension authenticates split requests and dispatches once", async () => {
    const f = fixture();
    const socket = f.socket();
    const request = JSON.stringify({ cmd: "capture", token: f.record.descriptor.token }) + "\n";
    await send(socket, request.slice(0, 12));
    assert.equal(f.record.handled, 0);
    await send(socket, request.slice(12));
    assert.equal(socket.reply.ok, true);
    await send(socket, request);
    assert.equal(f.record.handled, 1);
    f.context.subscriptions[0].dispose();
    assert.equal(f.server.listening, false);
    assert.equal(f.record.disposed, 1);
});

test("VS Code extension refuses malformed, wrong, oversize and disconnected requests", async () => {
    const f = fixture();
    for (const data of ["bad-json\n", "{}\n", JSON.stringify({ token: "x".repeat(64) }) + "\n",
        "x".repeat(5 * 1024 * 1024 + 1)]) {
        const socket = f.socket();
        await send(socket, data);
        assert.equal(socket.destroyed, true);
    }
    for (const cmd of ["fail", "disconnect"]) {
        const socket = f.socket();
        await send(socket, JSON.stringify({ cmd, token: f.record.descriptor.token }) + "\n");
        assert.equal(socket.destroyed, true);
    }
    const timeout = f.socket();
    timeout.timeout();
    assert.equal(timeout.destroyed, true);
    const failed = f.socket();
    failed.emit("error", new Error("fixture"));
    assert.equal(failed.destroyed, true);
    f.context.subscriptions[0].dispose();
});

test("VS Code extension bounds sockets and cleans up on server errors", () => {
    const f = fixture();
    const pending = Array.from({ length: 32 }, () => f.socket());
    assert.equal(f.socket().destroyed, true);
    f.server.emit("error", new Error("fixture"));
    assert.equal(pending.every(socket => socket.destroyed), true);
    f.context.subscriptions[0].dispose();
    assert.equal(f.record.removed, 2);
});

test("VS Code extension does not activate on another OS and closes if descriptor cannot be written", () => {
    assert.equal(fixture("linux").context.subscriptions.length, 0);
    const denied = fixture("win32", true);
    assert.equal(denied.server.listening, false);
    assert.equal(denied.record.disposed, 1);
    assert.equal(Buffer.byteLength("fixture"), 7);
});
