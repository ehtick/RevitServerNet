using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace RevitServerNet.Tests
{
    /// <summary>
    /// HTTP response returned by <see cref="StubHttpServer"/>.
    /// </summary>
    internal sealed class StubResponse
    {
        public StubResponse(int statusCode, string reasonPhrase, string body)
        {
            StatusCode = statusCode;
            ReasonPhrase = reasonPhrase;
            Body = body;
        }

        public int StatusCode { get; }
        public string ReasonPhrase { get; }
        public string Body { get; }

        public static StubResponse Ok(string json) => new StubResponse(200, "OK", json);
    }

    /// <summary>
    /// Minimal HTTP/1.1 server on 127.0.0.1 for wire-level tests: records the request line
    /// ("METHOD target") exactly as the client sent it and answers with a canned response.
    /// One request per connection (the response carries "Connection: close").
    /// </summary>
    internal sealed class StubHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly Func<string, StubResponse> _respond;
        private readonly List<string> _requests = new List<string>();
        private readonly List<Exception> _errors = new List<Exception>();
        private volatile bool _disposed;

        /// <param name="respond">Builds the response for a request line; null closes the connection without any HTTP response.</param>
        public StubHttpServer(Func<string, StubResponse> respond)
        {
            _respond = respond ?? throw new ArgumentNullException(nameof(respond));
            _listener.Start();
            Host = "127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port;
            Task.Run(() => AcceptLoopAsync());
        }

        /// <summary>
        /// Answers 200 with the JSON mapped to the request line; any other request gets 404.
        /// </summary>
        public static StubHttpServer WithJson(IDictionary<string, string> jsonByRequestLine)
        {
            return new StubHttpServer(requestLine => jsonByRequestLine.TryGetValue(requestLine, out var json)
                ? StubResponse.Ok(json)
                : new StubResponse(404, "Not Found", "Unexpected request: " + requestLine));
        }

        /// <summary>
        /// Host with port to pass to the API client constructors.
        /// </summary>
        public string Host { get; }

        /// <summary>
        /// Received request lines ("METHOD target") in arrival order.
        /// </summary>
        public IReadOnlyList<string> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        /// <summary>
        /// Failures while serving connections.
        /// </summary>
        public IReadOnlyList<Exception> Errors
        {
            get { lock (_errors) return _errors.ToList(); }
        }

        /// <summary>
        /// Returns the only received request line and checks that serving it did not fail.
        /// </summary>
        public string SingleRequest()
        {
            Assert.Empty(Errors);
            return Assert.Single(Requests);
        }

        public void Dispose()
        {
            _disposed = true;
            _listener.Stop();
        }

        private async Task AcceptLoopAsync()
        {
            while (true)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_disposed)
                {
                    return; // Listener stopped by Dispose
                }

                using (client)
                {
                    try
                    {
                        await HandleAsync(client).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        lock (_errors) _errors.Add(ex);
                    }
                }
            }
        }

        private async Task HandleAsync(TcpClient client)
        {
            var stream = client.GetStream();
            var requestLine = await ReadRequestLineAsync(stream).ConfigureAwait(false);
            lock (_requests) _requests.Add(requestLine);

            var response = _respond(requestLine);
            if (response == null)
                return; // Close the connection without an HTTP response

            var body = Encoding.UTF8.GetBytes(response.Body ?? string.Empty);
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + response.StatusCode + " " + response.ReasonPhrase + "\r\n" +
                "Content-Type: application/json; charset=utf-8\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                "Connection: close\r\n" +
                "\r\n");
            await stream.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
            await stream.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            client.Client.Shutdown(SocketShutdown.Send);
        }

        // Reads the request head (requests in these tests carry no body) and returns "METHOD target".
        private static async Task<string> ReadRequestLineAsync(Stream stream)
        {
            var received = new MemoryStream();
            var buffer = new byte[4096];
            string head;
            while (true)
            {
                var read = await stream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
                if (read == 0)
                    throw new IOException("Connection closed before the request head was received");
                received.Write(buffer, 0, read);

                // UTF-8 keeps a target that was sent unescaped readable in assertion messages
                head = Encoding.UTF8.GetString(received.ToArray());
                if (head.Contains("\r\n\r\n"))
                    break;
            }

            var requestLine = head.Substring(0, head.IndexOf("\r\n", StringComparison.Ordinal));
            var parts = requestLine.Split(' ');
            if (parts.Length != 3)
                throw new InvalidDataException("Malformed request line: " + requestLine);
            return parts[0] + " " + parts[1];
        }
    }
}
