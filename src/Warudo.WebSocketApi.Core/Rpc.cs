using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Warudo.WebSocketApi;

/// <summary>A JSON-RPC 2.0 error (https://www.jsonrpc.org/specification, section 5.1).</summary>
public sealed class RpcException : Exception
{
    /// <summary>An error with a code and message.</summary>
    /// <param name="code">The JSON-RPC error code.</param>
    /// <param name="message">What went wrong.</param>
    public RpcException(int code, string message)
        : base(message) => Code = code;

    /// <summary>An error.</summary>
    public RpcException()
        : this(InternalError, "Internal error") { }

    /// <summary>An error with a message.</summary>
    /// <param name="message">What went wrong.</param>
    public RpcException(string message)
        : this(InternalError, message) { }

    /// <summary>An error with a message and its cause.</summary>
    /// <param name="message">What went wrong.</param>
    /// <param name="innerException">The cause.</param>
    public RpcException(string message, Exception innerException)
        : base(message, innerException) => Code = InternalError;

    /// <summary>The request is not valid JSON.</summary>
    public const int ParseError = -32700;

    /// <summary>The JSON is not a valid request.</summary>
    public const int InvalidRequest = -32600;

    /// <summary>No such method.</summary>
    public const int MethodNotFoundCode = -32601;

    /// <summary>A parameter is missing or of the wrong type.</summary>
    public const int InvalidParams = -32602;

    /// <summary>The method failed.</summary>
    public const int InternalError = -32603;

    /// <summary>The entity, node type or blueprint named does not exist (an application error code).</summary>
    public const int NotFound = -32001;

    /// <summary>The connection has not presented the pairing token (an application error code).</summary>
    public const int Unauthorized = -32002;

    /// <summary>The JSON-RPC error code.</summary>
    public int Code { get; }

    /// <summary>The method is not served.</summary>
    /// <param name="method">The method asked for.</param>
    /// <returns>The error.</returns>
    public static RpcException MethodNotFound(string method) =>
        new(MethodNotFoundCode, $"Method not found: {method}");
}

/// <summary>Reads named parameters and shapes results, for the generated dispatcher.</summary>
public static class RpcParameters
{
    // A JSON type name for error messages. Warudo's mod sandbox blocks System.Reflection, which Type.Name is part of.
    private static string Label<T>() =>
        typeof(T) == typeof(string) ? "string"
        : typeof(T) == typeof(int) ? "integer"
        : typeof(T) == typeof(double) ? "number"
        : typeof(T) == typeof(bool) ? "boolean"
        : typeof(T) == typeof(Guid) ? "GUID string"
        : typeof(T).IsArray ? "array"
        : "JSON value";

    /// <summary>A required parameter.</summary>
    /// <typeparam name="T">Its type.</typeparam>
    /// <param name="parameters">The request's parameters.</param>
    /// <param name="name">The parameter.</param>
    /// <returns>Its value.</returns>
    public static T Required<T>(JObject? parameters, string name)
    {
        JToken? token = parameters?[name];
        if (token is null || token.Type is JTokenType.Null)
        {
            throw new RpcException(RpcException.InvalidParams, $"Missing parameter '{name}'.");
        }

        return Convert<T>(token, name);
    }

    /// <summary>An optional reference-type parameter.</summary>
    /// <typeparam name="T">Its type.</typeparam>
    /// <param name="parameters">The request's parameters.</param>
    /// <param name="name">The parameter.</param>
    /// <returns>Its value, or null.</returns>
    public static T? Optional<T>(JObject? parameters, string name)
        where T : class
    {
        JToken? token = parameters?[name];
        return token is null || token.Type is JTokenType.Null ? null : Convert<T>(token, name);
    }

    /// <summary>An optional value-type parameter.</summary>
    /// <typeparam name="T">Its type.</typeparam>
    /// <param name="parameters">The request's parameters.</param>
    /// <param name="name">The parameter.</param>
    /// <returns>Its value, or null.</returns>
    public static T? OptionalValue<T>(JObject? parameters, string name)
        where T : struct
    {
        JToken? token = parameters?[name];
        return token is null || token.Type is JTokenType.Null ? null : Convert<T>(token, name);
    }

    /// <summary>A result as JSON.</summary>
    /// <param name="value">The result.</param>
    /// <returns>Its JSON.</returns>
    public static JToken ToToken(object? value) =>
        value switch
        {
            null => JValue.CreateNull(),
            JToken token => token,
            Guid id => new JValue(id.ToString()),
            _ => JToken.FromObject(value),
        };

    private static T Convert<T>(JToken token, string name)
    {
        try
        {
            if (typeof(T) == typeof(JToken))
            {
                return (T)(object)token;
            }

            return token.ToObject<T>()
                ?? throw new RpcException(
                    RpcException.InvalidParams,
                    $"Parameter '{name}' is null."
                );
        }
        catch (Exception ex)
            when (ex
                    is JsonException
                        or FormatException
                        or InvalidCastException
                        or ArgumentException
            )
        {
            throw new RpcException(
                RpcException.InvalidParams,
                $"Parameter '{name}' is not a {Label<T>()}: {ex.Message}"
            );
        }
    }
}

/// <summary>
/// One client connection's JSON-RPC session: it checks the pairing token, routes requests through the generated
/// dispatcher, answers in order, and keeps the events the client subscribed to. Transport-free, so it is tested
/// without Warudo; the plugin feeds it frames from its WebSocket.
/// </summary>
public sealed class RpcSession
{
    private readonly IWarudoApi _api;
    private readonly string _token;
    private readonly HashSet<string> _events = new(StringComparer.Ordinal);
    private readonly Action<string, Exception>? _onFault;

