using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Owin;
using Owin.WebSocket.Extensions;

namespace Owin.WebSocket
{
    public abstract class WebSocketConnection
    {
        private readonly TaskQueue mSendQueue;
        private readonly CancellationTokenSource mCancellToken;
        private System.Net.WebSockets.WebSocket mWebSocket;

        /// <summary>
        /// Owin context for the web socket
        /// </summary>
        public IOwinContext Context { get; private set; }

        /// <summary>
        /// Maximum message size in bytes for the receive buffer
        /// </summary>
        public int MaxMessageSize { get; private set; }

        /// <summary>
        /// The underlying websocket connection
        /// </summary>
        public System.Net.WebSockets.WebSocket WebSocket { get { return mWebSocket; } }

        /// <summary>
        /// Arguments captured from URI using Regex
        /// </summary>
        public Dictionary<string, string> Arguments { get; private set; }

        /// <summary>
        /// Queue of send operations to the client
        /// </summary>
        public TaskQueue QueueSend { get { return mSendQueue;} }

        protected WebSocketConnection(int maxMessageSize = 1024*64)
        {
            mSendQueue = new TaskQueue();
            mCancellToken = new CancellationTokenSource();
            MaxMessageSize = maxMessageSize;
        }
        
        /// <summary>
        /// Closes the websocket connection
        /// </summary>
        /// <returns></returns>
        public Task Close(WebSocketCloseStatus status, string reason)
        {
            return mWebSocket.CloseAsync(status, reason, CancellationToken.None);
        }

        /// <summary>
        /// Sends data to the client with binary message type
        /// </summary>
        /// <param name="buffer">Data to send</param>
        /// <param name="endOfMessage">End of the message?</param>
        /// <returns>Task to send the data</returns>
        public Task SendAsyncBinary(byte[] buffer, bool endOfMessage)
        {
            return SendAsync(new ArraySegment<byte>(buffer), endOfMessage, WebSocketMessageType.Binary);
        }

        /// <summary>
        /// Sends data to the client with the text message type
        /// </summary>
        /// <param name="buffer">Data to send</param>
        /// <param name="endOfMessage">End of the message?</param>
        /// <returns>Task to send the data</returns>
        public Task SendAsyncText(byte[] buffer, bool endOfMessage)
        {
            return SendAsync(new ArraySegment<byte>(buffer), endOfMessage, WebSocketMessageType.Text);
        }

        /// <summary>
        /// Sends data to the client
        /// </summary>
        /// <param name="buffer">Data to send</param>
        /// <param name="endOfMessage">End of the message?</param>
        /// <param name="type">Message type of the data</param>
        /// <returns>Task to send the data</returns>
        public Task SendAsync(ArraySegment<byte> buffer, bool endOfMessage, WebSocketMessageType type)
        {
            var sendContext = new SendContext { Buffer = buffer, EndOfMessage = endOfMessage, Type = type };

            return mSendQueue.Enqueue(
                async s =>
                    {
                        await mWebSocket.SendAsync(s.Buffer, s.Type, s.EndOfMessage, CancellationToken.None);
                    },
                sendContext);
        }

        /// <summary>
        /// Close the websocket connection using the close handshake
        /// </summary>
        /// <param name="status">Reason for closing the websocket connection</param>
        /// <param name="statusDescription">Human readable explanation of why the connection was closed</param>
        public Task CloseConnection(WebSocketCloseStatus status, string statusDescription)
        {
            return mWebSocket.CloseAsync(status, statusDescription, CancellationToken.None);
        }

        /// <summary>
        /// Verify the request
        /// </summary>
        /// <param name="request">Websocket request</param>
        /// <returns>True if the request is authenticated else false to throw unauthenticated and deny the connection</returns>
        public virtual bool AuthenticateRequest(IOwinRequest request)
        {
            return true;
        }

        /// <summary>
        /// Fires after the websocket has been opened with the client
        /// </summary>
        public virtual async Task OnOpen()
        {
            await Task.FromResult(0);
        }
        
        /// <summary>
        /// Fires when data is received from the client
        /// </summary>
        /// <param name="message">Data that was received</param>
        /// <param name="type">Message type of the data</param>
        public virtual async Task OnMessageReceived(ArraySegment<byte> message, WebSocketMessageType type)
        {
            await Task.FromResult(0);
        }

