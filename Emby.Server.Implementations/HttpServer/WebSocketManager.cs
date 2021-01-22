#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.HttpServer
{
    public class WebSocketManager : IWebSocketManager
    {
        private readonly IWebSocketListener[] _webSocketListeners;
        private readonly ILogger<WebSocketManager> _logger;
        private readonly ILoggerFactory _loggerFactory;

        public WebSocketManager(
            IEnumerable<IWebSocketListener> webSocketListeners,
            ILogger<WebSocketManager> logger,
            ILoggerFactory loggerFactory)
        {
            _webSocketListeners = webSocketListeners.ToArray();
            _logger = logger;
            _loggerFactory = loggerFactory;
        }

        /// <inheritdoc />
        public async Task WebSocketRequestHandler(HttpContext context)
        {
            try
            {
                _logger.LogInformation("WS {IP} request", context.Connection.RemoteIpAddress);

                WebSocket webSocket = await context.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);

                using var connection = new WebSocketConnection(
                    _loggerFactory.CreateLogger<WebSocketConnection>(),
                    webSocket,
                    context.Connection.RemoteIpAddress,
                    context.Request.Query)
                {
                    OnReceive = ProcessWebSocketMessageReceived
                };

                var tasks = new Task[_webSocketListeners.Length];
                for (var i = 0; i < _webSocketListeners.Length; ++i)
                {
                    tasks[i] = _webSocketListeners[i].ProcessWebSocketConnectedAsync(connection);
                }

                await Task.WhenAll(tasks).ConfigureAwait(false);

                await connection.ProcessAsync().ConfigureAwait(false);
                _logger.LogInformation("WS {IP} closed", context.Connection.RemoteIpAddress);
            }
            catch (Exception ex) // Otherwise ASP.Net will ignore the exception
            {
                _logger.LogError(ex, "WS {IP} WebSocketRequestHandler error", context.Connection.RemoteIpAddress);
                if (!context.Response.HasStarted)
                {
                    context.Response.StatusCode = 500;
                }
            }
        }

        /// <summary>
        /// Processes the web socket message received.
        /// </summary>
        /// <param name="result">The result.</param>
        private Task ProcessWebSocketMessageReceived(WebSocketMessageInfo result)
        {
            var tasks = new Task[_webSocketListeners.Length];
            for (var i = 0; i < _webSocketListeners.Length; ++i)
            {
                tasks[i] = _webSocketListeners[i].ProcessMessageAsync(result);
            }

            return Task.WhenAll(tasks);
        }
    }
}
