    const state = {
        original: "",
        result: "",
        context: "",
        model: "",
        activeView: "result",
        streaming: false,
        allowApply: false,
        language: "ru"
    };

    const translations = window.ZbsI18n;

    const codeBlock = document.getElementById("codeBlock");
    const codeElement = document.querySelector("#codeBlock code");
    const diffBlock = document.getElementById("diffBlock");
    const reasoningTab = document.getElementById("reasoningTab");
    const reasoningBlock = document.getElementById("reasoningBlock");
    const reasoningCode = document.querySelector("#reasoningBlock code");
    const spinner = document.getElementById("spinner");
    const contextValue = document.getElementById("contextValue");
    const modelValue = document.getElementById("modelValue");
    const tabs = document.querySelectorAll(".tab");
    const applyButton = document.getElementById("applyButton");
    const copyButton = document.getElementById("copyButton");
    const cancelButton = document.getElementById("cancelButton");
    const integrityReview = document.getElementById("integrityReview");
    const integrityAcknowledged = document.getElementById("integrityAcknowledged");
    const previewNotice = document.getElementById("previewNotice");

    function renderIntegrity(review) {
        integrityAcknowledged.checked = false;
        integrityReview.hidden = review?.requiresReview !== true;
        document.getElementById("integrityConsent").hidden = !state.allowApply;
        document.getElementById("integrityTitle").textContent = t(review?.unavailable
            ? "preview.integrityUnavailable" : "preview.integrityTitle");
        const details = [];
        if (review?.removed?.length) {
            details.push(t("preview.integrityRemoved") + review.removed.join(", "));
        }
        if (review?.added?.length) {
            details.push(t("preview.integrityAdded") + review.added.join(", "));
        }
        document.getElementById("integrityDetails").textContent = details.join(" | ");
        applyButton.disabled = !integrityReview.hidden;
    }

    function normalize(value) {
        return value === null || value === undefined ? "" : String(value);
    }

    function t(key) {
        const language = state.language === "en" ? "en" : "ru";
        return translations[language][key] || translations.ru[key] || key;
    }

    function applyTranslations() {
        document.documentElement.lang = state.language;
        document.title = state.language === "en" ? "Make it ZBS Preview" : "Предпросмотр Make it ZBS";
        for (const element of document.querySelectorAll("[data-i18n]")) {
            element.textContent = t(element.dataset.i18n);
        }
        for (const element of document.querySelectorAll("[data-i18n-aria-label]")) {
            element.setAttribute("aria-label", t(element.dataset.i18nAriaLabel));
        }
    }

    function splitLines(value) {
        const text = normalize(value).replaceAll("\r\n", "\n").replaceAll("\r", "\n");
        return text.length === 0 ? [] : text.split("\n");
    }

    function buildDiffRows(originalText, resultText) {
        const originalLines = splitLines(originalText);
        const resultLines = splitLines(resultText);
        let start = 0;
        while (start < originalLines.length && start < resultLines.length
            && originalLines[start] === resultLines[start]) {
            start++;
        }
        let oldEnd = originalLines.length;
        let newEnd = resultLines.length;
        while (oldEnd > start && newEnd > start
            && originalLines[oldEnd - 1] === resultLines[newEnd - 1]) {
            oldEnd--;
            newEnd--;
        }

        const oldMiddle = originalLines.slice(start, oldEnd);
        const newMiddle = resultLines.slice(start, newEnd);
        const simplified = (oldMiddle.length + 1) * (newMiddle.length + 1) > 1000000;
        const middle = simplified
            ? oldMiddle.map(text => ({ type: "removed", text }))
                .concat(newMiddle.map(text => ({ type: "added", text })))
            : buildDetailedDiffRows(oldMiddle, newMiddle);

        return {
            rows: originalLines.slice(0, start).map(text => ({ type: "unchanged", text }))
                .concat(middle, originalLines.slice(oldEnd).map(text => ({ type: "unchanged", text }))),
            simplified
        };
    }

    function buildDetailedDiffRows(originalLines, resultLines) {
        const table = buildLcsTable(originalLines, resultLines);
        const rows = [];
        let i = 0;
        let j = 0;
        while (i < originalLines.length || j < resultLines.length) {
            if (i < originalLines.length && j < resultLines.length && originalLines[i] === resultLines[j]) {
                rows.push({ type: "unchanged", text: originalLines[i] });
                i++;
                j++;
                continue;
            }

            const change = consumeChangeBlock(originalLines, resultLines, table, i, j);
            for (const removedLine of change.removed) {
                rows.push({ type: "removed", text: removedLine });
            }
            for (const addedLine of change.added) {
                rows.push({ type: "added", text: addedLine });
            }
            i = change.originalIndex;
            j = change.resultIndex;
        }

        return rows;
    }

    function buildLcsTable(originalLines, resultLines) {
        const table = Array.from(
            { length: originalLines.length + 1 },
            () => new Array(resultLines.length + 1).fill(0)
        );

        for (let i = originalLines.length - 1; i >= 0; i--) {
            for (let j = resultLines.length - 1; j >= 0; j--) {
                table[i][j] = originalLines[i] === resultLines[j]
                    ? table[i + 1][j + 1] + 1
                    : Math.max(table[i + 1][j], table[i][j + 1]);
            }
        }

        return table;
    }

    function consumeChangeBlock(originalLines, resultLines, table, originalIndex, resultIndex) {
        const removed = [];
        const added = [];

        while (originalIndex < originalLines.length || resultIndex < resultLines.length) {
            if (originalIndex < originalLines.length
                && resultIndex < resultLines.length
                && originalLines[originalIndex] === resultLines[resultIndex]) {
                break;
            }

            if (originalIndex < originalLines.length
                && (resultIndex >= resultLines.length
                    || table[originalIndex + 1][resultIndex] >= table[originalIndex][resultIndex + 1])) {
                removed.push(originalLines[originalIndex]);
                originalIndex++;
            } else {
                added.push(resultLines[resultIndex]);
                resultIndex++;
            }
        }

        return { removed, added, originalIndex, resultIndex };
    }

    function renderDiff() {
        const comparison = buildDiffRows(state.original, state.result);
        const fragment = document.createDocumentFragment();

        diffBlock.textContent = "";
        if (comparison.simplified) {
            const notice = document.createElement("div");
            notice.className = "diff-summary";
            notice.setAttribute("role", "status");
            notice.textContent = t("preview.simplifiedDiff");
            fragment.appendChild(notice);
        }

        for (const row of comparison.rows) {
            const line = document.createElement("div");
            let prefix = "  ";

            if (row.type === "added") {
                prefix = "+ ";
            } else if (row.type === "removed") {
                prefix = "- ";
            }

            line.className = "diff-line diff-" + row.type;
            line.textContent = prefix + row.text;
            fragment.appendChild(line);
        }

        diffBlock.appendChild(fragment);
    }

    function renderCode() {
        const text = state.activeView === "original" ? state.original : state.result;
        const isCode = state.context === "Code" || state.context === "OneCCode";
        codeBlock.classList.toggle("code-content", isCode);
        codeBlock.classList.toggle("prose-content", !isCode);
        codeElement.textContent = text;
    }

    function renderMeta() {
        contextValue.textContent = state.context || "-";
        modelValue.textContent = state.model || "-";
        contextValue.title = state.context;
        modelValue.title = state.model;
    }

    function renderContextActions() {
        const isAiChat = state.context === "AIChat";
        applyButton.textContent = isAiChat ? t("preview.send") : t("preview.apply");

        for (const tab of tabs) {
            const view = tab.dataset.view;
            if (view === "diff" || view === "original") {
                tab.hidden = isAiChat;
            }
        }
    }

    function setActiveView(view) {
        state.activeView = view;

        for (const tab of tabs) {
            const isActive = tab.dataset.view === view;
            tab.classList.toggle("active", isActive);
            tab.setAttribute("aria-selected", isActive ? "true" : "false");
        }

        const isDiff = view === "diff";
        const isReasoning = view === "reasoning";
        codeBlock.hidden = isDiff || isReasoning;
        diffBlock.hidden = !isDiff;
        reasoningBlock.hidden = !isReasoning;

        if (isDiff) {
            renderDiff();
        } else if (!isReasoning) {
            renderCode();
        }
    }

    function postCommand(command) {
        if (window.chrome?.webview) {
            window.chrome.webview.postMessage(JSON.stringify({ cmd: command,
                integrityAcknowledged: command === "apply" && integrityAcknowledged.checked }));
        }
    }

    function startStreaming(data) {
        previewNotice.hidden = true;
        state.streaming = true;
        state.allowApply = data.allowApply === true;
        state.original = normalize(data.original);
        state.result = "";
        state.context = normalize(data.context);
        state.model = normalize(data.model);
        state.language = normalize(data.language).toLowerCase() === "en" ? "en" : "ru";
        reasoningCode.textContent = "";

        spinner.hidden = false;
        reasoningTab.hidden = false;
        applyButton.hidden = true;
        copyButton.hidden = true;

        applyTranslations();
        renderMeta();
        renderContextActions();
        renderIntegrity(null);
        setActiveView("reasoning");
    }

    function renderBuffered(data) {
        previewNotice.textContent = normalize(data.notice);
        previewNotice.hidden = !previewNotice.textContent;
        state.streaming = false;
        state.allowApply = data.allowApply === true;
        state.original = normalize(data.original);
        state.result = normalize(data.result);
        state.context = normalize(data.context);
        state.model = normalize(data.model);
        state.language = normalize(data.language).toLowerCase() === "en" ? "en" : "ru";
        reasoningCode.textContent = "";

        spinner.hidden = true;
        reasoningTab.hidden = true;
        applyButton.hidden = data.allowApply !== true;
        copyButton.hidden = false;

        applyTranslations();
        renderMeta();
        renderContextActions();
        renderIntegrity(data.integrity);
        setActiveView("result");
    }

    function appendReasoning(text) {
        reasoningCode.textContent += normalize(text);
        reasoningBlock.scrollTop = reasoningBlock.scrollHeight;
    }

    function appendStreamError(text) {
        const message = normalize(text);
        if (message.length === 0) {
            return;
        }

        if (reasoningCode.textContent.length > 0 && !reasoningCode.textContent.endsWith("\n")) {
            reasoningCode.textContent += "\n";
        }

        reasoningCode.textContent += "[" + t("preview.error") + "] " + message + "\n";
        reasoningBlock.scrollTop = reasoningBlock.scrollHeight;
    }

    function handleStreamEvent(data) {
        if (data.type === "reasoning") {
            appendReasoning(data.text);
            return;
        }

        if (data.type === "content") {
            state.result += normalize(data.text);
            if (state.activeView === "result") {
                renderCode();
            }
            return;
        }

        if (data.type === "done") {
            spinner.hidden = true;
            copyButton.hidden = false;
            applyButton.hidden = !state.allowApply;
            renderIntegrity(data.integrity);
            setActiveView("result");
            return;
        }

        if (data.type === "error") {
            spinner.hidden = true;
            appendStreamError(data.text);
            copyButton.hidden = false;
        }
    }

    // C# sends objects through CoreWebView2.PostWebMessageAsJson; event.data is already parsed.
    window.chrome.webview.addEventListener("message", function (event) { // NOSONAR: host-only WebView2 channel; native side validates e.Source.
        const data = event.data || {};

        if (data.streaming === true) {
            startStreaming(data);
            return;
        }

        if (data.type) {
            handleStreamEvent(data);
            return;
        }

        renderBuffered(data);
    });

    for (const tab of tabs) {
        tab.addEventListener("click", function () {
            setActiveView(this.dataset.view);
        });
    }

    copyButton.addEventListener("click", function () {
        postCommand("copy");
    });

    applyButton.addEventListener("click", function () {
        postCommand("apply");
    });

    integrityAcknowledged.addEventListener("change", function () {
        applyButton.disabled = !integrityReview.hidden && !integrityAcknowledged.checked;
    });

    cancelButton.addEventListener("click", function () {
        postCommand("cancel");
    });

    document.addEventListener("keydown", function (event) {
        if (event.key === "Escape") {
            postCommand("cancel");
        }
    });
