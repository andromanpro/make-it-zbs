# Security policy

## Supported builds

This project is a beta Windows utility. Security fixes target the newest beta
source and portable package. Older beta.3 binaries contain an older OneScript
ZIP dependency and must not be treated as the current hardened build.

## Report privately

Send suspected vulnerabilities privately to the author at
[Telegram @andromanpro](https://t.me/andromanpro). When available in the public
GitHub repository, private vulnerability reporting is also suitable.
Do not put tokens, document text, chat history or customer screenshots in a
public issue. Start with the affected version, Windows version, a description
and a synthetic reproducer. There is no guaranteed response-time SLA or bounty.

## Boundaries

The application operates with your Windows permissions. It cannot defend against
malicious software running as the same user or administrator. Focus and document
checks reduce accidental application to another source; they are not a sandbox.
The VS Code adapter validates a per-session token, pipe server process, document
identity/version and selection. It does not make an untrusted local user trusted.

Input read from a document, browser or AI chat is untrusted content, not permission
to run commands. Always review AI output. Never automate repeated send/continue
without an appropriate user decision. See [PRIVACY.md](PRIVACY.md) for data handling.

Unsigned beta packages can trigger SmartScreen. Check the downloaded ZIP against
its SHA-256 file from the same trusted release page. Checksums do not prove the
publisher's identity and cannot replace Authenticode.
