using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Warudo.Client;

/// <summary>Where the Warudo WebSocket API plugin listens and how to pair with it.</summary>
public sealed class WarudoClientOptions
{
    /// <summary>The plugin's default port.</summary>
    public const int DefaultPort = 19071;

    /// <summary>The plugin's endpoint.</summary>
    public Uri Endpoint { get; set; } = new($"ws://127.0.0.1:{DefaultPort}/");

    /// <summary>The pairing token shown in the plugin's settings in Warudo.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>How long a call waits for its reply.</summary>
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The clock for request timeouts.</summary>
    public TimeProvider TimeProvider { get; set; } = TimeProvider.System;
}

/// <summary>The plugin refused a call, did not answer, or could not be reached.</summary>
public sealed class WarudoException : Exception
{
    /// <summary>An exception.</summary>
    public WarudoException() { }

    /// <summary>An exception with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public WarudoException(string message)
        : base(message) { }

    /// <summary>An exception with a message and its cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public WarudoException(string message, Exception innerException)
        : base(message, innerException) { }

    /// <summary>The JSON-RPC error code, when the plugin answered with an error.</summary>
    public int? Code { get; init; }
}

/// <summary>An event the plugin pushed.</summary>
/// <param name="name">The event's name, such as <c>blueprint.event</c>.</param>
/// <param name="data">Its data.</param>
public sealed class WarudoEventArgs(string name, JsonElement data) : EventArgs
{
    /// <summary>The event's name.</summary>
    public string Name { get; } = name;

    /// <summary>Its data.</summary>
    public JsonElement Data { get; } = data;
}

/// <summary>
/// Client for the Warudo WebSocket API plugin: JSON-RPC 2.0 over a WebSocket on localhost. Every method of the
/// protocol table is generated as a typed method on this class.
/// </summary>
public sealed partial class WarudoClient : IAsyncDisposable
{
    private readonly WarudoClientOptions _options;
    private readonly ILogger<WarudoClient> _logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveStop;
    private Task? _receiveLoop;
    private long _nextId;
    private bool _disposed;

    /// <summary>A client; call <see cref="ConnectAsync"/> to open it.</summary>
    /// <param name="options">Endpoint, token and timing.</param>
    /// <param name="logger">Where it logs.</param>
    public WarudoClient(WarudoClientOptions? options = null, ILogger<WarudoClient>? logger = null)
    {
        _options = options ?? new WarudoClientOptions();
        _logger = logger ?? NullLogger<WarudoClient>.Instance;
    }

    /// <summary>Whether the socket is open.</summary>
    public bool IsConnected => _socket is { State: WebSocketState.Open };

    /// <summary>Raised for every event the connection subscribed to.</summary>
    public event EventHandler<WarudoEventArgs>? EventReceived;

    /// <summary>Raised when the connection ends.</summary>
    public event EventHandler? Disconnected;

    /// <summary>Connects and pairs with the token.</summary>
    /// <param name="ct">Cancels the connect.</param>
    /// <exception cref="WarudoException">The plugin is not running, or the token is wrong.</exception>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsConnected)
        {
            return;
        }

        ClientWebSocket socket = new();
        try
        {
            await socket.ConnectAsync(_options.Endpoint, ct).ConfigureAwait(false);
        }
        catch (WebSocketException ex)
        {
            socket.Dispose();
            throw new WarudoException(
                $"Could not connect to the Warudo WebSocket API at {_options.Endpoint}; is the plugin installed and Warudo running?",
                ex
            );
        }

