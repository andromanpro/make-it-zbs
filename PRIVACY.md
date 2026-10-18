# Data and privacy

Make it ZBS runs on your Windows computer. It does not require an account with
the author, and the application has no built-in analytics upload service.

## What leaves your computer

A hotkey can collect selected text, editor context or an image, depending on the
active application and your settings. The collected input and configured prompt
are sent to your selected provider: OpenRouter, or the locally installed Codex CLI.
Local Codex execution is not local model inference: Codex can send data to its
configured provider. Provider terms, retention, availability and usage limits apply.

AI-chat text has local secret-pattern redaction. This is not complete data loss
prevention. Review sensitive material yourself; images can contain information
that text redaction cannot remove. Screenshot fallback for AI chats is opt-in.
Do not process a document you are not permitted to send to the selected provider.

## What stays on disk

- Configuration: the portable application's `config` directory. The OpenRouter
  key is stored in `config/.env`; it is not encrypted by this application.
  Restrict access to the application folder and never share your configured copy.
- Technical diagnostics: disabled by default. The technical-only mode excludes
  request/response text and raw exception messages. Explicitly enabled content
  logging can retain documents, prompts and answers as plaintext JSON.
- Developer console output can contain window titles and diagnostics. Redirecting
  it to a file is a separate explicit action and can retain that information.
- Replacement safety copies: `logs/backups/*.zbsbackup`, encrypted with Windows
  DPAPI for the current user. They cannot normally be recovered under another
  Windows account or on another computer. There is no promise of secure erasure.
- Emergency result copies: the same encrypted format if both result windows fail.
- Captured images: `%LOCALAPPDATA%/MakeItZBS/Temp`. Active operations own their
  temporary files; startup/operation cleanup limits orphaned PNGs to 20 per type.
  These PNGs are not encrypted. Cleanup by count is not immediate deletion of
  every old image and does not securely erase storage media.
- Generated images: `logs/images` and any destination you choose to save to.
- WebView2: a per-user application data folder containing browser-engine state.

Codex manages its own login, history, logs and generated files. Closing or
uninstalling Make it ZBS does not erase data held by Codex or remote providers.

## Control and deletion

Choose a provider before processing, keep logging off unless required, and use
synthetic material for bug reports. Close the application before manually removing
its generated files. Keep encrypted safety copies until you are satisfied with
applied changes. Back up settings privately before upgrading; distribute only
the unconfigured release ZIP, not your installed folder.

Cancellation stops owned local work and rejects a late result. It does not
guarantee cancellation of remote generation or charges, and does not undo an
already completed edit. Contacts in About open the browser only when clicked.
