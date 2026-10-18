// Run with the module path and absolute output PNG path; the current foreground window is the source.
ModulePath = CommandLineArguments[0];
OutputPath = CommandLineArguments[1];
AttachAddIn(ModulePath);
// Polling interval in milliseconds.
PollMilliseconds = 50;
Clients = New AutomationClients;
Job = Clients.CaptureWindow(OutputPath, "auto");
Try
    While Not Job.IsCompleted Do
        Sleep(PollMilliseconds);
    EndDo;
    Message(Job.ResultJson());
Except
    Job.Close();
    Raise;
EndTry;
Job.Close();