    /// <summary>A session.</summary>
    /// <param name="api">What it serves.</param>
    /// <param name="token">The pairing token the client must present (<c>auth.pair</c>, or the connect query).</param>
    /// <param name="authorized">Whether the transport already checked the token.</param>
    /// <param name="onFault">Told about a method that failed with anything but an <see cref="RpcException"/>, so the host can log it.</param>
    public RpcSession(
        IWarudoApi api,
        string token,
        bool authorized,
        Action<string, Exception>? onFault = null
    )
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _token = token ?? throw new ArgumentNullException(nameof(token));
        IsAuthorized = authorized;
        _onFault = onFault;
    }

    /// <summary>Whether the client presented the pairing token.</summary>
    public bool IsAuthorized { get; private set; }

    /// <summary>Whether this client wants an event.</summary>
    /// <param name="name">The event name.</param>
    /// <returns>Whether it subscribed.</returns>
    public bool Wants(string name)
    {
        lock (_events)
        {
            return _events.Contains(name);
        }
    }

    /// <summary>Handles one frame: a request, a notification, or a batch.</summary>
    /// <param name="frame">The text frame.</param>
    /// <returns>The reply to send, or null when there is none (notifications only).</returns>
    public async Task<string?> HandleAsync(string frame)
    {
        JToken parsed;
        try
        {
            parsed = JToken.Parse(frame);
        }
        catch (JsonException)
        {
            return Error(null, RpcException.ParseError, "Parse error").ToString(Formatting.None);
        }

        if (parsed is JArray batch)
        {
            if (batch.Count == 0)
            {
                return Error(null, RpcException.InvalidRequest, "Empty batch")
                    .ToString(Formatting.None);
            }

            List<JObject> replies = [];
            foreach (JToken item in batch)
            {
                if (await HandleOneAsync(item).ConfigureAwait(false) is { } reply)
                {
                    replies.Add(reply);
                }
            }

            return replies.Count == 0 ? null : new JArray(replies).ToString(Formatting.None);
        }

        return (await HandleOneAsync(parsed).ConfigureAwait(false))?.ToString(Formatting.None);
    }

    /// <summary>An event notification as a frame.</summary>
    /// <param name="name">The event.</param>
    /// <param name="data">Its data.</param>
    /// <returns>The frame.</returns>
    public static string Notification(string name, JToken data) =>
        new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "event",
            ["params"] = new JObject { ["name"] = name, ["data"] = data },
        }.ToString(Formatting.None);

    private async Task<JObject?> HandleOneAsync(JToken item)
    {
        if (
            item is not JObject request
            || request["jsonrpc"]?.ToString() != "2.0"
            || request["method"]?.Type is not JTokenType.String
        )
        {
            return Error(null, RpcException.InvalidRequest, "Invalid request");
        }

        JToken? id = request["id"];
        bool notification = id is null;
        string method = request["method"]!.ToString();
        JObject? parameters = request["params"] as JObject;
        try
        {
            JToken? result = await RouteAsync(method, parameters).ConfigureAwait(false);
            return notification
                ? null
                : new JObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id,
                    ["result"] = result ?? JValue.CreateNull(),
                };
        }
        catch (RpcException ex)
        {
            return notification ? null : Error(id, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            // A fault in the implementation still gets an answer (JSON-RPC 2.0 section 5.1), so a client never waits.
            _onFault?.Invoke(method, ex);
            return notification
                ? null
                : Error(id, RpcException.InternalError, $"{method} failed: {ex.Message}");
        }
    }

    private async Task<JToken?> RouteAsync(string method, JObject? parameters)
    {
        if (method == "auth.pair")
        {
            IsAuthorized = string.Equals(
                RpcParameters.Required<string>(parameters, "token"),
                _token,
                StringComparison.Ordinal
            );
            return IsAuthorized
                ? new JValue(true)
                : throw new RpcException(RpcException.Unauthorized, "Wrong pairing token.");
        }

        if (!IsAuthorized)
        {
            throw new RpcException(
                RpcException.Unauthorized,
                "Pair first: call auth.pair with the token shown in the plugin's settings."
            );
        }

        switch (method)
        {
            case "events.subscribe":
                string[] wanted = RpcParameters.Required<string[]>(parameters, "events");
                string[] accepted = wanted
                    .Where(static e => WarudoEvents.All.Contains(e, StringComparer.Ordinal))
                    .ToArray();
                lock (_events)
                {
                    _events.UnionWith(accepted);
                }

                return new JArray(accepted);
            case "events.unsubscribe":
                lock (_events)
                {
                    _events.ExceptWith(RpcParameters.Required<string[]>(parameters, "events"));
                }

                return null;
            default:
                return await WarudoApiDispatcher
                    .DispatchAsync(_api, method, parameters)
                    .ConfigureAwait(false);
        }
    }

    private static JObject Error(JToken? id, int code, string message) =>
        new()
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message },
        };
}

/// <summary>The event names the plugin notifies subscribers of.</summary>
public static class WarudoEvents
{
    /// <summary>Assets or blueprints were added, removed or renamed, or another scene opened.</summary>
    public const string SceneChanged = "scene.changed";

    /// <summary>A data input changed: <c>{ entityId, key, value }</c>.</summary>
    public const string InputChanged = "entity.inputChanged";

    /// <summary>A Send WebSocket API Event node fired: <c>{ name, text, number }</c>.</summary>
    public const string BlueprintEvent = "blueprint.event";

    /// <summary>All of them.</summary>
    public static readonly string[] All = [SceneChanged, InputChanged, BlueprintEvent];
}