        _socket = socket;
        _receiveStop = new CancellationTokenSource();
        _receiveLoop = Task.Run(
            () => ReceiveAsync(socket, _receiveStop.Token),
            CancellationToken.None
        );
        _ = await CallAsync("auth.pair", new JsonObject { ["token"] = _options.Token }, ct)
            .ConfigureAwait(false);
        LogConnected(_logger, _options.Endpoint);
    }

    /// <summary>Closes the connection.</summary>
    /// <param name="ct">Cancels the close.</param>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        ClientWebSocket? socket = Interlocked.Exchange(ref _socket, null);
        if (socket is null)
        {
            return;
        }

        if (socket.State is WebSocketState.Open)
        {
            // Only our half of the close (RFC 6455 section 7.1.1): a peer that never answers must not hold us.
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(
                ct
            );
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                await socket
                    .CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closing", timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
            {
                LogCloseFailed(_logger, ex);
            }
        }

        if (_receiveStop is not null)
        {
            await _receiveStop.CancelAsync().ConfigureAwait(false);
        }

        if (_receiveLoop is not null)
        {
            await _receiveLoop.ConfigureAwait(false);
        }

        _receiveStop?.Dispose();
        _receiveStop = null;
        _receiveLoop = null;
        socket.Dispose();
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DisconnectAsync().ConfigureAwait(false);
        _sendLock.Dispose();
    }

    /// <summary>Calls any method, including ones newer than this client.</summary>
    /// <param name="method">The JSON-RPC method.</param>
    /// <param name="parameters">Its named parameters, or null.</param>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>The result.</returns>
    /// <exception cref="WarudoException">The plugin answered with an error or did not answer in time.</exception>
    public async Task<JsonElement> CallAsync(
        string method,
        JsonObject? parameters = null,
        CancellationToken ct = default
    )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(_disposed, this);
        ClientWebSocket socket = _socket is { State: WebSocketState.Open } open
            ? open
            : throw new WarudoException("The Warudo client is not connected.");

        long id = Interlocked.Increment(ref _nextId);
        JsonObject request = new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["method"] = method,
        };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        byte[] frame = JsonSerializer.SerializeToUtf8Bytes(
            request,
            WarudoJsonContext.Default.JsonObject
        );
        TaskCompletionSource<JsonElement> reply = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        _pending[id] = reply;
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket
                    .SendAsync(frame, WebSocketMessageType.Text, endOfMessage: true, ct)
                    .ConfigureAwait(false);
            }
            finally
            {
                _ = _sendLock.Release();
            }

            JsonElement answer;
            try
            {
                answer = await reply
                    .Task.WaitAsync(_options.RequestTimeout, _options.TimeProvider, ct)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new WarudoException(
                    $"Warudo did not answer '{method}' within {_options.RequestTimeout.TotalSeconds:0.#} s.",
                    ex
                );
            }

            if (answer.TryGetProperty("error", out JsonElement error))
            {
                throw new WarudoException(
                    $"Warudo refused '{method}': {RpcValues.Text(error, "message")}"
                )
                {
                    Code =
                        error.TryGetProperty("code", out JsonElement code)
                        && code.ValueKind is JsonValueKind.Number
                            ? code.GetInt32()
                            : null,
                };
            }

            return answer.TryGetProperty("result", out JsonElement result) ? result : default;
        }
        finally
        {
            _ = _pending.TryRemove(id, out _);
        }
    }

    private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
    {
        byte[] buffer = new byte[16 * 1024];
        using MemoryStream message = new();
        try
        {
            while (!ct.IsCancellationRequested && socket.State is WebSocketState.Open)
            {
                ValueWebSocketReceiveResult received = await socket
                    .ReceiveAsync(buffer.AsMemory(), ct)
                    .ConfigureAwait(false);
                if (received.MessageType is WebSocketMessageType.Close)
                {
                    break;
                }

                message.Write(buffer, 0, received.Count);
                if (received.EndOfMessage)
                {
                    Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
                    message.SetLength(0);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Deliberately not logged: DisconnectAsync stopped the loop.
        }
        catch (WebSocketException ex)
        {
            LogConnectionLost(_logger, ex);
        }
        finally
        {
            foreach (TaskCompletionSource<JsonElement> waiting in _pending.Values)
            {
                _ = waiting.TrySetException(
                    new WarudoException("The connection to Warudo closed before it answered.")
                );
            }

            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Dispatch(ReadOnlyMemory<byte> utf8)
    {
        JsonElement root;
        try
        {
            using JsonDocument document = JsonDocument.Parse(utf8);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            LogUnreadable(_logger, ex);
            return;
        }

        IEnumerable<JsonElement> items =
            root.ValueKind is JsonValueKind.Array ? root.EnumerateArray() : [root];
        foreach (JsonElement item in items)
        {
            if (
                item.TryGetProperty("id", out JsonElement id)
                && id.ValueKind is JsonValueKind.Number
                && _pending.TryGetValue(
                    id.GetInt64(),
                    out TaskCompletionSource<JsonElement>? waiting
                )
            )
            {
                _ = waiting.TrySetResult(item);
            }
            else if (
                RpcValues.Text(item, "method") == "event"
                && item.TryGetProperty("params", out JsonElement parameters)
            )
            {
                EventReceived?.Invoke(
                    this,
                    new WarudoEventArgs(
                        RpcValues.Text(parameters, "name"),
                        parameters.TryGetProperty("data", out JsonElement data) ? data : default
                    )
                );
            }
        }
    }

    [LoggerMessage(
        EventId = 1,
        Level = LogLevel.Information,
        Message = "Connected to the Warudo WebSocket API at {Endpoint}."
    )]
    private static partial void LogConnected(ILogger logger, Uri endpoint);

    [LoggerMessage(
        EventId = 2,
        Level = LogLevel.Warning,
        Message = "The connection to Warudo was lost."
    )]
    private static partial void LogConnectionLost(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3,
        Level = LogLevel.Warning,
        Message = "Warudo sent a frame that is not JSON; it was ignored."
    )]
    private static partial void LogUnreadable(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 4,
        Level = LogLevel.Debug,
        Message = "Closing the Warudo connection failed; it is dropped."
    )]
    private static partial void LogCloseFailed(ILogger logger, Exception exception);
}

/// <summary>Value conversions the generated methods use; AOT-safe, no reflection.</summary>
internal static class RpcValues
{
    public static JsonNode ToNode(string value) => JsonValue.Create(value);

    public static JsonNode ToNode(int value) => JsonValue.Create(value);

    public static JsonNode ToNode(double value) => JsonValue.Create(value);

    public static JsonNode ToNode(bool value) => JsonValue.Create(value);

    public static JsonNode ToNode(Guid value) => JsonValue.Create(value.ToString("D"));

    public static JsonNode? ToNode(JsonNode? value) => value?.DeepClone();

    public static JsonNode ToNode(IReadOnlyList<string> values) =>
        new JsonArray([.. values.Select(static v => (JsonNode)JsonValue.Create(v))]);

    public static JsonNode ToNode(IReadOnlyList<double> values) =>
        new JsonArray([.. values.Select(static v => (JsonNode)JsonValue.Create(v))]);

    public static JsonNode ToNode(IReadOnlyList<int> values) =>
        new JsonArray([.. values.Select(static v => (JsonNode)JsonValue.Create(v))]);

    public static JsonElement Element(JsonElement value) => value;

    public static string String(JsonElement value) =>
        value.ValueKind is JsonValueKind.String ? value.GetString()! : value.ToString();

    public static int Int(JsonElement value) => value.GetInt32();

    public static double Double(JsonElement value) => value.GetDouble();

    public static bool Bool(JsonElement value) => value.GetBoolean();

    public static Guid Guid(JsonElement value) =>
        System.Guid.Parse(value.GetString()!, CultureInfo.InvariantCulture);

    public static string[] Strings(JsonElement value) =>
        value.ValueKind is JsonValueKind.Array ? [.. value.EnumerateArray().Select(String)] : [];

    public static double[] Doubles(JsonElement value) =>
        value.ValueKind is JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(static v => v.GetDouble())]
            : [];

    public static int[] Ints(JsonElement value) =>
        value.ValueKind is JsonValueKind.Array
            ? [.. value.EnumerateArray().Select(static v => v.GetInt32())]
            : [];

    public static string Text(JsonElement value, string property) =>
        value.ValueKind is JsonValueKind.Object
        && value.TryGetProperty(property, out JsonElement text)
        && text.ValueKind is JsonValueKind.String
            ? text.GetString()!
            : string.Empty;
}

/// <summary>Source-generated JSON metadata.</summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class WarudoJsonContext
    : System.Text.Json.Serialization.JsonSerializerContext;
