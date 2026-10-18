using NUnit.Framework;
using ZBSWindowsComponent;

[SetUpFixture]
public class DispatcherLifetime
{
    [OneTimeTearDown]
    public void StopOnlyTheDispatchersCreatedInThisTestProcess()
    {
        ClipboardDispatcher.ShutdownShared();
        UiaDispatcher.ShutdownShared();
    }
}
