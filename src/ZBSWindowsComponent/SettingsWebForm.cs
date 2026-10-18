using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    public class SettingsWebForm : Form
    {
        private const string AiChatKey = "aiChat";
        private const string AuthorTelegramHost = "t.me";
        private const string AuthorTelegramPath = "andromanpro";
        private const string AuthorWebsiteHost = "androman.pro";
        private const string BackgroundKey = "background";
        private const string CancelledOutcome = "cancelled";
        private const string CodexModelKey = "codexModel";
        private const string CodexReasoningEffortKey = "codexReasoningEffort";
        private const string ConfigErrorMessageKey = "configErrorMessage";
        private const string ContextsKey = "contexts";
        private const string EnvTokenKey = "OPENROUTER_API_KEY";
        private const string LoggingKey = "logging";
        private const string EnabledKey = "enabled";
        private const string GeneralKey = "general";
        private const string HotkeyKey = "hotkey";
        private const string LabelKey = "label";
        private const string LanguageKey = "language";
        private const string MaxTokensKey = "maxTokens";
        private const string ModelKey = "model";
        private const string ProfilesKey = "profiles";
        private const string ProviderKey = "provider";
        private const string SystemKey = "system";
        private const string TemperatureKey = "temperature";
        private const string UiOrigin = "https://zbs.ui";
        internal static readonly string AuthorTelegramUrl = BuildHttpsUrl(
            AuthorTelegramHost, AuthorTelegramPath);
        internal static readonly string AuthorWebsiteUrl = BuildHttpsUrl(
            AuthorWebsiteHost, string.Empty);

        private WebView2 _webView;
        private bool _closed;
        private bool _prewarmStarted;
        private bool _initialized;
        private bool _shuttingDown;
        private bool _initFailed;
        private CodexCliStatus _codexStatus;
        private VsCodeAdapterStatus _adapterStatus;
        private bool _adapterActionActive;
        private int _initGuard;  // Interlocked-захват, защита от двойного BeginInitialization
        private readonly bool _probeIntegrations;
        private readonly string _webViewUserDataFolder;
        private readonly Action<bool, string> _writeAutostart;
        private readonly Action<string> _saveWarning;

        public string Outcome { get; private set; }

        public bool IsPrewarmed { get { return _initialized; } }

        // Phase 15: инициализация pre-warm формы провалилась (напр. нет
        // WebView2 Runtime). Трей должен НЕ показывать эту форму, а сделать
        // fallback на видимую on-demand-форму.
        public bool IsBroken { get { return _initFailed; } }

        public SettingsWebForm() : this(true, null)
        {
        }

        internal SettingsWebForm(bool probeIntegrations, string webViewUserDataFolder,
            Action<bool, string> writeAutostart = null, Action<string> saveWarning = null)
        {
            _probeIntegrations = probeIntegrations;
            _webViewUserDataFolder = webViewUserDataFolder;
            _writeAutostart = writeAutostart ?? SettingsStore.SetAutostart;
            _saveWarning = saveWarning ?? (message => MessageBox.Show(this, message,
                Text, MessageBoxButtons.OK, MessageBoxIcon.Warning));
            Outcome = CancelledOutcome;
            InitializeComponent();
        }

        /// <summary>
        /// Phase 11: pre-warm форму скрыто — создать handle, добавить
        /// WebView2, EnsureCoreWebView2 + Navigate(settings.html). НЕ
        /// показывает форму. Открытие из трея потом = Show() готовой.
        /// </summary>
        public void BeginPrewarm()
        {
            BeginPrewarm(true);
        }

        internal void BeginPrewarm(bool initializeWebView)
        {
            if (_prewarmStarted)
            {
                return;
            }
            _prewarmStarted = true;

            // Невидимая форма OnLoad не получает, handle тоже не создаётся
            // без force-touch. Handle создаём на реальном мониторе: координаты
            // -32000 заставляли Windows назначать форме DPI не того экрана.
            StartPosition = FormStartPosition.Manual;
            MoveToScreen(Screen.FromPoint(Cursor.Position));
            GC.KeepAlive(Handle);  // force HWND creation
            DpiAwareness.ApplyInitialFormSize(
                this, new Size(900, 720), new Size(720, 520));
            MoveToScreen(Screen.FromPoint(Cursor.Position));

            if (initializeWebView)
            {
                _ = BeginInitializationAsync();
            }
        }

        /// <summary>
        /// Открыть форму на экране — instant, если pre-warm успел; иначе
        /// упадёт обратно в обычный OnLoad-путь (NavigationCompleted сам
        /// постнёт payload позже).
        /// </summary>
        public void ReloadAndShow()
        {
            _closed = false;
            Outcome = CancelledOutcome;

            // Центрируем вручную: StartPosition.CenterScreen вычисляется
            // в момент создания handle, а он уже создан pre-warm'ом.
            MoveToScreen(Screen.FromPoint(Cursor.Position));

            // Phase 11 P1 (DeepSeek contrast): Show ДО PostWebMessageAsJson.
            // На скрытое окно WebView2 postMessage документирован как
            // работающий, но JS-side обработчики могут трогать DOM-метрики
            // (resize/scroll) — на невидимом окне отдают мусор → render() с
            // битым layout. Сначала Show → форма видима + layout валиден →
            // потом repost свежего payload → settings.js render() с правильным
            // viewport. settings.js handleMessage идемпотентен (render
            // безусловно вызывается на webMessage event).
            Show();
            BringToFront();
            Activate();

            if (_initialized)
            {
                try
                {
                    var payload = BuildLoadPayload();
                    PostWebMessageAsJson(JsonConvert.SerializeObject(payload));
                    if (_probeIntegrations)
                    {
                        _ = CheckCodexStatusAsync();
                        _ = CheckEditorAdapterAsync(false);
                    }
                }
                catch
                {
                    // fail-open: показываем форму с её последним состоянием
                }
            }
        }

        /// <summary>
        /// Явное закрытие на shutdown приложения (Tray.Dispose). Иначе
        /// OnFormClosing превращает Close в Hide, форма живёт вечно.
        /// </summary>
        public void Shutdown()
        {
            _shuttingDown = true;
            if (IsHandleCreated && !IsDisposed)
            {
                Close();
            }
        }

        private void InitializeComponent()
        {
            DpiAwareness.ConfigureForm(this);
            Text = UiText.Current("Настройки Make it ZBS", "Make it ZBS Settings");
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(900, 720);
            MinimumSize = new Size(720, 520);
            BackColor = Color.FromArgb(13, 17, 23);
            Font = SystemFonts.MessageBoxFont;
            // A hidden pre-warmed form has no taskbar button until it is shown.
            // Keep this stable to avoid recreating the WebView2 parent handle.
            ShowInTaskbar = true;
            if (IconResources.App != null)
            {
                Icon = IconResources.App;
            }

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = _webViewUserDataFolder ?? ProjectPaths.WebViewUserDataDir
                }
            };

            Controls.Add(_webView);
        }

        private void MoveToScreen(Screen targetScreen)
        {
            var screen = (targetScreen ?? Screen.PrimaryScreen).WorkingArea;
            Location = new Point(
                screen.X + (screen.Width - Width) / 2,
                screen.Y + (screen.Height - Height) / 2);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.TryEnableDarkTitleBar(Handle);
        }

        protected override void CreateHandle()
        {
            using (new ScreenCapture.PhysicalPixelScope()) base.CreateHandle();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Если pre-warm уже запустил инициализацию — OnLoad ничего не
            // делает. Обычный путь (ShowDialog без pre-warm) — стартуем тут.
            if (!_prewarmStarted)
            {
                DpiAwareness.ApplyInitialFormSize(
                    this, new Size(900, 720), new Size(720, 520));
                MoveToScreen(Screen.FromPoint(Cursor.Position));
                _ = BeginInitializationAsync();
            }
        }

        private async Task BeginInitializationAsync()
        {
            // Phase 11 P1 (DeepSeek contrast): dist-smoke показал ДВА
            // вызова `[zbs.settings] EnsureCoreWebView2` подряд (535+556ms)
            // — корень не очевиден (WebView2 wrap quirk при force-handle?
            // OnLoad на handle creation?), но защита от двойного init
            // = два controller'а на одном control'е = leak ~50-100MB. Guard
            // независим от причины: первый вызов выигрывает, остальные no-op.
            if (System.Threading.Interlocked.CompareExchange(ref _initGuard, 1, 0) != 0)
            {
                return;
            }

            try
            {
                var sw = Stopwatch.StartNew();
                await _webView.EnsureCoreWebView2Async(null);
                Console.WriteLine("[zbs.settings] EnsureCoreWebView2: " + sw.ElapsedMilliseconds + "ms");

                var uiDir = ProjectPaths.UiDir;
                if (string.IsNullOrEmpty(uiDir))
                {
                    HandleInitFailure(L("Каталог ui не найден.", "The ui folder was not found."));
                    return;
                }

                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "zbs.ui",
                    uiDir,
                    CoreWebView2HostResourceAccessKind.Deny);

                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                _webView.CoreWebView2.Navigate(UiOrigin + "/settings.html");
            }
            catch (Exception ex)
            {
                // Типичная причина в pre-warm: на машине нет WebView2 Evergreen
                // Runtime (свежая Win10) → EnsureCoreWebView2Async бросает.
                HandleInitFailure(L(
                    "Не удалось инициализировать настройки: ",
                    "Could not initialize settings: ") + ex.Message);
            }
        }

        // Phase 15 (аудит P1): раньше при сбое инициализации показывался
        // MessageBox.Show(this, ...) — но в pre-warm форма ещё скрыта, так что
        // модал может залипнуть за другими окнами на STA-потоке трея, а форма
        // останется не-Disposed (Hide вместо Close) →
        // fallback в трее (gated на IsDisposed) не срабатывал, «Настройки»
        // открывали вечно пустое окно. Теперь: скрытый pre-warm — без
        // MessageBox, помечаем IsBroken; трей делает fallback на видимую
        // on-demand-форму. Видимая форма (on-demand) — MessageBox как прежде.
        private void HandleInitFailure(string userMessage)
        {
            _initFailed = true;
            if (_prewarmStarted && !Visible)
            {
                Console.WriteLine("[zbs.settings] pre-warm init failed: " + userMessage);
            }
            else
            {
                MessageBox.Show(this, userMessage, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            CloseWith(CancelledOutcome);
        }

        private void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_closed)
            {
                return;
            }

            if (!e.IsSuccess)
            {
                MessageBox.Show(this, L("Не удалось загрузить settings.html.", "Could not load settings.html."), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                CloseWith(CancelledOutcome);
                return;
            }

            try
            {
                var payload = BuildLoadPayload();
                PostWebMessageAsJson(JsonConvert.SerializeObject(payload));
                _initialized = true;
                if (_probeIntegrations)
                {
                    _ = CheckCodexStatusAsync();
                    _ = CheckEditorAdapterAsync(false);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("Не удалось передать данные в настройки: ", "Could not send data to settings: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                CloseWith(CancelledOutcome);
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!ShouldHandleWebMessage(_closed, e.Source, "/settings.html"))
            {
                return;
            }

            try
            {
                var message = WebMessageParser.ReadObject(e.WebMessageAsJson);
                var command = WebMessageParser.ReadCommand(message);

                if (command == "cancel")
                {
                    CloseWith(CancelledOutcome);
                }
                else if (command == "openConfigFolder")
                {
                    OpenConfigFolder();
                }
                else if (command == "openLogsFolder")
                {
                    OpenLogsFolder();
                }
                else if (command == "openAuthorWebsite" || command == "openAuthorTelegram")
                {
                    OpenAuthorLink(ResolveAuthorUrl(command));
                }
                else if (command == "save")
                {
                    SavePayload(message["payload"] as JObject);
                }
                else if (command == "checkCodexStatus")
                {
                    _ = CheckCodexStatusAsync();
                }
                else if (command == "checkEditorAdapter" || command == "installEditorAdapter")
                {
                    _ = CheckEditorAdapterAsync(command == "installEditorAdapter");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("Не удалось обработать команду настроек: ", "Could not process the settings command: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        internal static bool ShouldHandleWebMessage(
            bool closed, string source, string expectedPath)
        {
            return !closed && IsTrustedWebMessageSource(source, expectedPath);
        }

        internal static string ResolveAuthorUrl(string command)
        {
            if (string.Equals(command, "openAuthorWebsite", StringComparison.Ordinal))
            {
                return AuthorWebsiteUrl;
            }

            if (string.Equals(command, "openAuthorTelegram", StringComparison.Ordinal))
            {
                return AuthorTelegramUrl;
            }

            return string.Empty;
        }

        private static string BuildHttpsUrl(string host, string path)
        {
            var builder = new UriBuilder(Uri.UriSchemeHttps, host)
            {
                Path = path
            };
            return builder.Uri.AbsoluteUri.TrimEnd('/');
        }

        internal static bool IsTrustedWebMessageSource(string value, string expectedPath)
        {
            Uri source;
            return Uri.TryCreate(value, UriKind.Absolute, out source)
                && string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && string.Equals(source.Host, "zbs.ui", StringComparison.OrdinalIgnoreCase)
                && string.Equals(source.AbsolutePath, expectedPath, StringComparison.Ordinal);
        }

        internal JObject BuildLoadPayload()
        {
            var payload = new JObject
            {
                [GeneralKey] = new JObject
                {
                    [HotkeyKey] = string.Empty,
                    [LanguageKey] = UiText.Russian,
                    [BackgroundKey] = true,
                    ["autostart"] = SafeIsAutostartEnabled()
                },
                [AiChatKey] = new JObject
                {
                    [ProfilesKey] = AiClientProfiles.CreateDefaults(
                        "continue", AiClientProfiles.DefaultContinueText, AiClientProfiles.DefaultHotkey)
                },
                ["token"] = string.Empty,
                [ContextsKey] = new JObject(),
                ["prompts"] = new JObject(),
                [LoggingKey] = new JObject { [EnabledKey] = false },
                ["catalog"] = new JArray(),
                ["codexStatus"] = BuildCodexStatusPayload(_codexStatus ?? new CodexCliStatus
                {
                    Installed = !string.IsNullOrEmpty(CodexCliClient.FindExecutable())
                }),
                ["editorAdapter"] = BuildEditorAdapterPayload(_adapterStatus),
                ["configError"] = false,
                [ConfigErrorMessageKey] = string.Empty
            };

            var configDir = ProjectPaths.ConfigDir;
            if (string.IsNullOrEmpty(configDir))
            {
                payload["configError"] = true;
                payload[ConfigErrorMessageKey] = L(
                    "Каталог config не найден.", "The config folder was not found.");
                return payload;
            }

            var settingsJson = LoadConfigObject(Path.Combine(configDir, "settings.json"), payload);
            var modelsJson = LoadConfigObject(Path.Combine(configDir, "models.json"), payload);
            var promptsJson = LoadConfigObject(Path.Combine(configDir, "prompts.json"), payload);
            var catalogJson = LoadConfigObject(Path.Combine(configDir, "model-catalog.json"), payload);
            var envPath = Path.Combine(configDir, ".env");

            payload[GeneralKey] = BuildGeneralPayload(settingsJson);
            payload[LoggingKey] = BuildLoggingPayload(settingsJson);
            Text = UiText.Get(
                ReadString(payload[GeneralKey] as JObject, LanguageKey, UiText.Russian),
                "Настройки Make it ZBS",
                "Make it ZBS Settings");
            payload[AiChatKey] = BuildAiChatPayload(settingsJson);
            payload["token"] = SettingsStore.ReadEnvValue(envPath, EnvTokenKey);
            payload[ContextsKey] = BuildContextsPayload(modelsJson);
            payload["prompts"] = BuildPromptsPayload(promptsJson);
            payload["catalog"] = BuildCatalogPayload(catalogJson);

            return payload;
        }

        private static JObject LoadConfigObject(string path, JObject payload)
        {
            try
            {
                if (!File.Exists(path))
                {
                    AppendConfigError(payload, L("Файл не найден: ", "File not found: ") + Path.GetFileName(path));
                    return new JObject();
                }

                return SettingsStore.LoadJObject(path);
            }
            catch (Exception ex)
            {
                AppendConfigError(payload, Path.GetFileName(path) + ": " + ex.Message);
                return new JObject();
            }
        }

        private static void AppendConfigError(JObject payload, string message)
        {
            payload["configError"] = true;
            var current = ReadString(payload, ConfigErrorMessageKey, string.Empty);
            payload[ConfigErrorMessageKey] = string.IsNullOrEmpty(current) ? message : current + Environment.NewLine + message;
        }

        internal static JObject BuildGeneralPayload(JObject settingsJson)
        {
            var general = settingsJson[GeneralKey] as JObject;
            return new JObject
            {
                [HotkeyKey] = ReadString(general, HotkeyKey, string.Empty),
                [LanguageKey] = UiText.NormalizeLanguage(
                    ReadString(general, LanguageKey, UiText.Russian)),
                [BackgroundKey] = ReadBool(general, BackgroundKey, true),
                [OperationFeedbackOptions.Key] = OperationFeedbackOptions.Normalize(ReadString(general, OperationFeedbackOptions.Key, "both")),
                ["autostart"] = SafeIsAutostartEnabled()
            };
        }

        internal static JObject BuildLoggingPayload(JObject settingsJson)
        {
            return new JObject
            {
                [EnabledKey] = ReadBool(settingsJson[LoggingKey] as JObject, EnabledKey, false)
            };
        }

        internal static void ApplyLogging(JObject settingsJson, JObject payload)
        {
            if (payload == null) return;
            var logging = GetOrCreateObject(settingsJson, LoggingKey);
            logging[EnabledKey] = ReadBool(payload, EnabledKey, false);
            logging["diagnosticsOnly"] = true;
            logging["logRequests"] = false;
            logging["logResponses"] = false;
        }

        internal static JObject BuildAiChatPayload(JObject settingsJson)
        {
            var aiChat = settingsJson[AiChatKey] as JObject;
            return new JObject
            {
                [ProfilesKey] = aiChat?[ProfilesKey]?.DeepClone()
                    ?? AiClientProfiles.CreateDefaults(
                        "continue", AiClientProfiles.DefaultContinueText, AiClientProfiles.DefaultHotkey)
            };
        }

        internal static JObject BuildContextsPayload(JObject modelsJson)
        {
            var result = new JObject();
            var contexts = modelsJson[ContextsKey] as JObject;
            if (contexts == null)
            {
                return result;
            }

            foreach (var property in contexts.Properties())
            {
                var context = property.Value as JObject;
                if (context == null)
                {
                    continue;
                }

                result[property.Name] = new JObject
                {
                    [ProviderKey] = NormalizeProvider(ReadString(context, ProviderKey, "openrouter")),
                    [ModelKey] = ReadString(context, ModelKey, string.Empty),
                    [CodexModelKey] = ReadString(context, CodexModelKey, string.Empty),
                    [CodexReasoningEffortKey] = CodexCliClient.NormalizeReasoningEffort(
                        ReadString(context, CodexReasoningEffortKey, string.Empty)),
                    [TemperatureKey] = ReadDecimal(context, TemperatureKey, 0.5m),
                    [MaxTokensKey] = ReadInt(context, MaxTokensKey, 2048)
                };
            }

            return result;
        }

        internal static JObject BuildPromptsPayload(JObject promptsJson)
        {
            var result = new JObject();
            foreach (var property in promptsJson.Properties())
            {
                var prompt = property.Value as JObject;
                if (prompt == null)
                {
                    continue;
                }

                result[property.Name] = new JObject
                {
                    [SystemKey] = ReadString(prompt, SystemKey, string.Empty),
                    ["user"] = ReadString(prompt, "user", string.Empty)
                };
            }

            return result;
        }

        private static JArray BuildCatalogPayload(JObject catalogJson)
        {
            var result = new JArray();
            var models = catalogJson["models"] as JArray;
            if (models == null)
            {
                return result;
            }

            foreach (var token in models)
            {
                var model = token as JObject;
                if (model == null)
                {
                    continue;
                }

                result.Add(new JObject
                {
                    ["slug"] = ReadString(model, "slug", string.Empty),
                    [LabelKey] = ReadString(model, LabelKey, string.Empty),
                    ["kind"] = ReadString(model, "kind", string.Empty)
                });
            }

            return result;
        }

        private void SavePayload(JObject payload)
        {
            if (payload == null)
            {
                throw new InvalidOperationException("payload не передан");
            }

            var configDir = ProjectPaths.ConfigDir;
            if (string.IsNullOrEmpty(configDir))
            {
                MessageBox.Show(this, L("Каталог config не найден.", "The config folder was not found."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var generalPayload = payload[GeneralKey] as JObject ?? new JObject();
            var language = UiText.NormalizeLanguage(
                ReadString(generalPayload, LanguageKey, UiText.Russian));
            Text = UiText.Get(language, "Настройки Make it ZBS", "Make it ZBS Settings");
            var hotkey = ReadString(generalPayload, HotkeyKey, string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(hotkey))
            {
                MessageBox.Show(this, UiText.Get(language,
                    "Горячая клавиша не может быть пустой.",
                    "The keyboard shortcut cannot be empty."),
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var settingsPath = Path.Combine(configDir, "settings.json");
                var modelsPath = Path.Combine(configDir, "models.json");
                var promptsPath = Path.Combine(configDir, "prompts.json");

                var settingsJson = LoadExistingOrNew(settingsPath);
                var modelsJson = LoadExistingOrNew(modelsPath);
                var promptsJson = LoadExistingOrNew(promptsPath);

                ApplyGeneral(settingsJson, generalPayload, hotkey);
                ApplyLogging(settingsJson, payload[LoggingKey] as JObject);
                ApplyAiChat(settingsJson, payload[AiChatKey] as JObject);
                SettingsSchema.MigrateAndNormalize(settingsJson);
                ApplyContexts(modelsJson, payload[ContextsKey] as JObject);
                ApplyPrompts(promptsJson, payload["prompts"] as JObject);

                var token = ReadString(payload, "token", string.Empty).Trim();
                var autostartRequested = ReadBool(generalPayload, "autostart", false);
                var launcherPath = ProjectPaths.LauncherPath;
                var warning = SettingsSaveWorkflow.Commit(
                    () => ConfigurationStore.Save(configDir, new System.Collections.Generic.Dictionary<string, JObject>
                    {
                        ["settings.json"] = settingsJson,
                        ["models.json"] = modelsJson,
                        ["prompts.json"] = promptsJson
                    }, token),
                    () =>
                    {
                        if (autostartRequested && string.IsNullOrEmpty(launcherPath))
                            throw new FileNotFoundException(UiText.Get(language,
                                "Не найден лаунчер MakeItZBS.exe или Make-it-ZBS.bat.",
                                "The MakeItZBS.exe or Make-it-ZBS.bat launcher was not found."));
                        _writeAutostart(autostartRequested, launcherPath);
                    }, SettingsState.MarkDirty);
                if (warning != null)
                {
                    _saveWarning(UiText.Get(language,
                        "Настройки сохранены и применены, но изменить автозапуск не удалось: ",
                        "Settings were saved and applied, but Windows startup could not be changed: ") + warning.Message);
                }
                CloseWith("saved");
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, UiText.Get(language,
                    "Не удалось сохранить настройки: ", "Could not save settings: ") + ex.Message,
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static JObject LoadExistingOrNew(string path)
        {
            return File.Exists(path) ? SettingsStore.LoadJObject(path) : new JObject();
        }

        internal static void ApplyGeneral(JObject settingsJson, JObject generalPayload, string hotkey)
        {
            var general = GetOrCreateObject(settingsJson, GeneralKey);
            general[HotkeyKey] = hotkey;
            general[LanguageKey] = UiText.NormalizeLanguage(
                ReadString(generalPayload, LanguageKey, UiText.Russian));
            general[BackgroundKey] = ReadBool(generalPayload, BackgroundKey, true);
            general[OperationFeedbackOptions.Key] = OperationFeedbackOptions.Normalize(
                ReadString(generalPayload, OperationFeedbackOptions.Key, ReadString(general, OperationFeedbackOptions.Key, "both")));
        }

        internal static void ApplyAiChat(JObject settingsJson, JObject aiChatPayload)
        {
            if (aiChatPayload == null)
            {
                return;
            }

            var sourceProfiles = aiChatPayload[ProfilesKey] as JObject;
            if (sourceProfiles == null)
            {
                return;
            }

            var aiChat = GetOrCreateObject(settingsJson, AiChatKey);
            var targetProfiles = GetOrCreateObject(aiChat, ProfilesKey);
            foreach (var property in sourceProfiles.Properties())
            {
                var source = property.Value as JObject;
                if (source == null || property.Name.Length > 64)
                {
                    continue;
                }

                var target = GetOrCreateObject(targetProfiles, property.Name);
                target[LabelKey] = ReadString(source, LabelKey, property.Name);
                target[EnabledKey] = ReadBool(source, EnabledKey, true);
                target["processes"] = source["processes"] is JArray processList
                    ? processList.DeepClone()
                    : new JArray();
                target[HotkeyKey] = ReadString(source, HotkeyKey, AiClientProfiles.DefaultHotkey);
                target["mode"] = ReadString(source, "mode", "continue");
                target["continueText"] = ReadString(
                    source, "continueText", AiClientProfiles.DefaultContinueText);
                target["sendMode"] = ReadString(source, "sendMode", "submit");
                target["analysisSource"] = ReadString(source, "analysisSource", "localText");
            }
        }

        internal static void ApplyContexts(JObject modelsJson, JObject contextsPayload)
        {
            if (contextsPayload == null)
            {
                return;
            }

            var contexts = GetOrCreateObject(modelsJson, ContextsKey);
            foreach (var property in contextsPayload.Properties())
            {
                var source = property.Value as JObject;
                if (source == null)
                {
                    continue;
                }

                var target = GetOrCreateObject(contexts, property.Name);
                target[ProviderKey] = NormalizeProvider(ReadString(source, ProviderKey, "openrouter"));
                target[ModelKey] = ReadString(source, ModelKey, string.Empty).Trim();
                target[CodexModelKey] = ReadString(source, CodexModelKey, string.Empty).Trim();
                target[CodexReasoningEffortKey] = CodexCliClient.NormalizeReasoningEffort(
                    ReadString(source, CodexReasoningEffortKey, string.Empty));
                target[TemperatureKey] = ReadDecimal(source, TemperatureKey, 0.5m);
                target[MaxTokensKey] = ReadInt(source, MaxTokensKey, 2048);
            }
        }

        internal static void ApplyPrompts(JObject promptsJson, JObject promptsPayload)
        {
            if (promptsPayload == null)
            {
                return;
            }

            foreach (var property in promptsPayload.Properties())
            {
                var source = property.Value as JObject;
                if (source == null)
                {
                    continue;
                }

                var target = GetOrCreateObject(promptsJson, property.Name);
                target[SystemKey] = ReadString(source, SystemKey, string.Empty);
                target["user"] = ReadString(source, "user", string.Empty);
            }
        }

        private void OpenConfigFolder()
        {
            var configDir = ProjectPaths.ConfigDir;
            if (string.IsNullOrEmpty(configDir))
            {
                MessageBox.Show(this, L("Каталог config не найден.", "The config folder was not found."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = configDir,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("Не удалось открыть папку конфигов: ", "Could not open the config folder: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenLogsFolder()
        {
            var configDir = ProjectPaths.ConfigDir;
            if (string.IsNullOrEmpty(configDir)) return;
            try
            {
                var logsDir = Path.GetFullPath(Path.Combine(configDir, "..", "logs"));
                Directory.CreateDirectory(logsDir);
                Process.Start(new ProcessStartInfo { FileName = logsDir, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("Не удалось открыть журнал: ", "Could not open the log folder: ")
                    + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenAuthorLink(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = url,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("Не удалось открыть ссылку автора: ", "Could not open the author link: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async Task CheckCodexStatusAsync()
        {
            var status = await Task.Run(() => CodexCliClient.CheckStatus()).ConfigureAwait(true);
            _codexStatus = status;
            PostWebMessageAsJson(JsonConvert.SerializeObject(new JObject
            {
                ["type"] = "codexStatus",
                ["codexStatus"] = BuildCodexStatusPayload(status)
            }));
        }

        private async Task CheckEditorAdapterAsync(bool install)
        {
            if (_adapterActionActive) return;
            _adapterActionActive = true;
            try
            {
                _adapterStatus = await Task.Run(() =>
                {
                    var setup = new VsCodeAdapterSetup();
                    return install ? setup.Install() : setup.Check();
                }).ConfigureAwait(true);
            }
            catch (Exception) { _adapterStatus = new VsCodeAdapterStatus { Error = "setup-failed" }; }
            finally { _adapterActionActive = false; }
            PostWebMessageAsJson(JsonConvert.SerializeObject(new JObject
            {
                ["type"] = "editorAdapter", ["editorAdapter"] = BuildEditorAdapterPayload(_adapterStatus)
            }));
        }

        internal static JObject BuildEditorAdapterPayload(VsCodeAdapterStatus status) => status == null
            ? new JObject { ["checked"] = false }
            : new JObject
            {
                ["checked"] = true, ["editorFound"] = status.EditorFound,
                ["installed"] = status.Installed, ["current"] = status.Current,
                ["reloadRequired"] = status.ReloadRequired, ["version"] = status.Version,
                ["error"] = status.Error
            };

        internal static JObject BuildCodexStatusPayload(CodexCliStatus status)
        {
            status = status ?? new CodexCliStatus();
            var models = new JArray();
            foreach (var model in status.Models ?? new System.Collections.Generic.List<CodexModelInfo>())
            {
                models.Add(new JObject
                {
                    ["slug"] = model.Slug ?? string.Empty,
                    [LabelKey] = model.DisplayName ?? string.Empty,
                    ["defaultReasoningEffort"] = model.DefaultReasoningEffort ?? string.Empty,
                    ["reasoningEfforts"] = new JArray(model.SupportedReasoningEfforts ?? new System.Collections.Generic.List<string>())
                });
            }

            return new JObject
            {
                ["installed"] = status.Installed,
                ["authenticated"] = status.Authenticated,
                ["subscription"] = status.UsesChatGptSubscription,
                ["version"] = status.Version ?? string.Empty,
                ["diagnostic"] = status.Diagnostic ?? string.Empty,
                ["models"] = models
            };
        }

        internal static string NormalizeProvider(string value)
        {
            return string.Equals(value, "codex", StringComparison.OrdinalIgnoreCase)
                ? "codex"
                : "openrouter";
        }

        private static string L(string russian, string english)
        {
            return UiText.Current(russian, english);
        }

        private void PostWebMessageAsJson(string json)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    try
                    {
                        if (!IsDisposed && _webView != null && !_webView.IsDisposed && _webView.CoreWebView2 != null)
                        {
                            _webView.CoreWebView2.PostWebMessageAsJson(json);
                        }
                    }
                    catch
                    {
                        // The settings page can disappear while a queued refresh is being delivered.
                    }
                }));
            }
            catch
            {
                // BeginInvoke can race with form disposal; the next opening reloads all settings.
            }
        }

        private void CloseWith(string outcome)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Outcome = outcome;
            Close();
        }

        private static bool SafeIsAutostartEnabled()
        {
            try
            {
                return SettingsStore.IsAutostartEnabled();
            }
            catch
            {
                return false;
            }
        }

        private static JObject GetOrCreateObject(JObject parent, string propertyName)
        {
            var value = parent[propertyName] as JObject;
            if (value == null)
            {
                value = new JObject();
                parent[propertyName] = value;
            }

            return value;
        }

        private static string ReadString(JObject obj, string propertyName, string defaultValue)
        {
            if (obj == null)
            {
                return defaultValue;
            }

            var token = obj[propertyName];
            return token == null || token.Type == JTokenType.Null ? defaultValue : token.ToString();
        }

        private static bool ReadBool(JObject obj, string propertyName, bool defaultValue)
        {
            if (obj == null)
            {
                return defaultValue;
            }

            var token = obj[propertyName];
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

        private static decimal ReadDecimal(JObject obj, string propertyName, decimal defaultValue)
        {
            if (obj == null)
            {
                return defaultValue;
            }

            var token = obj[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return defaultValue;
            }

            try
            {
                return token.Value<decimal>();
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

        private static int ReadInt(JObject obj, string propertyName, int defaultValue)
        {
            if (obj == null)
            {
                return defaultValue;
            }

            var token = obj[propertyName];
            if (token == null || token.Type == JTokenType.Null)
            {
                return defaultValue;
            }

            try
            {
                return token.Value<int>();
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

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // Phase 11 reuse: пользовательский Close (Esc / крестик /
            // Cancel button / save success) → НЕ убиваем форму, прячем.
            // Form остаётся в памяти с прогретым WebView2 — следующее
            // открытие из трея = ReloadAndShow → instant. Реальное закрытие
            // только при Shutdown() (из TrayApplicationContext.Dispose).
            if (!_shuttingDown)
            {
                e.Cancel = true;
                Hide();
                return;
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closed = true;
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_webView != null && _webView.CoreWebView2 != null)
                {
                    _webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                    _webView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                }

                if (_webView != null)
                {
                    _webView.Dispose();
                    _webView = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
