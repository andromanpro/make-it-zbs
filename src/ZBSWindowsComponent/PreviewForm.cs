using System;
using System.Drawing;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ZBSWindowsComponent
{
    public class PreviewForm : Form
    {
        private const string CancelledOutcome = "cancelled";
        private const string ErrorStatus = "error";
        private const string ContentKey = "content";
        private const string ReasoningKey = "reasoning";
        private const string UiOrigin = "https://zbs.ui";

        private WebView2 _webView;
        private readonly string _original;
        private readonly string _result;
        private readonly string _context;
        private readonly string _model;
        private readonly string _language;
        private readonly int _timeoutSec;
        private readonly bool _allowApply;
        private readonly string _notice = string.Empty;
        private readonly bool _streaming;
        private readonly string _streamUrl;
        private readonly string _streamAuthHeader;
        private readonly string _streamBodyJson;
        private readonly object _streamLock = new object();
        private System.Windows.Forms.Timer _autoCloseTimer;
        private CancellationTokenSource _streamCancel;
        private Task _streamTask;
        private bool _anyDelta;
        private bool _donePosted;

        public string Outcome { get; private set; }
        public string StreamedContent { get; private set; }
        public string StreamError { get; private set; }
        private bool _closed;
        private WorkOperation _operation;
        private CancellationTokenRegistration _operationCancellation;

        internal void BindOperation(WorkOperation operation)
        {
            _operation = operation;
            if (operation != null)
                _operationCancellation = operation.Token.Register(() => CloseWithOnUi(CancelledOutcome));
        }

        public PreviewForm(string original, string result, string context, string model, int timeoutSec, bool allowApply, string notice = "")
        {
            _original = original ?? string.Empty;
            _result = result ?? string.Empty;
            _context = context ?? string.Empty;
            _model = model ?? string.Empty;
            _language = UiText.CurrentLanguage();
            _timeoutSec = timeoutSec;
            _allowApply = allowApply;
            _notice = notice ?? string.Empty;
            _streaming = false;
            Outcome = CancelledOutcome;
            StreamedContent = string.Empty;
            StreamError = string.Empty;

            InitializeComponent();
        }

        private PreviewForm(StreamPreviewOptions options)
        {
            _original = options.Original;
            _result = string.Empty;
            _context = options.Context;
            _model = options.Model;
            _language = UiText.CurrentLanguage();
            _timeoutSec = options.TimeoutSec;
            _allowApply = options.AllowApply;
            _streaming = true;
            _streamUrl = options.Url;
            _streamAuthHeader = options.AuthHeader;
            _streamBodyJson = options.BodyJson;
            Outcome = CancelledOutcome;
            StreamedContent = string.Empty;
            StreamError = string.Empty;

            InitializeComponent();
        }

        internal static PreviewForm ДляСтрима(StreamPreviewOptions options)
        {
            return new PreviewForm(options);
        }

        private void InitializeComponent()
        {
            DpiAwareness.ConfigureForm(this);
            Text = UiText.Get(_language, "Предпросмотр Make it ZBS", "Make it ZBS Preview");
            StartPosition = FormStartPosition.CenterScreen;
            Size = new Size(900, 650);
            MinimumSize = new Size(720, 460);
            BackColor = Color.FromArgb(13, 17, 23);
            Font = SystemFonts.MessageBoxFont;
            if (IconResources.App != null)
            {
                Icon = IconResources.App;
            }

            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = ProjectPaths.WebViewUserDataDir
                }
            };

            Controls.Add(_webView);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            NativeMethods.TryEnableDarkTitleBar(Handle);
        }

        protected override async void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            if (_operation?.Token.IsCancellationRequested == true) { CloseWith(CancelledOutcome); return; }
            ApplyInitialDpiLayout();

            try
            {
                await _webView.EnsureCoreWebView2Async(null);

                var uiDir = ProjectPaths.UiDir;
                if (string.IsNullOrEmpty(uiDir))
                {
                    MessageBox.Show(this, L("Каталог ui не найден.", "The ui folder was not found."), Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    CloseWith(ErrorStatus);
                    return;
                }

                _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                    "zbs.ui",
                    uiDir,
                    CoreWebView2HostResourceAccessKind.Deny);

                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _webView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                _webView.CoreWebView2.Navigate(UiOrigin + "/preview.html");
                if (!_streaming)
                {
                    StartAutoCloseTimer();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("Не удалось инициализировать предпросмотр: ", "Could not initialize the preview: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                CloseWith(ErrorStatus);
            }
        }

        internal void ApplyInitialDpiLayout()
        {
            DpiAwareness.ApplyInitialFormSize(
                this, new Size(900, 650), new Size(720, 460));
            CenterToScreen();
        }

        private void CoreWebView2_NavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (_closed)
            {
                return;
            }

            if (!e.IsSuccess)
            {
                MessageBox.Show(this, L("Не удалось загрузить preview.html.", "Could not load preview.html."), Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                CloseWith(ErrorStatus);
                return;
            }

            try
            {
                // Структурированная передача данных (а не конкатенация в
                // ExecuteScriptAsync): ноль инъекционной поверхности.
                // NavigationCompleted => preview.js уже выполнен и его
                // message-listener установлен, доставка не теряется.
                var payload = _streaming
                    ? JsonConvert.SerializeObject(new
                    {
                        streaming = true,
                        original = _original,
                        result = string.Empty,
                        context = _context,
                        model = _model,
                        language = _language,
                        allowApply = _allowApply
                    })
                    : JsonConvert.SerializeObject(new
                    {
                        original = _original,
                        result = _result,
                        notice = _notice,
                        context = _context,
                        model = _model,
                        language = _language,
                        allowApply = _allowApply,
                        integrity = ResultIntegrity.Compare(_original, _result, _context)
                    });

                PostWebMessageAsJson(payload);

                if (_streaming)
                {
                    StartStreamLoop();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("Не удалось передать данные в предпросмотр: ", "Could not send data to the preview: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                CloseWith(ErrorStatus);
            }
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (_operation?.Token.IsCancellationRequested == true) { CloseWith(CancelledOutcome); return; }
            if (_closed || !IsTrustedWebMessageSource(e, "/preview.html"))
            {
                return;
            }

            try
            {
                var message = WebMessageParser.ReadObject(e.WebMessageAsJson);
                var command = WebMessageParser.ReadCommand(message);
                if (command == "copy")
                {
                    Clipboard.SetText(GetCurrentResult());
                    CloseWith("copied");
                }
                else if (command == "apply")
                {
                    TryApply(message["integrityAcknowledged"]?.Type == JTokenType.Boolean
                        && (bool)message["integrityAcknowledged"]);
                }
                else if (command == "cancel")
                {
                    CloseWith(CancelledOutcome);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, L("Не удалось обработать команду предпросмотра: ", "Could not process the preview command: ") + ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                CloseWith(CancelledOutcome);
            }
        }

        internal bool TryApply(bool integrityAcknowledged)
        {
            var result = GetCurrentResult();
            if (_closed || _operation?.Token.IsCancellationRequested == true || !_allowApply || string.IsNullOrWhiteSpace(result)
                || (_streaming && (!_donePosted || !string.IsNullOrEmpty(StreamError)))) return false;
            if (ResultIntegrity.Compare(_original, result, _context).RequiresReview && !integrityAcknowledged) return false;
            CloseWith("applied");
            return true;
        }

        private static bool IsTrustedWebMessageSource(
            CoreWebView2WebMessageReceivedEventArgs e, string expectedPath)
        {
            Uri source;
            return e != null
                && Uri.TryCreate(e.Source, UriKind.Absolute, out source)
                && string.Equals(source.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && string.Equals(source.Host, "zbs.ui", StringComparison.OrdinalIgnoreCase)
                && string.Equals(source.AbsolutePath, expectedPath, StringComparison.Ordinal);
        }

        internal Task StartStreamLoop()
        {
            if (_streamTask != null)
            {
                return _streamTask;
            }

            _streamCancel = CancellationTokenSource.CreateLinkedTokenSource(
                _operation?.Token ?? CancellationToken.None);
            // Phase 15 (аудит): HttpWebRequest.Timeout/ReadWriteTimeout — no-op
            // для async GetResponseAsync/ReadLineAsync, поэтому если сервер
            // ответил 200 и замолчал (нет байтов, нет [DONE]), ReadLineAsync
            // никогда не вернётся → окно висит вечно, спиннер крутится,
            // соединение и Task живут. Предохранитель — IDLE-таймаут (сброс на
            // каждой прочитанной строке в StreamLoopAsync): гибнут только реально
            // зависшие стримы (60с тишины), здоровый длинный ответ не рвётся.
            // Cancellation aborts this request; it must never create a replacement request.
            _streamCancel.CancelAfter(StreamIdleTimeout);
            _streamTask = Task.Run(async () => await StreamLoopAsync(_streamCancel.Token).ConfigureAwait(false));
            return _streamTask;
        }

        private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(60);

        internal async Task StreamLoopAsync(CancellationToken cancellationToken)
        {
            var request = CreateStreamRequest();
            var cancellationRegistration = default(CancellationTokenRegistration);

            try
            {
                cancellationRegistration = cancellationToken.Register(() => TryAbort(request));
                await WriteRequestBodyAsync(request).ConfigureAwait(false);

                using (var response = (HttpWebResponse)await request.GetResponseAsync().ConfigureAwait(false))
                {
                    var completed = await ProcessStreamResponseAsync(response, cancellationToken)
                        .ConfigureAwait(false);
                    HandleUnexpectedStreamEnd(completed, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                HandleStreamCancellation();
            }
            catch (ObjectDisposedException)
            {
                // Closing the form disposes the request while the background stream is unwinding.
            }
            catch (WebException ex)
            {
                await HandleWebExceptionAsync(ex).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                HandleStreamException(ex);
            }
            finally
            {
                cancellationRegistration.Dispose();
            }
        }

        internal HttpWebRequest CreateStreamRequest()
        {
            var request = (HttpWebRequest)WebRequest.Create(_streamUrl);
            request.Method = "POST";
            request.Timeout = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
            request.ReadWriteTimeout = (int)TimeSpan.FromMinutes(5).TotalMilliseconds;
            request.Accept = "text/event-stream";
            request.ContentType = "application/json";
            request.Headers["X-Title"] = "MakeItZBS";

            if (!string.IsNullOrEmpty(_streamAuthHeader))
            {
                request.Headers[HttpRequestHeader.Authorization] = _streamAuthHeader;
            }

            return request;
        }

        internal static void TryAbort(HttpWebRequest request)
        {
            try
            {
                request.Abort();
            }
            catch
            {
                // Abort is best effort when cancellation races with response disposal.
            }
        }

        private async Task WriteRequestBodyAsync(HttpWebRequest request)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(_streamBodyJson);
            request.ContentLength = bodyBytes.Length;
            using (var requestStream = await request.GetRequestStreamAsync().ConfigureAwait(false))
            {
                await requestStream.WriteAsync(bodyBytes, 0, bodyBytes.Length).ConfigureAwait(false);
            }
        }

        private async Task<bool> ProcessStreamResponseAsync(
            HttpWebResponse response, CancellationToken cancellationToken)
        {
            var statusCode = (int)response.StatusCode;
            if (statusCode < 200 || statusCode >= 300)
            {
                await ReportHttpErrorAsync(response).ConfigureAwait(false);
                return true;
            }

            using (var stream = response.GetResponseStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                return await ReadEventStreamAsync(reader, cancellationToken).ConfigureAwait(false);
            }
        }

        internal async Task<bool> ReadEventStreamAsync(
            StreamReader reader, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                {
                    return false;
                }

                _streamCancel?.CancelAfter(StreamIdleTimeout);
                string payload;
                if (!TryGetSsePayload(line, out payload))
                {
                    continue;
                }

                if (payload == "[DONE]")
                {
                    PostStreamDone();
                    return true;
                }

                ReadSseDelta(payload);
            }

            return false;
        }

        internal static bool TryGetSsePayload(string line, out string payload)
        {
            payload = string.Empty;
            if (line.Length == 0
                || line.StartsWith(":", StringComparison.Ordinal)
                || !line.StartsWith("data:", StringComparison.Ordinal))
            {
                return false;
            }

            payload = line.Substring(5).Trim();
            return payload.Length > 0;
        }

        private async Task ReportHttpErrorAsync(HttpWebResponse response)
        {
            var body = await ReadResponseBodyAsync(response).ConfigureAwait(false);
            var text = "HTTP " + (int)response.StatusCode + ": " + Truncate(body);
            StreamError = text;
            PostStreamEvent(ErrorStatus, text);
            CloseWithOnUi("stream-error");
        }

        internal void HandleUnexpectedStreamEnd(bool completed, CancellationToken cancellationToken)
        {
            if (!completed && !cancellationToken.IsCancellationRequested)
            {
                HandleStreamException(new EndOfStreamException(L(
                    "SSE-поток завершился без маркера [DONE]",
                    "The SSE stream ended without the [DONE] marker")));
            }
        }

        internal void HandleStreamCancellation()
        {
            if (!_closed && !IsDisposed)
            {
                StreamError = L(
                    "стрим прерван по таймауту (сервер не отвечает)",
                    "the stream timed out because the server did not respond");
                CloseWithOnUi("stream-error");
            }
        }

        internal async Task HandleWebExceptionAsync(WebException exception)
        {
            if (_closed || IsDisposed)
            {
                return;
            }

            var response = exception.Response as HttpWebResponse;
            if (response == null || _anyDelta)
            {
                HandleStreamException(exception);
                return;
            }

            using (response)
            {
                await ReportHttpErrorAsync(response).ConfigureAwait(false);
            }
        }

        internal static async Task<string> ReadResponseBodyAsync(WebResponse response)
        {
            using (var stream = response.GetResponseStream())
            {
                if (stream == null)
                {
                    return string.Empty;
                }

                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    return await reader.ReadToEndAsync().ConfigureAwait(false);
                }
            }
        }

        internal void HandleStreamException(Exception ex)
        {
            if (_closed || IsDisposed)
            {
                return;
            }

            var message = Truncate(ex.Message);

            // Phase 15 (аудит P1): стрим оборван (Wi-Fi моргнул и т.п.).
            // РАНЬШЕ при _anyDelta=true (уже пришли токены) StreamError НЕ
            // ставился, а PostStreamDone помечал стрим «успешным» → oscript
            // трактовал ОБРЕЗАННЫЙ StreamedContent как готовый результат, а
            // «Применить»/«Копировать» вставляли пол-ответа поверх кода.
            // StreamError передаётся в oscript без автоматического повтора
            // платного запроса. Частичный ответ нельзя применить как готовый.
            StreamError = _anyDelta
                ? L("стрим прерван после частичного ответа: ", "stream interrupted after a partial response: ") + message
                : message;
            if (_anyDelta)
            {
                PostStreamEvent(ErrorStatus, L("стрим прерван: ", "stream interrupted: ") + message);
            }
            CloseWithOnUi("stream-error");
        }

        internal void ReadSseDelta(string payload)
        {
            var root = JObject.Parse(payload);
            var delta = root["choices"] != null && root["choices"].HasValues
                ? root["choices"][0]["delta"]
                : null;

            if (delta == null)
            {
                return;
            }

            var reasoning = delta[ReasoningKey] != null && delta[ReasoningKey].Type == JTokenType.String
                ? delta[ReasoningKey].Value<string>()
                : string.Empty;
            var content = delta[ContentKey] != null && delta[ContentKey].Type == JTokenType.String
                ? delta[ContentKey].Value<string>()
                : string.Empty;

            if (!string.IsNullOrEmpty(reasoning))
            {
                _anyDelta = true;
                PostStreamEvent(ReasoningKey, reasoning);
            }

            if (!string.IsNullOrEmpty(content))
            {
                _anyDelta = true;
                lock (_streamLock)
                {
                    StreamedContent += content;
                }

                PostStreamEvent(ContentKey, content);
            }
        }

        internal void PostStreamEvent(string type, string text)
        {
            var payload = JsonConvert.SerializeObject(new
            {
                type = type,
                text = text ?? string.Empty
            });

            PostWebMessageAsJson(payload);
        }

        internal void PostStreamDone()
        {
            if (_donePosted)
            {
                return;
            }

            _donePosted = true;
            var payload = JsonConvert.SerializeObject(new
            {
                type = "done",
                integrity = ResultIntegrity.Compare(_original, GetCurrentResult(), _context)
            });

            PostWebMessageAsJson(payload);
            StartAutoCloseTimerOnUi();
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
                        // The form may be disposed between the guard and the queued UI callback.
                    }
                }));
            }
            catch
            {
                // BeginInvoke can race with handle destruction; no UI update is then required.
            }
        }

        private void CloseWithOnUi(string outcome)
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)(() => CloseWith(outcome)));
            }
            catch
            {
                // The form is already closing, so no second close operation is needed.
            }
        }

        private void StartAutoCloseTimerOnUi()
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)StartAutoCloseTimer);
            }
            catch
            {
                // Timer startup is optional after the form has begun closing.
            }
        }

        private string GetCurrentResult()
        {
            if (!_streaming)
            {
                return _result;
            }

            lock (_streamLock)
            {
                return StreamedContent ?? string.Empty;
            }
        }

        internal static string Truncate(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var compact = text.Replace("\r", " ").Replace("\n", " ");
            return compact.Length <= 2000 ? compact : compact.Substring(0, 2000) + "...";
        }

        private string L(string russian, string english)
        {
            return UiText.Get(_language, russian, english);
        }

        // Идемпотентное закрытие: повторные/гоночные вызовы (async-обработчики,
        // WebMessage после ошибки) не перезаписывают Outcome и не зовут Close дважды.
        private void CloseWith(string outcome)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
            Outcome = outcome;
            DisposeAutoCloseTimer();
            Close();
        }

        private void StartAutoCloseTimer()
        {
            if (_timeoutSec <= 0 || _closed || _autoCloseTimer != null)
            {
                return;
            }

            if (ResultIntegrity.Compare(_original, GetCurrentResult(), _context).RequiresReview) return;

            _autoCloseTimer = new System.Windows.Forms.Timer
            {
                Interval = _timeoutSec * 1000
            };
            _autoCloseTimer.Tick += AutoCloseTimer_Tick;
            _autoCloseTimer.Start();
        }

        private void AutoCloseTimer_Tick(object sender, EventArgs e)
        {
            CloseWith("timeout");
        }

        private void DisposeAutoCloseTimer()
        {
            if (_autoCloseTimer == null)
            {
                return;
            }

            _autoCloseTimer.Tick -= AutoCloseTimer_Tick;
            _autoCloseTimer.Stop();
            _autoCloseTimer.Dispose();
            _autoCloseTimer = null;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closed = true;
            if (_streamCancel != null)
            {
                try
                {
                    _streamCancel.Cancel();
                }
                catch
                {
                    // Cancellation is idempotent and may race with token-source disposal.
                }
            }

            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _operationCancellation.Dispose();
                DisposeAutoCloseTimer();
                if (_streamCancel != null)
                {
                    var streamCancel = _streamCancel;
                    _streamCancel = null;
                    try
                    {
                        streamCancel.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // A concurrent stream shutdown may have already disposed the source.
                    }
                    streamCancel.Dispose();
                }

                if (_webView != null)
                {
                    if (_webView.CoreWebView2 != null)
                    {
                        _webView.CoreWebView2.WebMessageReceived -= CoreWebView2_WebMessageReceived;
                        _webView.CoreWebView2.NavigationCompleted -= CoreWebView2_NavigationCompleted;
                    }

                    _webView.Dispose();
                    _webView = null;
                }
            }

            base.Dispose(disposing);
        }
    }
}
