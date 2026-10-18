// Explicitly consumes the user's CLI allowance; not run by the package loader or smoke tests.
AttachAddIn(CommandLineArguments[0]);
// Polling interval in milliseconds.
PollMilliseconds = 50;
Clients = New AutomationClients;
Job = Clients.StartCodex("Correct spelling only.", "Synthetic input text.");
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
