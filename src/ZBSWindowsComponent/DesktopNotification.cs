using System;
using System.Diagnostics;
using System.Threading;
using System.Xml;
using Windows.UI.Notifications;

namespace ZBSWindowsComponent
{
    internal sealed class DesktopNotification : IDisposable
    {
        private readonly SynchronizationContext _ui;
        private readonly Action _activated;
        private readonly Action _closed;
        private ToastNotifier _notifier;
        private ToastNotification _toast;
        private int _disposed;
        private const string NotificationTag = "codex-onboarding";
        private const string NotificationGroup = "setup";

        internal DesktopNotification(SynchronizationContext ui, Action activated, Action closed)
        {
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _activated = activated;
            _closed = closed;
        }

        internal void Show(string message)
        {
            var xml = new Windows.Data.Xml.Dom.XmlDocument();
            xml.LoadXml(BuildXml(message));
            _toast = new ToastNotification(xml)
            {
                Tag = NotificationTag, Group = NotificationGroup,
                ExpirationTime = DateTimeOffset.UtcNow.AddMinutes(1)
            };
            _toast.Activated += OnActivated;
            _toast.Dismissed += OnDismissed;
            _toast.Failed += OnFailed;
            _notifier = ToastNotificationManager.CreateToastNotifier(WindowsAppIdentity.AppId);
            _notifier.Show(_toast);
            Console.WriteLine("[zbs.notification] Shown: " + WindowsAppIdentity.AppId);
        }

        internal static string BuildXml(string message)
        {
            var xml = new XmlDocument();
            var toast = xml.CreateElement("toast");
            xml.AppendChild(toast);
            var visual = xml.CreateElement("visual");
            toast.AppendChild(visual);
            var binding = xml.CreateElement("binding");
            binding.SetAttribute("template", "ToastGeneric");
            visual.AppendChild(binding);
            foreach (var text in new[] { WindowsAppIdentity.DisplayName, message })
            {
                var element = xml.CreateElement("text");
                element.InnerText = text;
                binding.AppendChild(element);
            }
            return xml.OuterXml;
        }

        private void OnActivated(ToastNotification sender, object args)
        {
            Console.WriteLine("[zbs.notification] Activated");
            Post(_activated);
        }
        private void OnDismissed(ToastNotification sender, ToastDismissedEventArgs args)
        {
            Console.WriteLine("[zbs.notification] Dismissed: " + args.Reason);
            Post(_closed);
        }
        private void OnFailed(ToastNotification sender, ToastFailedEventArgs args)
        {
            Console.WriteLine("[zbs.notification] Delivery failed: 0x" + args.ErrorCode.HResult.ToString("X8"));
            Post(_closed);
        }
        private void Post(Action action) => _ui.Post(_ =>
        {
            if (Volatile.Read(ref _disposed) == 0) action?.Invoke();
        }, null);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0 || _toast == null) return;
            _toast.Activated -= OnActivated;
            _toast.Dismissed -= OnDismissed;
            _toast.Failed -= OnFailed;
            try
            {
                _notifier?.Hide(_toast);
                ToastNotificationManager.History.Remove(NotificationTag, NotificationGroup, WindowsAppIdentity.AppId);
            }
            catch (Exception error) { Trace.TraceWarning("Notification cleanup: " + error.GetType().Name); }
            _toast = null;
            _notifier = null;
        }
    }
}
