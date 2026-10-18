using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Diagnostics;
using NUnit.Framework;
using ScriptEngine.Machine;
using ScriptEngine.Machine.Contexts;

namespace ZBSWindowsComponent.Tests
{
    [TestFixture]
    public class OpenRouterHttpTransportTests
    {
        [Test]
        public void CancelAbortsARequestWhoseServerNeverReplies()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            using (var cancellation = new CancellationTokenSource())
            {
                listener.Start();
                var server = Task.Run(() =>
                {
                    using (var client = listener.AcceptTcpClient())
                    using (var stream = client.GetStream())
                    {
                        stream.ReadTimeout = 3000;
                        ReadRequest(stream);
                        cancellation.Cancel();
                        try { return stream.ReadByte(); }
                        catch (IOException ex) when (ex.InnerException is SocketException socket
                            && socket.SocketErrorCode == SocketError.ConnectionReset) { return -1; }
                    }
                });
                try
                {
                    var elapsed = Stopwatch.StartNew();
                    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                    Assert.Throws<OperationCanceledException>(() => OpenRouterHttpTransport.Execute(new OpenRouterHttpRequest(
                        "http://127.0.0.1:" + port + "/stall", "GET", "", "", "", "", 30), cancellation.Token));
                    Assert.That(elapsed.Elapsed, Is.LessThan(TimeSpan.FromSeconds(3)));
                    Assert.That(server.GetAwaiter().GetResult(), Is.EqualTo(-1), "Client actually closed the connection.");
                }
                finally { listener.Stop(); }
            }
        }

        [Test]
        public void BufferedPostReturnsErrorStatusAndBody()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var server = Task.Run(() => ServeOnce(listener));

                var result = OpenRouterHttpTransport.Execute(new OpenRouterHttpRequest(
                    "http://127.0.0.1:" + endpoint.Port + "/chat/completions",
                    "POST",
                    "{\"model\":\"test\"}",
                    "Bearer local-test",
                    "https://androman.pro",
                    "MakeItZBS",
                    10));

