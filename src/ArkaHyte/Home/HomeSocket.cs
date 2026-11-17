using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ArkaHyte.Home;

/// <summary>The server refused the access token (auth_invalid): retrying will not help until it changes.</summary>
public sealed class HomeAuthException : Exception
{
	public HomeAuthException() : base("Home Assistant refused the access token") { }
}

/// <summary>
/// One authenticated connection to the Home Assistant WebSocket API (/api/websocket): numbered requests
/// answered by a result message, subscriptions whose events carry the request's id. Messages are read on a
/// background loop; event handlers run there and must not keep the element (its document is disposed after).
/// </summary>
internal sealed class HomeSocket : IAsyncDisposable
{
	private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
	private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

	private readonly ClientWebSocket _socket = new();
	private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
	private readonly ConcurrentDictionary<int, Action<JsonElement>> _subscriptions = new();
	private readonly SemaphoreSlim _sendLock = new(1, 1);
	private readonly CancellationTokenSource _closing = new();
	private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
	// Reused for every message: the host allocates as little as it can (see CLAUDE.md, host memory)
	private readonly byte[] _buffer = new byte[16 * 1024];
	private readonly MemoryStream _message = new();
	private int _nextId;

	public string Version { get; private set; } = string.Empty;

	/// <summary>Completes when the connection is lost or closed.</summary>
	public Task Closed => _closed.Task;

	private HomeSocket()
	{
	}

	/// <summary>"http://homeassistant.local:8123" → ws://homeassistant.local:8123/api/websocket (wss for https).</summary>
	public static Uri SocketUri(string url)
	{
		var builder = new UriBuilder(url.Trim());
		if (builder.Scheme is not ("http" or "https"))
		{
			throw new UriFormatException("Home Assistant address must start with http:// or https://");
		}
		builder.Scheme = builder.Scheme == "https" ? "wss" : "ws";
		builder.Path = builder.Path.TrimEnd('/') + "/api/websocket";
		return builder.Uri;
	}

	public static async Task<HomeSocket> ConnectAsync(string url, string token, CancellationToken cancellation)
	{
		var socket = new HomeSocket();
		try
		{
			using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
			timeout.CancelAfter(ConnectTimeout);
			await socket._socket.ConnectAsync(SocketUri(url), timeout.Token);
			using (var greeting = await socket.ReceiveAsync(timeout.Token))
			{
				if (TypeOf(greeting.RootElement) != "auth_required")
				{
					throw new IOException("Not a Home Assistant WebSocket endpoint");
				}
			}
			await socket.SendAsync(new JsonObject { ["type"] = "auth", ["access_token"] = token }, timeout.Token);
			using (var reply = await socket.ReceiveAsync(timeout.Token))
			{
				var type = TypeOf(reply.RootElement);
				if (type == "auth_invalid")
				{
					throw new HomeAuthException();
				}
				if (type != "auth_ok")
				{
					throw new IOException($"Unexpected authentication reply: {type}");
				}
				socket.Version = reply.RootElement.TryGetProperty("ha_version", out var version) ? version.GetString() ?? string.Empty : string.Empty;
			}
			_ = Task.Run(socket.ReceiveLoopAsync);
			return socket;
		}
		catch
		{
			await socket.DisposeAsync();
			throw;
		}
	}

	/// <summary>Sends a command and returns its result ("result" of a successful result message).</summary>
	public async Task<JsonElement> RequestAsync(JsonObject message, CancellationToken cancellation = default)
	{
		var id = Interlocked.Increment(ref _nextId);
		var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		_pending[id] = reply;
		try
		{
			message["id"] = id;
			await SendAsync(message, cancellation);
			return await reply.Task.WaitAsync(RequestTimeout, cancellation);
		}
		finally
		{
			_pending.TryRemove(id, out _);
		}
	}

	/// <summary>Subscribes (subscribe_entities, subscribe_events...): the handler gets each event's "event" element.</summary>
	public async Task SubscribeAsync(JsonObject message, Action<JsonElement> onEvent, CancellationToken cancellation = default)
	{
		var id = Interlocked.Increment(ref _nextId);
		var reply = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
		// Registered before sending: the first event may follow the result at once
		_subscriptions[id] = onEvent;
		_pending[id] = reply;
		try
		{
			message["id"] = id;
			await SendAsync(message, cancellation);
			await reply.Task.WaitAsync(RequestTimeout, cancellation);
		}
		catch
		{
			_subscriptions.TryRemove(id, out _);
			throw;
		}
		finally
		{
			_pending.TryRemove(id, out _);
		}
	}

	private static string? TypeOf(JsonElement message) =>
		message.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString() : null;

	private async Task SendAsync(JsonObject message, CancellationToken cancellation)
	{
		var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
		await _sendLock.WaitAsync(cancellation);
		try
		{
			await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellation);
		}
		finally
		{
			_sendLock.Release();
		}
	}

	private async Task<JsonDocument> ReceiveAsync(CancellationToken cancellation)
	{
		_message.SetLength(0);
		while (true)
		{
			var chunk = await _socket.ReceiveAsync(_buffer, cancellation);
			if (chunk.MessageType == WebSocketMessageType.Close)
			{
				throw new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "Home Assistant closed the connection");
			}
			_message.Write(_buffer, 0, chunk.Count);
			if (chunk.EndOfMessage)
			{
				// The document reads the stream's memory in place: every caller disposes it before the next
				// message is received (results are cloned, event handlers keep nothing)
				return JsonDocument.Parse(_message.GetBuffer().AsMemory(0, (int)_message.Length));
			}
		}
	}

	private async Task ReceiveLoopAsync()
	{
		Exception? failure = null;
		try
		{
			while (!_closing.IsCancellationRequested)
			{
				using var document = await ReceiveAsync(_closing.Token);
				Dispatch(document.RootElement);
			}
		}
		catch (Exception exception)
		{
			failure = exception;
		}
		var lost = failure as IOException ?? new IOException("Home Assistant connection lost", failure);
		foreach (var pending in _pending.Values)
		{
			pending.TrySetException(lost);
		}
		_closed.TrySetResult();
	}

	private void Dispatch(JsonElement message)
	{
		if (!message.TryGetProperty("id", out var idElement) || !idElement.TryGetInt32(out var id))
		{
			return;
		}
		switch (TypeOf(message))
		{
			case "result" when _pending.TryGetValue(id, out var reply):
				if (message.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.True)
				{
					reply.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : default);
				}
				else
				{
					var error = message.TryGetProperty("error", out var detail) && detail.TryGetProperty("message", out var text) ? text.GetString() : "request failed";
					reply.TrySetException(new InvalidOperationException($"Home Assistant: {error}"));
				}
				break;
			case "pong" when _pending.TryGetValue(id, out var pong):
				pong.TrySetResult(default);
				break;
			case "event" when _subscriptions.TryGetValue(id, out var handler) && message.TryGetProperty("event", out var payload):
				try
				{
					handler(payload);
				}
				catch (Exception exception)
				{
					Log.Error("Home Assistant event handling failed", exception);
				}
				break;
		}
	}

	public async ValueTask DisposeAsync()
	{
		_closing.Cancel();
		if (_socket.State == WebSocketState.Open)
		{
			try
			{
				using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
				await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
			}
			catch (Exception exception) when (exception is WebSocketException or OperationCanceledException)
			{
				// Gone already: nothing to close
			}
		}
		_socket.Dispose();
		_closed.TrySetResult();
	}
}
