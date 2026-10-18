const zbsSettingsNormalize = (value) =>
    value === null || value === undefined ? "" : String(value);

const zbsSettingsCloneObject = (value) => {
    if (!value || typeof value !== "object") {
        return {};
    }

    return structuredClone(value);
};

(function (root, factory) {
    "use strict";

    const api = factory();
    if (typeof module === "object" && module.exports) {
        module.exports = api;
    } else {
        root.ZbsSettingsCore = api;
    }
}(typeof globalThis !== "undefined" ? globalThis : this, function () {
    "use strict";

    const defaultContinueText = "Давай дальше.";
    const defaultHotkey = "Ctrl+F12";
    const hotkeyCodeNames = Object.freeze({
        Backspace: "Backspace",
        Tab: "Tab",
        Enter: "Enter",
        Pause: "Pause",
        CapsLock: "CapsLock",
        Escape: "Escape",
        Space: "Space",
        PageUp: "PageUp",
        PageDown: "PageDown",
        End: "End",
        Home: "Home",
        ArrowLeft: "Left",
        ArrowUp: "Up",
        ArrowRight: "Right",
        ArrowDown: "Down",
        PrintScreen: "PrintScreen",
        Insert: "Insert",
        Delete: "Delete",
        NumpadMultiply: "Multiply",
        NumpadAdd: "Add",
        NumpadSubtract: "Subtract",
        NumpadDecimal: "Decimal",
        NumpadDivide: "Divide",
        Semicolon: "Semicolon",
        Equal: "Equal",
        Comma: "Comma",
        Minus: "Minus",
        Period: "Period",
        Slash: "Slash",
        Backquote: "Tilde",
        BracketLeft: "OpenBracket",
        Backslash: "Backslash",
        BracketRight: "CloseBracket",
        Quote: "Quote",
        AudioVolumeMute: "VolumeMute",
        AudioVolumeDown: "VolumeDown",
        AudioVolumeUp: "VolumeUp",
        MediaTrackNext: "MediaNextTrack",
        MediaTrackPrevious: "MediaPrevTrack",
        MediaStop: "MediaStop",
        MediaPlayPause: "MediaPlayPause"
    });
    const hotkeyKeyNames = Object.freeze({
        BACKSPACE: "Backspace",
        TAB: "Tab",
        ENTER: "Enter",
        PAUSE: "Pause",
        CAPSLOCK: "CapsLock",
        ESCAPE: "Escape",
        ESC: "Escape",
        " ": "Space",
        SPACEBAR: "Space",
        PAGEUP: "PageUp",
        PAGEDOWN: "PageDown",
        END: "End",
        HOME: "Home",
        ARROWLEFT: "Left",
        ARROWUP: "Up",
        ARROWRIGHT: "Right",
        ARROWDOWN: "Down",
        PRINTSCREEN: "PrintScreen",
        INSERT: "Insert",
        DELETE: "Delete"
    });
    const modifierKeys = Object.freeze(["ALT", "CONTROL", "META", "SHIFT"]);

    function hotkeyMainKey(event) {
        const source = event || {};
        const code = zbsSettingsNormalize(source.code);
        const key = zbsSettingsNormalize(source.key);
        const upperKey = key.toUpperCase();
        const functionKey = /^(?:F(?:[1-9]|1\d|2[0-4]))$/.exec(code.toUpperCase())
            || /^(?:F(?:[1-9]|1\d|2[0-4]))$/.exec(upperKey);
        if (functionKey) {
            return functionKey[0];
        }

        const letter = /^Key([A-Z])$/.exec(code);
        if (letter) {
            return letter[1];
        }

        const digit = /^Digit(\d)$/.exec(code);
        if (digit) {
            return digit[1];
        }

        const numpad = /^Numpad(\d)$/.exec(code);
        if (numpad) {
            return `Numpad${numpad[1]}`;
        }

        if (hotkeyCodeNames[code]) {
            return hotkeyCodeNames[code];
        }

        if (hotkeyKeyNames[upperKey]) {
            return hotkeyKeyNames[upperKey];
        }

        return /^[A-Z\d]$/.test(upperKey) ? upperKey : "";
    }

    function captureHotkeyEvent(event) {
        const source = event || {};
        const parts = [];
        if (source.ctrlKey) {
            parts.push("Ctrl");
        }
        if (source.altKey) {
            parts.push("Alt");
        }
        if (source.shiftKey) {
            parts.push("Shift");
        }
        if (source.metaKey) {
            parts.push("Win");
        }

        if (modifierKeys.includes(zbsSettingsNormalize(source.key).toUpperCase())) {
            return { status: "pending", value: parts.length ? `${parts.join("+")}+` : "" };
        }

        const mainKey = hotkeyMainKey(source);
        if (!mainKey) {
            return { status: "unsupported", value: "" };
        }

        parts.push(mainKey);
        return { status: "complete", value: parts.join("+") };
    }

    function normalizeChoice(value, allowed, fallback) {
        const normalized = zbsSettingsNormalize(value);
        return allowed.includes(normalized) ? normalized : fallback;
    }

    function normalizeProcesses(value) {
        const source = Array.isArray(value) ? value : zbsSettingsNormalize(value).split(",");
        const result = [];

        for (const item of source) {
            const process = zbsSettingsNormalize(item).trim().toLowerCase();
            if (process && !result.includes(process)) {
                result.push(process);
            }
        }

        return result;
    }

    function normalizeProfile(name, value) {
        const source = value && typeof value === "object" ? value : {};
        return {
            label: zbsSettingsNormalize(source.label).trim() || name,
            enabled: source.enabled !== false,
            processes: normalizeProcesses(source.processes),
            hotkey: zbsSettingsNormalize(source.hotkey).trim() || defaultHotkey,
            mode: normalizeChoice(source.mode, ["continue", "nextStep"], "continue"),
            continueText: zbsSettingsNormalize(source.continueText).trim() || defaultContinueText,
            sendMode: normalizeChoice(source.sendMode, ["submit", "draft"], "submit"),
            analysisSource: normalizeChoice(
                source.analysisSource, ["localText", "screenshot"], "localText")
        };
    }

    function normalizeProfiles(value) {
        const source = value && typeof value === "object" ? value : {};
        const result = {};
        for (const name of Object.keys(source)) {
            result[name] = normalizeProfile(name, source[name]);
        }
        return result;
    }

    function requiresScreenshotConfirmation(previous, next) {
        const oldProfiles = normalizeProfiles(previous);
        const newProfiles = normalizeProfiles(next);
        for (const name of Object.keys(newProfiles)) {
            const oldProfile = oldProfiles[name] || {};
            const newProfile = newProfiles[name];
            if (newProfile.enabled
                && newProfile.mode === "nextStep"
                && newProfile.analysisSource === "screenshot"
                && (!oldProfile.enabled
                    || oldProfile.mode !== "nextStep"
                    || oldProfile.analysisSource !== "screenshot")) {
                return true;
            }
        }

        return false;
    }

    function validatePrompts(prompts) {
        const source = prompts || {};
        const codeUser = zbsSettingsNormalize(source.Code?.user);
        const imageUser = zbsSettingsNormalize(source.Image?.user);
        return {
            codeMissingPlaceholders: codeUser.length > 0
                && (!codeUser.includes("{text}") || !codeUser.includes("{language}")),
            imageContainsTextPlaceholder: imageUser.includes("{text}")
        };
    }

    return {
        cloneObject: zbsSettingsCloneObject,
        captureHotkeyEvent: captureHotkeyEvent,
        defaultContinueText: defaultContinueText,
        defaultHotkey: defaultHotkey,
        normalize: zbsSettingsNormalize,
        normalizeProcesses: normalizeProcesses,
        normalizeProfile: normalizeProfile,
        normalizeProfiles: normalizeProfiles,
        requiresScreenshotConfirmation: requiresScreenshotConfirmation,
        validatePrompts: validatePrompts
    };
}));