        /// <summary>
        /// Fires with the connection with the client has closed
        /// </summary>
        /// <param name="closeStatus">Status for the web socket close status</param>
        /// <param name="closeDescription">Description for the web socket close</param>
        public virtual async Task OnClose(WebSocketCloseStatus closeStatus, string closeDescription)
        {
            await Task.FromResult(0);
        }

        /// <summary>
        /// Fires when an exception occurs in the message reading loop
        /// </summary>
        /// <param name="error">Error that occured</param>
        public virtual void OnReceiveError(Exception error)
        {
        }

        /// <summary>
        /// Receive one entire message from the web socket
        /// </summary>
        protected async Task<Tuple<ArraySegment<byte>, WebSocketMessageType>> ReceiveOneMessage(byte[] buffer)
        {
            var count = 0;
            WebSocketReceiveResult result;
            do
            {
                var segment = new ArraySegment<byte>(buffer, count, buffer.Length - count);
                result = await mWebSocket.ReceiveAsync(segment, mCancellToken.Token);

                count += result.Count;
            }
            while (!result.EndOfMessage);

            return new Tuple<ArraySegment<byte>, WebSocketMessageType>(new ArraySegment<byte>(buffer, 0, count), result.MessageType);
        }

        internal void AcceptSocket(IOwinContext context, IDictionary<string, string> argumentMatches)
        {
            var accept = context.Get<Action<IDictionary<string, object>, Func<IDictionary<string, object>, Task>>>("websocket.Accept");
            if (accept == null)
            {
                // Bad Request
                context.Response.StatusCode = 400;
                context.Response.Write("Not a valid websocket request");
                return;
            }

            Arguments = new Dictionary<string, string>(argumentMatches);

            var responseBuffering = context.Environment.Get<Action>("server.DisableResponseBuffering");
            if (responseBuffering != null)
                responseBuffering();

            var responseCompression = context.Environment.Get<Action>("systemweb.DisableResponseCompression");
            if (responseCompression != null)
                responseCompression();

            context.Response.Headers.Set("X-Content-Type-Options", "nosniff");

            Context = context;

            if (AuthenticateRequest(context.Request))
            {
                //user was authed so accept the socket
                accept(null, RunWebSocket);
                return;
            }

            //see if user was forbidden or unauthorized from previous authenticaterequest failure
            if (context.Request.User != null && context.Request.User.Identity.IsAuthenticated)
            {
                context.Response.StatusCode = 403;
            }
            else
            {
                context.Response.StatusCode = 401;
            }
        }

        private async Task RunWebSocket(IDictionary<string, object> websocketContext)
        {
            // Try to get the websocket context from the environment
            object value;
            if (!websocketContext.TryGetValue(typeof(WebSocketContext).FullName, out value))
            {
                throw new InvalidOperationException("Unable to find web socket context");
            }

            mWebSocket = ((WebSocketContext)value).WebSocket;

            await OnOpen();

            var buffer = new byte[MaxMessageSize];

            do
            {
                try
                {
                    var received = await ReceiveOneMessage(buffer);
                    if (received.Item1.Count > 0)
                        await OnMessageReceived(received.Item1, received.Item2);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (OperationCanceledException oce)
                {
                    if (!mCancellToken.IsCancellationRequested)
                    {
                        OnReceiveError(oce);
                    }
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (IsFatalSocketException(ex))
                    {
                        OnReceiveError(ex);
                    }
                    break;
                }
            }
            while (!mWebSocket.CloseStatus.HasValue);

            try
            {
                if (mWebSocket.State != WebSocketState.Closed && mWebSocket.State != WebSocketState.Aborted)
                {
                    await mWebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, string.Empty, CancellationToken.None);
                }
            }
            catch
            { //Ignore
            }

            mCancellToken.Cancel();

            await OnClose(mWebSocket.CloseStatus.GetValueOrDefault(WebSocketCloseStatus.Empty),
                mWebSocket.CloseStatusDescription);
        }

        internal static bool IsFatalSocketException(Exception ex)
        {
            // If this exception is due to the underlying TCP connection going away, treat as a normal close
            // rather than a fatal exception.
            var ce = ex as COMException;
            if (ce != null)
            {
                switch ((uint)ce.ErrorCode)
                {
                    case 0x800703e3:
                    case 0x800704cd:
                    case 0x80070026:
                        return false;
                }
            }

            // unknown exception; treat as fatal
            return true;
        }
    }

    internal class SendContext
    {
        public ArraySegment<byte> Buffer;
        public bool EndOfMessage;
        public WebSocketMessageType Type;
    }
}