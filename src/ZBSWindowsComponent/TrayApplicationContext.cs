using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    internal static class IconResources
    {
        private static Icon _appIcon;
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);

        internal static Icon CreateBusy()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var background = new SolidBrush(Color.FromArgb(255, 194, 54)))
            using (var pen = new Pen(Color.FromArgb(20, 25, 30), 2))
            {
                graphics.DrawIcon(App ?? SystemIcons.Application, new Rectangle(0, 0, 32, 32));
                graphics.FillEllipse(background, 13, 13, 19, 19);
                graphics.DrawLine(pen, 18, 17, 27, 17);
                graphics.DrawLine(pen, 18, 28, 27, 28);
                graphics.DrawLine(pen, 18, 17, 27, 28);
                graphics.DrawLine(pen, 27, 17, 18, 28);
                var handle = bitmap.GetHicon();
                try { using (var temporary = Icon.FromHandle(handle)) return (Icon)temporary.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }

        public static Icon App
        {
            get
            {
                if (_appIcon != null) return _appIcon;

                try
                {
                    using (var s = typeof(IconResources).Assembly.GetManifestResourceStream("ZBSWindowsComponent.app.ico"))
                    {
                        if (s != null) _appIcon = new Icon(s);
                    }
                }
                catch
                {
                    // Fail open: callers fall back to SystemIcons.Application.
                }

                return _appIcon;
            }
        }
    }

    /// <summary>
    /// Корневой контекст WinForms message loop с иконкой в трее.
    /// </summary>
    public class TrayApplicationContext : ApplicationContext
    {
        private const string ApplicationName = WindowsAppIdentity.DisplayName;
        private const string GeneralSection = "general";
        private const string SettingsFileName = "settings.json";
        private readonly Action _onExit;
        private readonly SettingsWebForm _settingsForm;  // скрытая pre-warmed форма, см. HotkeyManager
        private NotifyIcon _notifyIcon;
        private ContextMenuStrip _contextMenu;
        private readonly ToolStripMenuItem _autostartItem;
        private readonly ToolStripMenuItem _replaceItem;
        private readonly ToolStripMenuItem _streamItem;
        private readonly ToolStripMenuItem _confirmFullItem;
        private readonly ToolStripMenuItem _settingsItem;
        private readonly ToolStripMenuItem _exitItem;
        private readonly ToolStripMenuItem _cancelItem;
        private readonly ToolStripMenuItem _operationItem;
        private Timer _codexOnboardingTimer;
        private bool _codexOfferActive;
        private DesktopNotification _codexNotification;
        private readonly OperationStatusForm _statusForm;
        private readonly Icon _busyIcon;
        private readonly Timer _feedbackTimer;
        private readonly Func<OperationFeedbackOptions> _readFeedback;
        private OperationFeedbackOptions _feedbackOptions;
        private WorkOperation _feedbackOperation;
        private int _feedbackVersion;
        private bool _feedbackActive;
        private bool _popupSuppressed;
        private string _feedbackMessage;
        internal bool PopupVisible => _statusForm.Visible;
        internal bool BusyIconVisible => _notifyIcon != null && ReferenceEquals(_notifyIcon.Icon, _busyIcon);
        internal string FeedbackText => _notifyIcon?.Text;

        public TrayApplicationContext(Action onExit, SettingsWebForm settingsForm) : this(onExit, settingsForm, true) { }

        internal TrayApplicationContext(Action onExit, SettingsWebForm settingsForm, bool startOnboarding,
            Func<OperationFeedbackOptions> readFeedback = null)
        {
            _onExit = onExit;
            _settingsForm = settingsForm;
            _statusForm = new OperationStatusForm();
            _busyIcon = IconResources.CreateBusy();
            _readFeedback = readFeedback ?? ReadFeedbackOptions;
            _feedbackOptions = new OperationFeedbackOptions("both");
            _feedbackTimer = new Timer { Interval = 500 };
            _feedbackTimer.Tick += (_, __) => RefreshFeedback();

            _contextMenu = new ContextMenuStrip
            {
                BackColor = Color.FromArgb(26, 31, 38),
                ForeColor = Color.FromArgb(229, 240, 255),
                Font = SystemFonts.MenuFont,
                Renderer = new DarkTrayMenuRenderer()
            };
            // Opening — последний шанс обновить Checked под актуальное состояние
            // settings.json/HKCU: меняли в окне Настроек / руками / другой
            // инстанс ZBS до single-instance — меню не должно врать.
            _contextMenu.Opening += ContextMenu_Opening;

            _autostartItem = new ToolStripMenuItem(string.Empty, null, AutostartItem_Click)
            {
                CheckOnClick = false   // toggle сами после успешной записи в реестр
            };
            _replaceItem = new ToolStripMenuItem(string.Empty, null, ReplaceItem_Click)
            {
                CheckOnClick = false
            };
            _streamItem = new ToolStripMenuItem(string.Empty, null, StreamItem_Click)
            {
                CheckOnClick = false
            };
            _confirmFullItem = new ToolStripMenuItem(string.Empty, null, ConfirmFullItem_Click)
            {
                CheckOnClick = false
            };

            _settingsItem = new ToolStripMenuItem(string.Empty, null, SettingsMenuItem_Click);
            _cancelItem = new ToolStripMenuItem(string.Empty, null, CancelOperation_Click);
            _operationItem = new ToolStripMenuItem(string.Empty) { Enabled = false };
            _exitItem = new ToolStripMenuItem(string.Empty, null, ExitMenuItem_Click);
            ApplyLanguage();

            _contextMenu.Items.Add(_autostartItem);
            _contextMenu.Items.Add(_replaceItem);
            _contextMenu.Items.Add(_streamItem);
            _contextMenu.Items.Add(_confirmFullItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(_settingsItem);
            _contextMenu.Items.Add(new ToolStripSeparator());
            _contextMenu.Items.Add(_operationItem);
            _contextMenu.Items.Add(_cancelItem);
            _contextMenu.Items.Add(_exitItem);

            _notifyIcon = new NotifyIcon
            {
                Icon = IconResources.App ?? SystemIcons.Application,
                Text = ApplicationName,
                ContextMenuStrip = _contextMenu,
                Visible = true
            };
            if (startOnboarding) BeginCodexOnboardingCheck();
        }

        public void ShowBusy(string message)
        {
            if (_notifyIcon == null) return;

            _feedbackActive = true;
            _feedbackOperation = Operations.Active;
            _feedbackMessage = message;
            _popupSuppressed = false;
            ApplyFeedbackOptions();
            UpdateFeedbackText();
            _feedbackTimer.Start();
        }

        public void ResetReady(string message)
        {
            if (_notifyIcon == null) return;
            _feedbackActive = false;
            _feedbackTimer.Stop();
            _notifyIcon.Icon = IconResources.App ?? SystemIcons.Application;
            _notifyIcon.Text = ApplicationName;
            if (_feedbackOptions.ShowPopup && !_popupSuppressed) _statusForm.ShowReady(message);
            else _statusForm.HideStatus();
        }

        public void HideFeedback() { _popupSuppressed = true; _statusForm.HideStatus(); }

        internal void RefreshFeedback()
        {
            if (!_feedbackActive || _notifyIcon == null) return;
            if (_feedbackOperation != null && !ReferenceEquals(_feedbackOperation, Operations.Active))
            {
                ResetReady(UiText.Current("Обработка завершена", "Processing finished"));
                return;
            }
            if (_feedbackVersion != SettingsState.Version) ApplyFeedbackOptions();
            UpdateFeedbackText();
        }

        private void UpdateFeedbackText()
        {
            var elapsed = OperationFeedbackOptions.FormatElapsed(_feedbackOperation?.Elapsed ?? TimeSpan.Zero);
            _notifyIcon.Text = Operations.IsCancelling
                ? UiText.Current("Make it ZBS - отмена ", "Make it ZBS - cancelling ") + elapsed
                : UiText.Current("Make it ZBS - обработка ", "Make it ZBS - processing ") + elapsed;
        }

        private void ApplyFeedbackOptions()
        {
            _feedbackVersion = SettingsState.Version;
            _feedbackOptions = _readFeedback();
            _notifyIcon.Icon = _feedbackOptions.ShowIcon ? _busyIcon : IconResources.App ?? SystemIcons.Application;
            if (_feedbackOptions.ShowPopup && !_popupSuppressed) _statusForm.ShowBusy(_feedbackMessage);
            else _statusForm.HideStatus();
        }

        private static OperationFeedbackOptions ReadFeedbackOptions()
        {
            try
            {
                var directory = ProjectPaths.ConfigDir;
                var path = string.IsNullOrEmpty(directory) ? null : Path.Combine(directory, SettingsFileName);
                return OperationFeedbackOptions.FromSettings(path != null && File.Exists(path) ? SettingsStore.LoadJObject(path) : null);
            }
            catch (Exception error)
            {
                System.Diagnostics.Trace.TraceWarning("Operation feedback settings: " + error.GetType().Name);
                return new OperationFeedbackOptions("both");
            }
        }

        private void SettingsMenuItem_Click(object sender, EventArgs e)
        {
            // Phase 11: pre-warmed форма в HotkeyManager (скрыта, WebView2
            // прогрет + settings.html отрендерен) → Show() мгновенный. Если
            // pre-warm не передан или сломан — fallback на видимую on-demand
            // форму (ShowDialog). Phase 15: добавлена проверка IsBroken —
            // если pre-warm упал (нет WebView2 Runtime), ReloadAndShow показал
            // бы вечно пустое окно; fallback создаёт видимую форму, которая при
            // повторном сбое покажет нормальный видимый MessageBox.
            if (_settingsForm != null && !_settingsForm.IsDisposed && !_settingsForm.IsBroken)
            {
                _settingsForm.ReloadAndShow();
                return;
            }

            using (var form = new SettingsWebForm())
            {
                form.ShowDialog();
            }
        }

        private void BeginCodexOnboardingCheck()
        {
            _codexOnboardingTimer = new Timer { Interval = 1500 };
            _codexOnboardingTimer.Tick += async (sender, args) =>
            {
                _codexOnboardingTimer.Stop();
                await CheckCodexOnboardingAsync();
            };
            _codexOnboardingTimer.Start();
        }

        private async Task CheckCodexOnboardingAsync()
        {
            try
            {
                var configDir = ProjectPaths.ConfigDir;
                if (string.IsNullOrEmpty(configDir))
                {
                    return;
                }

                var settingsPath = Path.Combine(configDir, SettingsFileName);
                if (!File.Exists(settingsPath))
                {
                    return;
                }

                var settings = SettingsStore.LoadJObject(settingsPath);
                if (!ShouldShowCodexOnboarding(
                    settings, _settingsForm != null && _settingsForm.Visible))
                {
                    return;
                }

                var status = await Task.Run(() => CodexCliClient.CheckStatus(false));
                settings = SettingsStore.LoadJObject(settingsPath);
                if (!ShouldShowCodexOnboarding(
                    settings, _settingsForm != null && _settingsForm.Visible))
                {
                    return;
                }
                var message = BuildCodexOnboardingMessage(status, UiText.CurrentLanguage());
                _codexOfferActive = true;
                _codexNotification = new DesktopNotification(System.Threading.SynchronizationContext.Current,
                    () => CodexOnboardingNotification_Clicked(this, EventArgs.Empty), ClearCodexOnboardingNotification);
                _codexNotification.Show(message);
                MarkCodexOnboardingShown(settings);
                SettingsStore.SaveJsonAtomic(settingsPath, settings);
            }
            catch (Exception ex)
            {
                ClearCodexOnboardingNotification();
                Console.WriteLine("[zbs.codex] onboarding check failed: " + ex.Message);
            }
        }

        private void CodexOnboardingNotification_Clicked(object sender, EventArgs e)
        {
            if (!_codexOfferActive)
            {
                return;
            }

            ClearCodexOnboardingNotification();
            SettingsMenuItem_Click(sender, e);
        }

        private void ClearCodexOnboardingNotification()
        {
            _codexOfferActive = false;
            _codexNotification?.Dispose();
            _codexNotification = null;
        }

        internal static bool ShouldOfferCodexOnboarding(JObject settings)
        {
            return !ReadBool(settings?[GeneralSection] as JObject, "codexOfferShown", false);
        }

        internal static bool ShouldShowCodexOnboarding(JObject settings, bool settingsVisible)
        {
            return !settingsVisible && ShouldOfferCodexOnboarding(settings);
        }

        internal static void MarkCodexOnboardingShown(JObject settings)
        {
            if (settings == null)
            {
                return;
            }

            var general = settings[GeneralSection] as JObject;
            if (general == null)
            {
                general = new JObject();
                settings[GeneralSection] = general;
            }

            general["codexOfferShown"] = true;
        }

        internal static string BuildCodexOnboardingMessage(CodexCliStatus status, string language)
        {
            status = status ?? new CodexCliStatus();
            if (status.UsesChatGptSubscription)
            {
                return UiText.Get(language,
                    "Codex CLI найден и подключён к ChatGPT. Нажмите, чтобы выбрать его для совместимых контекстов.",
                    "Codex CLI is connected to ChatGPT. Click to use it for compatible contexts.");
            }

            if (status.Installed)
            {
                if (status.Authenticated)
                {
                    return UiText.Get(language,
                        "Codex CLI использует API-авторизацию. Для лимитов подписки выполните вход через ChatGPT.",
                        "Codex CLI is using API authentication. Sign in with ChatGPT to use subscription limits.");
                }

                return UiText.Get(language,
                    "Codex CLI найден, но вход не выполнен. Нажмите, чтобы открыть настройку провайдера.",
                    "Codex CLI is installed but not signed in. Click to open provider setup.");
            }

            return UiText.Get(language,
                "Codex CLI не найден. Нажмите, чтобы открыть настройку провайдера и инструкции подключения.",
                "Codex CLI was not found. Click to open provider settings and setup instructions.");
        }

        private void ContextMenu_Opening(object sender, CancelEventArgs e)
        {
            ApplyLanguage();
            // Autostart — HKCU Run, independent от settings.json
            try
            {
                _autostartItem.Checked = SettingsStore.IsAutostartEnabled();
            }
            catch
            {
                _autostartItem.Checked = false;
            }

            // settings.json — три булевых тоггла. Defaults согласованы с
            // oscript: replace.enabled opt-in=false; preview.stream=true
            // (Phase 5 «стрим вкл по умолчанию»); general.confirmFullModule
            // =true (Phase 3 — нет ключа → подтверждаем).
            var defaultReplace = false;
            var defaultStream = true;
            var defaultConfirmFull = true;

            try
            {
                var configDir = ProjectPaths.ConfigDir;
                if (!string.IsNullOrEmpty(configDir))
                {
                    var settingsPath = Path.Combine(configDir, SettingsFileName);
                    if (File.Exists(settingsPath))
                    {
                        var settings = SettingsStore.LoadJObject(settingsPath);
                        _replaceItem.Checked = ReadBool(settings["replace"] as JObject, "enabled", defaultReplace);
                        _streamItem.Checked = ReadBool(settings["preview"] as JObject, "stream", defaultStream);
                        _confirmFullItem.Checked = ReadBool(settings[GeneralSection] as JObject, "confirmFullModule", defaultConfirmFull);
                        return;
                    }
                }
            }
            catch
            {
                // fall through к defaults
            }

            _replaceItem.Checked = defaultReplace;
            _streamItem.Checked = defaultStream;
            _confirmFullItem.Checked = defaultConfirmFull;
        }

        private void AutostartItem_Click(object sender, EventArgs e)
        {
            try
            {
                var launcherPath = ProjectPaths.LauncherPath;
                if (string.IsNullOrEmpty(launcherPath))
                {
                    MessageBox.Show(UiText.Current(
                        "Лаунчер не найден - автозапуск нельзя включить.",
                        "The launcher was not found, so startup cannot be enabled."),
                        ApplicationName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var current = SettingsStore.IsAutostartEnabled();
                SettingsStore.SetAutostart(!current, launcherPath);
                _autostartItem.Checked = !current;
                // SettingsState.MarkDirty НЕ нужен: автозапуск не читается
                // oscript'ом в рантайме, только при следующем boot.
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText.Current(
                    "Не удалось переключить автозапуск: ",
                    "Could not change the startup setting: ") + ex.Message,
                    ApplicationName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ReplaceItem_Click(object sender, EventArgs e)
        {
            ToggleSettingsBool("replace", "enabled", false, _replaceItem);
        }

        private void StreamItem_Click(object sender, EventArgs e)
        {
            ToggleSettingsBool("preview", "stream", true, _streamItem);
        }

        private void ConfirmFullItem_Click(object sender, EventArgs e)
        {
            ToggleSettingsBool(GeneralSection, "confirmFullModule", true, _confirmFullItem);
        }

        private static void ToggleSettingsBool(string section, string key, bool defaultValue, ToolStripMenuItem menuItem)
        {
            try
            {
                var configDir = ProjectPaths.ConfigDir;
                if (string.IsNullOrEmpty(configDir))
                {
                    MessageBox.Show(UiText.Current(
                        "Каталог config не найден.", "The config folder was not found."),
                        ApplicationName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var settingsPath = Path.Combine(configDir, SettingsFileName);
                var settings = File.Exists(settingsPath)
                    ? SettingsStore.LoadJObject(settingsPath)
                    : new JObject();

                var sectionObj = settings[section] as JObject;
                if (sectionObj == null)
                {
                    sectionObj = new JObject();
                    settings[section] = sectionObj;
                }

                var current = ReadBool(sectionObj, key, defaultValue);
                sectionObj[key] = !current;

                SettingsStore.SaveJsonAtomic(settingsPath, settings);
                menuItem.Checked = !current;

                // live-reload в oscript — тот же сигнал, что использует
                // SettingsWebForm. Поллер увидит и подцепит изменение.
                SettingsState.MarkDirty();
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText.Current(
                    "Не удалось переключить настройку: ", "Could not change the setting: ") + ex.Message,
                    ApplicationName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static bool ReadBool(JObject obj, string key, bool defaultValue)
        {
            if (obj == null)
            {
                return defaultValue;
            }

            var token = obj[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return defaultValue;
            }

            try
            {
                return token.Value<bool>();
            }
            catch (FormatException)
            {
                return defaultValue;
            }
            catch (InvalidCastException)
            {
                return defaultValue;
            }
        }

        private void ApplyLanguage()
        {
            var language = UiText.CurrentLanguage();
            _cancelItem.Text = UiText.Get(language, "Отменить обработку", "Cancel processing");
            _cancelItem.Enabled = Operations.IsBusy && !Operations.IsCancelling;
            _operationItem.Text = Operations.IsBusy
                ? UiText.Get(language, "Задача выполняется", "Task running")
                : UiText.Get(language, "Готово к новой задаче", "Ready for a new task");
            if (Operations.IsCancelling)
                _operationItem.Text = UiText.Get(language, "Отмена задачи...", "Cancelling task...");
            _autostartItem.Text = UiText.Get(language,
                "Автозапуск с Windows", "Start with Windows");
            _replaceItem.Text = UiText.Get(language,
                "Заменять в исходном окне", "Replace in the source window");
            _streamItem.Text = UiText.Get(language,
                "Показывать ход обработки", "Show processing stream");
            _confirmFullItem.Text = UiText.Get(language,
                "Подтверждать весь модуль (1С)", "Confirm full 1C module");
            _settingsItem.Text = UiText.Get(language, "Настройки", "Settings");
            _exitItem.Text = UiText.Get(language, "Выход", "Exit");
        }

        private async void CancelOperation_Click(object sender, EventArgs e)
        {
            _cancelItem.Enabled = false;
            var operation = Operations.Active;
            await Task.Run(() => Operations.Cancel(operation));
            if (_notifyIcon != null)
                _notifyIcon.Text = UiText.Current("Make it ZBS - отмена...", "Make it ZBS - cancelling...");
        }

        private void ExitMenuItem_Click(object sender, EventArgs e)
        {
            // Phase 15 (аудит): раньше диспозилась только NotifyIcon, затем
            // Environment.Exit(0) убивал процесс — WebView2-контроллер и
            // прогретая форма Настроек не закрывались чисто (msedgewebview2
            // осиротевал, профиль не флашился). Полный Dispose закрывает и
            // форму (Shutdown), и меню. Остаточный риск: если Выход нажат
            // во время вставки результата, STA-поток вставки гибнет до
            // finally-restore буфера — страхует backup в logs/backups/.
            try
            {
                Dispose();
            }
            catch
            {
                DisposeNotifyIcon();  // как минимум снять иконку
            }

            try
            {
                if (_onExit != null)
                {
                    _onExit();
                }
            }
            finally
            {
                Environment.Exit(0);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                ClearCodexOnboardingNotification();
                _feedbackTimer.Stop();
                _feedbackTimer.Dispose();
                if (_codexOnboardingTimer != null)
                {
                    _codexOnboardingTimer.Stop();
                    _codexOnboardingTimer.Dispose();
                    _codexOnboardingTimer = null;
                }

                DisposeNotifyIcon();
                _busyIcon.Dispose();

                _statusForm.Dispose();

                if (_contextMenu != null)
                {
                    _contextMenu.Dispose();
                    _contextMenu = null;
                }

                // Pre-warmed форму shutdown'им именно тут: владение
                // делится между HotkeyManager (создал/прогрел) и tray
                // (показывает), но «реальный конец жизни» совпадает с
                // концом message loop'а трея.
                if (_settingsForm != null && !_settingsForm.IsDisposed)
                {
                    try
                    {
                        _settingsForm.Shutdown();
                    }
                    catch
                    {
                        // fail-open на shutdown — не блокируем выход
                    }
                }
            }

            base.Dispose(disposing);
        }

        private void DisposeNotifyIcon()
        {
            if (_notifyIcon != null)
            {
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _notifyIcon = null;
            }
        }

        private sealed class DarkTrayMenuRenderer : ToolStripProfessionalRenderer
        {
            public DarkTrayMenuRenderer()
                : base(new DarkTrayMenuColorTable())
            {
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                e.TextColor = e.Item.Selected
                    ? Color.FromArgb(13, 17, 23)
                    : Color.FromArgb(229, 240, 255);
                base.OnRenderItemText(e);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                using (var pen = new Pen(Color.FromArgb(48, 54, 61)))
                {
                    var bounds = e.Item.Bounds;
                    var y = bounds.Height / 2;
                    e.Graphics.DrawLine(pen, 8, y, bounds.Width - 8, y);
                }
            }
        }

        private sealed class DarkTrayMenuColorTable : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground
            {
                get { return Color.FromArgb(26, 31, 38); }
            }

            public override Color ImageMarginGradientBegin
            {
                get { return Color.FromArgb(26, 31, 38); }
            }

            public override Color ImageMarginGradientMiddle
            {
                get { return Color.FromArgb(26, 31, 38); }
            }

            public override Color ImageMarginGradientEnd
            {
                get { return Color.FromArgb(26, 31, 38); }
            }

            public override Color MenuBorder
            {
                get { return Color.FromArgb(48, 54, 61); }
            }

            public override Color MenuItemBorder
            {
                get { return Color.FromArgb(34, 211, 238); }
            }

            public override Color MenuItemSelected
            {
                get { return Color.FromArgb(34, 211, 238); }
            }

            public override Color MenuItemSelectedGradientBegin
            {
                get { return Color.FromArgb(34, 211, 238); }
            }

            public override Color MenuItemSelectedGradientEnd
            {
                get { return Color.FromArgb(34, 211, 238); }
            }

            public override Color MenuItemPressedGradientBegin
            {
                get { return Color.FromArgb(34, 211, 238); }
            }

            public override Color MenuItemPressedGradientMiddle
            {
                get { return Color.FromArgb(34, 211, 238); }
            }

            public override Color MenuItemPressedGradientEnd
            {
                get { return Color.FromArgb(34, 211, 238); }
            }

            public override Color SeparatorDark
            {
                get { return Color.FromArgb(48, 54, 61); }
            }

            public override Color SeparatorLight
            {
                get { return Color.FromArgb(48, 54, 61); }
            }
        }
    }
}
