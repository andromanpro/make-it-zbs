// This example registers a global combination until the first accepted press.
AttachAddIn(CommandLineArguments[0]);
// Polling interval in milliseconds.
PollMilliseconds = 50;
Hotkeys = New AutomationHotkeys;
Try
    Hotkeys.Listen("Ctrl+Shift+F23");
    Pressed = "";
    While Pressed = "" Do
        Sleep(PollMilliseconds);
        Pressed = Hotkeys.TakePressed();
    EndDo;
    Message(Pressed);
Except
    Hotkeys.Close();
    Raise;
EndTry;
Hotkeys.Close();
