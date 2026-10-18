AttachAddIn(CommandLineArguments[0]);
Clients = New AutomationClients;
If Clients.ResolveChatProfile("codex.exe", "") <> "codex" Then
    Raise "Codex profile mismatch";
EndIf;
If Clients.ResolveChatProfile("claude.exe", "") <> "claude" Then
    Raise "Claude profile mismatch";
EndIf;
If Clients.ResolveChatProfile("opencode.exe", "") <> "opencode" Then
    Raise "OpenCode profile mismatch";
EndIf;
If Clients.ResolveChatProfile("notepad.exe", "Codex") <> "" Then
    Raise "Unrelated application matched";
EndIf;
Message("AUTOMATION-SMOKE-PASS assertions=4");
