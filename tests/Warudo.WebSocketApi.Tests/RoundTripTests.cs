using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Newtonsoft.Json.Linq;
using Warudo.Client;

namespace Warudo.WebSocketApi.Tests;

[TestClass]
public sealed class RoundTripTests
{
    private const string Token = "pair-me";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task FireEvent_ReachesTheApiWithItsArguments()
    {
        FakeApi api = new();
        await using Host host = await Host.StartAsync(api);
        await using WarudoClient client = await host.ConnectAsync(
            Token,
            TestContext.CancellationToken
        );

        int receivers = await client.BlueprintFireEventAsync(
            "wave",
            ["hello"],
            [2.5],
            TestContext.CancellationToken
        );

        Assert.AreEqual(1, receivers);
        Assert.AreEqual("wave", api.LastEvent);
        Assert.AreSequenceEqual(["hello"], api.LastTexts!);
        Assert.AreSequenceEqual([2.5], api.LastNumbers!);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SetInput_SendsGuidKeyAndJsonValue()
    {
        FakeApi api = new();
        await using Host host = await Host.StartAsync(api);
        await using WarudoClient client = await host.ConnectAsync(
            Token,
            TestContext.CancellationToken
        );
        Guid entity = Guid.NewGuid();

        await client.EntitySetInputAsync(
            entity,
            "Scale",
            System.Text.Json.Nodes.JsonValue.Create(1.5),
            TestContext.CancellationToken
        );

        Assert.AreEqual(entity, api.LastEntity);
        Assert.AreEqual("Scale", api.LastKey);
        Assert.AreEqual(1.5, api.LastValue!.Value<double>());
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Connect_WithTheWrongToken_IsRefused()
    {
        await using Host host = await Host.StartAsync(new FakeApi());

        WarudoException error = await Assert.ThrowsExactlyAsync<WarudoException>(() =>
            host.ConnectAsync("wrong", TestContext.CancellationToken)
        );

        Assert.AreEqual(RpcException.Unauthorized, error.Code);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task UnknownMethod_IsAMethodNotFoundError()
    {
        await using Host host = await Host.StartAsync(new FakeApi());
        await using WarudoClient client = await host.ConnectAsync(
            Token,
            TestContext.CancellationToken
        );

        WarudoException error = await Assert.ThrowsExactlyAsync<WarudoException>(() =>
            client.CallAsync("nope.nothing", ct: TestContext.CancellationToken)
        );

        Assert.AreEqual(RpcException.MethodNotFoundCode, error.Code);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task UnservedMethod_IsAMethodNotFoundError()
    {
        await using Host host = await Host.StartAsync(new FakeApi());
        await using WarudoClient client = await host.ConnectAsync(
            Token,
            TestContext.CancellationToken
        );

        WarudoException error = await Assert.ThrowsExactlyAsync<WarudoException>(() =>
            client.SceneSaveAsync(TestContext.CancellationToken)
        );

        Assert.AreEqual(RpcException.MethodNotFoundCode, error.Code);
    }

    [TestMethod]
    public async Task FaultingMethod_IsAnsweredAsAnInternalErrorAndReported()
    {
        Exception? reported = null;
        RpcSession session = new(
            new FakeApi(),
            Token,
            authorized: true,
            onFault: (_, ex) => reported = ex
        );

        JObject reply = JObject.Parse(
            (await session.HandleAsync("""{"jsonrpc":"2.0","id":7,"method":"scene.get"}"""))!
        );

        Assert.AreEqual(RpcException.InternalError, reply["error"]!["code"]!.Value<int>());
        Assert.IsInstanceOfType<InvalidOperationException>(reported);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SubscribedEvent_IsDelivered()
    {
        await using Host host = await Host.StartAsync(new FakeApi());
        await using WarudoClient client = await host.ConnectAsync(
            Token,
            TestContext.CancellationToken
        );
        TaskCompletionSource<WarudoEventArgs> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        client.EventReceived += (_, e) => received.TrySetResult(e);

        string[] accepted = await client.EventsSubscribeAsync(
            [WarudoEvents.BlueprintEvent, "made.up"],
            TestContext.CancellationToken
        );
        await host.PublishAsync(
            WarudoEvents.BlueprintEvent,
            new JObject { ["name"] = "hit" },
            TestContext.CancellationToken
        );

        Assert.AreSequenceEqual([WarudoEvents.BlueprintEvent], accepted);
        WarudoEventArgs e = await received.Task.WaitAsync(
            TimeSpan.FromSeconds(10),
            TestContext.CancellationToken
        );
        Assert.AreEqual("hit", e.Data.GetProperty("name").GetString());
    }

    [TestMethod]
    public async Task Batch_AnswersEachRequestAndNoNotification()
    {
        RpcSession session = new(new FakeApi(), Token, authorized: true);

        string reply = (
            await session.HandleAsync(
                """
                [
                  {"jsonrpc":"2.0","id":1,"method":"api.version"},
                  {"jsonrpc":"2.0","method":"blueprint.fireEvent","params":{"name":"x"}},
                  {"jsonrpc":"2.0","id":2,"method":"missing"}
                ]
                """
            )
        )!;

        JArray replies = JArray.Parse(reply);
        Assert.HasCount(2, replies);
        Assert.AreEqual(
            RpcException.MethodNotFoundCode,
            replies[1]["error"]!["code"]!.Value<int>()
        );
    }

    /// <summary>An IWarudoApi that records what it was asked; everything else is not found.</summary>
    private sealed class FakeApi : WarudoApiBase
    {
        public string? LastEvent { get; private set; }

        public string[]? LastTexts { get; private set; }

        public double[]? LastNumbers { get; private set; }

        public Guid LastEntity { get; private set; }

        public string? LastKey { get; private set; }

        public JToken? LastValue { get; private set; }

        public override Task EntitySetInputAsync(Guid entityId, string key, JToken value)
        {
            (LastEntity, LastKey, LastValue) = (entityId, key, value);
            return Task.CompletedTask;
        }

        public override Task<int> BlueprintFireEventAsync(
            string name,
            string[]? texts,
            double[]? numbers
        )
        {
            (LastEvent, LastTexts, LastNumbers) = (name, texts, numbers);
            return Task.FromResult(1);
        }

        public override Task<JToken> SceneGetAsync() =>
            throw new InvalidOperationException("No scene in the fake.");

        public override Task<JToken> ApiVersionAsync() =>
            Task.FromResult<JToken>(new JObject { ["protocol"] = "1" });
    }

    /// <summary>A loopback WebSocket that feeds one RpcSession, as the plugin's server does.</summary>
    private sealed class Host : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly IWarudoApi _api;
        private readonly CancellationTokenSource _stop = new();
        private readonly TaskCompletionSource<(WebSocket Socket, RpcSession Session)> _connection =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task? _serve;
        private Uri _endpoint = null!;

        private Host(IWarudoApi api) => _api = api;

        public static Task<Host> StartAsync(IWarudoApi api)
        {
            Host host = new(api);
            using TcpListener probe = new(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            host._listener.Prefixes.Add($"http://localhost:{port}/");
            host._listener.Start();
            host._endpoint = new Uri($"ws://localhost:{port}/");
            host._serve = host.ServeAsync();
            return Task.FromResult(host);
        }

        public async Task<WarudoClient> ConnectAsync(string token, CancellationToken ct)
        {
            WarudoClient client = new(
                new WarudoClientOptions { Endpoint = _endpoint, Token = token }
            );
            try
            {
                await client.ConnectAsync(ct);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            return client;
        }

        public async Task PublishAsync(string name, JToken data, CancellationToken ct)
        {
            (WebSocket socket, RpcSession session) = await _connection.Task.WaitAsync(
                TimeSpan.FromSeconds(10),
                ct
            );
            if (session.Wants(name))
            {
                await socket.SendAsync(
                    Encoding.UTF8.GetBytes(RpcSession.Notification(name, data)),
                    WebSocketMessageType.Text,
                    true,
                    ct
                );
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            _listener.Stop();
            if (_serve is not null)
            {
                try
                {
                    await _serve;
                }
                catch (Exception ex)
                    when (ex
                            is OperationCanceledException
                                or HttpListenerException
                                or WebSocketException
                                or ObjectDisposedException
                    )
                {
                    // The listener stopped under the serve loop, which is how the host ends.
                }
            }

            _listener.Close();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            HttpListenerContext context = await _listener.GetContextAsync();
            WebSocket socket = (await context.AcceptWebSocketAsync(null)).WebSocket;
            RpcSession session = new(_api, Token, authorized: false);
            _ = _connection.TrySetResult((socket, session));
            byte[] buffer = new byte[64 * 1024];
            while (!_stop.IsCancellationRequested && socket.State is WebSocketState.Open)
            {
                WebSocketReceiveResult received = await socket.ReceiveAsync(buffer, _stop.Token);
                if (received.MessageType is WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure,
                        "bye",
                        CancellationToken.None
                    );
                    return;
                }

                if (
                    await session.HandleAsync(Encoding.UTF8.GetString(buffer, 0, received.Count)) is
                    { } reply
                )
                {
                    await socket.SendAsync(
                        Encoding.UTF8.GetBytes(reply),
                        WebSocketMessageType.Text,
                        true,
                        _stop.Token
                    );
                }
            }
        }
    }
}
