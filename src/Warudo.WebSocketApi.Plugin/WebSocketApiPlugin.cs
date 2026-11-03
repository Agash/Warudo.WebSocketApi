using System;
using System.Collections.Generic;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Attributes;
using Warudo.Core.Plugins;
using Warudo.Core.Utils;
using Warudo.Plugins.Core;
using WebSocketSharp;
using WebSocketSharp.Server;

namespace Warudo.WebSocketApi;

/// <summary>
/// Serves the Warudo WebSocket API (JSON-RPC 2.0) on localhost so other applications can read and drive the
/// opened scene. Clients pair with the token shown in this plugin's settings.
/// </summary>
[PluginType(
    Id = "Agash.WebSocketApi",
    Name = "WebSocket API",
    Description = "JSON-RPC 2.0 API over WebSocket for automation and streaming tools.",
    Author = "Agash",
    Version = WebSocketApiPlugin.PluginVersion,
    NodeTypes = new[] { typeof(OnWebSocketApiEventNode), typeof(SendWebSocketApiEventNode) }
)]
public sealed class WebSocketApiPlugin : Plugin
{
    /// <summary>This plugin's version.</summary>
    public const string PluginVersion = "0.1.0";

    /// <summary>The port the API listens on (localhost only).</summary>
    [DataInput]
    [Label("Port")]
    public int Port = 19071;

    /// <summary>The token clients present to pair; keep it private.</summary>
    [DataInput]
    [Label("Pairing token")]
    public string Token = string.Empty;

    private WebSocketServer? _server;
    private WarudoApi? _api;
    private readonly List<ApiBehavior> _connections = new();

    internal static WebSocketApiPlugin? Instance { get; private set; }

    /// <summary>Makes a new pairing token, which disconnects every paired client.</summary>
    [Trigger]
    [Label("New pairing token")]
    public void RegenerateToken()
    {
        SetDataInput(nameof(Token), NewToken(), broadcast: true);
        Restart();
    }

    /// <inheritdoc/>
    protected override void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        if (string.IsNullOrEmpty(Token))
        {
            SetDataInput(nameof(Token), NewToken(), broadcast: true);
        }

        _api = new WarudoApi(this);
        Watch(nameof(Port), Restart);
        Start();
    }

    /// <inheritdoc/>
    protected override void OnDestroy()
    {
        Stop();
        Instance = null;
        base.OnDestroy();
    }

    /// <inheritdoc/>
    public override void OnSceneLoaded(
        Warudo.Core.Scenes.Scene scene,
        Warudo.Core.Serializations.SerializedScene serializedScene
    )
    {
        base.OnSceneLoaded(scene, serializedScene);
        Publish(WarudoEvents.SceneChanged, new JObject { ["name"] = scene.Name });
    }

    /// <inheritdoc/>
    public override void OnSceneUnloaded(Warudo.Core.Scenes.Scene scene)
    {
        _api?.ClearWatches();
        base.OnSceneUnloaded(scene);
    }

    /// <summary>Sends an event to every connection subscribed to it.</summary>
    /// <param name="name">The event.</param>
    /// <param name="data">Its data.</param>
    internal void Publish(string name, JToken data)
    {
        string frame = RpcSession.Notification(name, data);
        lock (_connections)
        {
            foreach (ApiBehavior connection in _connections)
            {
                connection.SendIfWanted(name, frame);
            }
        }
    }

    internal void Attach(ApiBehavior connection)
    {
        lock (_connections)
        {
            _connections.Add(connection);
        }
    }

    internal void Detach(ApiBehavior connection)
    {
        lock (_connections)
        {
            _connections.Remove(connection);
        }
    }

    private async void Start()
    {
        try
        {
            CorePlugin core = Context.PluginManager.GetPlugin<CorePlugin>();
            await core.BeforeListenToPort();
            _server = new WebSocketServer(WebSocketHelpers.CreateLocalHostUri(Port));
            _server.AddWebSocketService<ApiBehavior>(
                "/",
                behavior => behavior.Initialize(this, _api!, Token)
            );
            _server.Start();
            core.AfterListenToPort();
            Debug.Log($"[WebSocketApi] Listening on ws://127.0.0.1:{Port}/");
        }
        catch (Exception ex)
        {
            // async void is required for Warudo lifecycle callbacks, so nothing may escape it.
            Log.UserError($"[WebSocketApi] Could not listen on port {Port}", ex);
        }
    }

    private void Stop()
    {
        WebSocketServer? server = _server;
        _server = null;
        if (server is { IsListening: true })
        {
            server.Stop();
        }
    }

    private void Restart()
    {
        Stop();
        Start();
    }

    private static string NewToken()
    {
        byte[] bytes = Guid.NewGuid().ToByteArray();
        byte[] more = Guid.NewGuid().ToByteArray();
        return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant()
            + BitConverter.ToString(more).Replace("-", string.Empty).ToLowerInvariant();
    }
}

/// <summary>One client connection: frames run on Warudo's main thread through an <see cref="RpcSession"/>.</summary>
internal sealed class ApiBehavior : WebSocketBehavior
{
    private WebSocketApiPlugin? _plugin;
    private RpcSession? _session;

    public void Initialize(WebSocketApiPlugin plugin, IWarudoApi api, string token)
    {
        _plugin = plugin;
        // A token in the connect URL (?token=) pairs at once; otherwise the client calls auth.pair.
        string? presented = Context?.QueryString?["token"];
        _session = new RpcSession(
            api,
            token,
            authorized: !string.IsNullOrEmpty(presented) && presented == token,
            onFault: static (method, ex) => Debug.LogError($"[WebSocketApi] {method} failed: {ex}")
        );
    }

    public void SendIfWanted(string name, string frame)
    {
        if (
            _session is not null
            && _session.IsAuthorized
            && _session.Wants(name)
            && Context.WebSocket.ReadyState == WebSocketState.Open
        )
        {
            SendAsync(frame, null);
        }
    }

    protected override void OnOpen() => _plugin?.Attach(this);

    protected override void OnClose(CloseEventArgs e) => _plugin?.Detach(this);

    protected override void OnMessage(MessageEventArgs e)
    {
        string frame = e.Data;
        Handle(frame).Forget();
    }

    private async UniTaskVoid Handle(string frame)
    {
        await UniTask.SwitchToMainThread();
        try
        {
            string? reply = await _session!.HandleAsync(frame);
            if (reply is not null && Context.WebSocket.ReadyState == WebSocketState.Open)
            {
                SendAsync(reply, null);
            }
        }
        catch (Exception ex)
        {
            // RpcSession answers errors itself; this is a fault in the plugin, logged where users look.
            Debug.LogException(ex);
        }
    }
}
