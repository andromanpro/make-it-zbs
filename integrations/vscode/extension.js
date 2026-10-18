"use strict";

const crypto = require("node:crypto");
const fs = require("node:fs");
const net = require("node:net");
const os = require("node:os");
const path = require("node:path");
const process = require("node:process");
const { Buffer } = require("node:buffer");
const { createEditorSessions } = require("./editor-sessions");

function activate(context) {
    if (os.platform() !== "win32") {
        return;
    }
    const vscode = require("vscode");
    const directory = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local"),
        "MakeItZBS", "EditorBridge");
    fs.mkdirSync(directory, { recursive: true });
    const nonce = crypto.randomBytes(16).toString("hex");
    const token = crypto.randomBytes(32).toString("hex");
    const pipe = "make-it-zbs-vscode-" + nonce;
    const descriptor = path.join(directory, "vscode-" + process.pid + "-" + nonce + ".json");
    const sessions = createEditorSessions(vscode);
    const sockets = new Set();
    const server = net.createServer(socket => {
        if (sockets.size >= 32) {
            socket.destroy();
            return;
        }
        sockets.add(socket);
        let data = "";
        let received = false;
        socket.setEncoding("utf8");
        socket.setTimeout(3000, () => socket.destroy());
        socket.on("error", () => socket.destroy());
        socket.on("close", () => sockets.delete(socket));
        socket.on("data", async chunk => {
            if (received) {
                return;
            }
            data += chunk;
            if (Buffer.byteLength(data, "utf8") > 5 * 1024 * 1024) {
                socket.destroy();
                return;
            }
            const end = data.indexOf("\n");
            if (end < 0) {
                return;
            }
            received = true;
            try {
                const message = JSON.parse(data.slice(0, end));
                if (typeof message.token !== "string" || message.token.length !== token.length
                    || !crypto.timingSafeEqual(Buffer.from(message.token), Buffer.from(token))) {
                    socket.destroy();
                    return;
                }
                const reply = await sessions.handle(message, () => socket.destroyed);
                if (!socket.destroyed) {
                    socket.end(JSON.stringify(reply) + "\n");
                }
            } catch {
                socket.destroy();
            }
        });
    });
    function dispose() {
        for (const socket of sockets) {
            socket.destroy();
        }
        if (server.listening) {
            server.close();
        }
        sessions.dispose();
        fs.rmSync(descriptor, { force: true });
    }
    server.on("error", dispose);
    server.listen("\\\\.\\pipe\\" + pipe, () => {
        try {
            fs.writeFileSync(descriptor, JSON.stringify({ protocol: 1, pipe, token, pid: process.pid }), { mode: 0o600 });
        } catch {
            dispose();
        }
    });
    context.subscriptions.push({ dispose });
}

module.exports = { activate };