                var requestText = server.GetAwaiter().GetResult();
                Assert.Multiple(() =>
                {
                    Assert.That(result.StatusCode, Is.EqualTo(401));
                    Assert.That(result.Body, Is.EqualTo("{\"error\":\"неверный ключ\"}"));
                    Assert.That(requestText, Does.Contain("POST /chat/completions HTTP/1.1"));
                    Assert.That(requestText, Does.Contain("Authorization: Bearer local-test"));
                    Assert.That(requestText, Does.Contain("HTTP-Referer: https://androman.pro"));
                    Assert.That(requestText, Does.Contain("X-Title: MakeItZBS"));
                    Assert.That(requestText, Does.Contain("User-Agent: MakeItZBS/0.5"));
                    Assert.That(requestText, Does.EndWith("{\"model\":\"test\"}"));
                });
            }
            finally
            {
                listener.Stop();
            }
        }

        [Test]
        public void PublicBoundaryOnlyAllowsOpenRouterHttps()
        {
            Assert.That(OpenRouterHttpTransport.Constructor(),
                Is.TypeOf<OpenRouterHttpTransport>());
            Assert.DoesNotThrow(() => OpenRouterHttpTransport.ValidateOpenRouterUrl(
                "https://openrouter.ai/api/v1/models"));
            Assert.Throws<RuntimeException>(() => OpenRouterHttpTransport.ValidateOpenRouterUrl(
                "http://127.0.0.1/private"));
            Assert.That(OpenRouterHttpTransport.NormalizeMethod(" get "), Is.EqualTo("GET"));
            Assert.Throws<ArgumentException>(() => OpenRouterHttpTransport.NormalizeMethod(null));
            Assert.Throws<ArgumentException>(() => OpenRouterHttpTransport.NormalizeMethod("DELETE"));
            Assert.Throws<RuntimeException>(() => OpenRouterHttpTransport.Constructor().Send(
                "http://127.0.0.1/private", "GET", "", "", "", "", 10));
            Assert.Multiple(() =>
            {
                Assert.That(OpenRouterHttpTransport.ClampTimeout(1), Is.EqualTo(10));
                Assert.That(OpenRouterHttpTransport.ClampTimeout(120), Is.EqualTo(120));
                Assert.That(OpenRouterHttpTransport.ClampTimeout(1000), Is.EqualTo(600));
            });
        }

        [Test]
        public void ScriptBoundaryReturnsStructureAndRedactsTransportErrors()
        {
            var value = OpenRouterHttpTransport.ExecuteForScript(
                () => new OpenRouterHttpResult(200, null));
            var context = (IRuntimeContextInstance)value;

            Assert.Multiple(() =>
            {
                Assert.That(context.GetPropValue(context.FindProperty("statusCode")).AsNumber(),
                    Is.EqualTo(200));
                Assert.That(context.GetPropValue(context.FindProperty("body")).AsString(), Is.Empty);
            });

            var error = Assert.Throws<RuntimeException>(() =>
                OpenRouterHttpTransport.ExecuteForScript(() =>
                    throw new InvalidOperationException(
                        "Authorization: Bearer abcdefghijklmnopqrstuvwxyz")));
            Assert.That(error.Message, Does.Contain("Ошибка соединения с OpenRouter"));
            Assert.That(error.Message, Does.Contain("[REDACTED]"));
            Assert.That(error.Message, Does.Not.Contain("abcdefghijklmnopqrstuvwxyz"));

            var wrapped = Assert.Throws<RuntimeException>(() =>
                OpenRouterHttpTransport.ExecuteForScript(() =>
                    throw new System.Reflection.TargetInvocationException(
                        new InvalidOperationException("inner failure"))));
            Assert.That(wrapped.Message, Does.Contain("inner failure"));
        }

        [Test]
        public void BufferedGetOmitsOptionalHeadersAndBody()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                listener.Start();
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var server = Task.Run(() => ServeOnce(listener));

                var result = OpenRouterHttpTransport.Execute(new OpenRouterHttpRequest(
                    "http://127.0.0.1:" + endpoint.Port + "/models",
                    "GET", "ignored", "", "", "", 10));
                var requestText = server.GetAwaiter().GetResult();

                Assert.Multiple(() =>
                {
                    Assert.That(result.StatusCode, Is.EqualTo(401));
                    Assert.That(requestText, Does.Contain("GET /models HTTP/1.1"));
                    Assert.That(requestText, Does.Not.Contain("Authorization:"));
                    Assert.That(requestText, Does.Not.Contain("HTTP-Referer:"));
                    Assert.That(requestText, Does.Not.Contain("X-Title:"));
                    Assert.That(requestText, Does.Not.EndWith("ignored"));
                });
            }
            finally
            {
                listener.Stop();
            }
        }

        private static string ServeOnce(TcpListener listener)
        {
            using (var client = listener.AcceptTcpClient())
            using (var stream = client.GetStream())
            {
                var request = ReadRequest(stream);
                const string body = "{\"error\":\"неверный ключ\"}";
                var response = "HTTP/1.1 401 Unauthorized\r\nContent-Type: application/json\r\n"
                    + "Content-Length: " + Encoding.UTF8.GetByteCount(body)
                    + "\r\nConnection: close\r\n\r\n" + body;
                var bytes = Encoding.UTF8.GetBytes(response);
                stream.Write(bytes, 0, bytes.Length);
                return request;
            }
        }

        private static string ReadRequest(NetworkStream stream)
        {
            var buffer = new byte[1024];
            using (var memory = new MemoryStream())
            {
                var headerEnd = -1;
                var contentLength = 0;
                while (headerEnd < 0 || memory.Length < headerEnd + 4 + contentLength)
                {
                    var read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0)
                    {
                        break;
                    }
                    memory.Write(buffer, 0, read);
                    var text = Encoding.UTF8.GetString(memory.ToArray());
                    headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (headerEnd >= 0)
                    {
                        foreach (var line in text.Substring(0, headerEnd).Split('\n'))
                        {
                            if (line.StartsWith("Content-Length:",
                                StringComparison.OrdinalIgnoreCase))
                            {
                                contentLength = int.Parse(
                                    line.Substring(line.IndexOf(':') + 1).Trim());
                            }
                        }
                    }
                }
                return Encoding.UTF8.GetString(memory.ToArray());
            }
        }
    }
}
