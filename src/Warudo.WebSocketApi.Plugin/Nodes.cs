using Newtonsoft.Json.Linq;
using Warudo.Core.Attributes;
using Warudo.Core.Graphs;

namespace Warudo.WebSocketApi;

/// <summary>Fires when a client calls <c>blueprint.fireEvent</c> with this node's event name.</summary>
[NodeType(
    Id = "5b8f6f3a-2d1e-4c7b-9a54-6e1f0c2d3a71",
    Title = "On WebSocket API Event",
    Category = "WebSocket API"
)]
public sealed class OnWebSocketApiEventNode : Node
{
    /// <summary>The event name clients fire.</summary>
    [DataInput]
    [Label("Event name")]
    public string EventName = "MyEvent";

    /// <summary>Continues when the event arrives.</summary>
    [FlowOutput]
    public Continuation? Exit;

    private string[] _texts = System.Array.Empty<string>();
    private double[] _numbers = System.Array.Empty<double>();

    /// <summary>The first text argument.</summary>
    /// <returns>It, or empty.</returns>
    [DataOutput]
    [Label("Text 1")]
    public string Text1() => _texts.Length > 0 ? _texts[0] : string.Empty;

    /// <summary>The second text argument.</summary>
    /// <returns>It, or empty.</returns>
    [DataOutput]
    [Label("Text 2")]
    public string Text2() => _texts.Length > 1 ? _texts[1] : string.Empty;

    /// <summary>The third text argument.</summary>
    /// <returns>It, or empty.</returns>
    [DataOutput]
    [Label("Text 3")]
    public string Text3() => _texts.Length > 2 ? _texts[2] : string.Empty;

    /// <summary>The first number argument.</summary>
    /// <returns>It, or zero.</returns>
    [DataOutput]
    [Label("Number 1")]
    public float Number1() => _numbers.Length > 0 ? (float)_numbers[0] : 0f;

    /// <summary>The second number argument.</summary>
    /// <returns>It, or zero.</returns>
    [DataOutput]
    [Label("Number 2")]
    public float Number2() => _numbers.Length > 1 ? (float)_numbers[1] : 0f;

    /// <summary>The third number argument.</summary>
    /// <returns>It, or zero.</returns>
    [DataOutput]
    [Label("Number 3")]
    public float Number3() => _numbers.Length > 2 ? (float)_numbers[2] : 0f;

    internal void Fire(string[] texts, double[] numbers)
    {
        _texts = texts;
        _numbers = numbers;
        InvokeFlow(nameof(Exit));
    }
}

/// <summary>Sends a <c>blueprint.event</c> notification to every client subscribed to it.</summary>
[NodeType(
    Id = "c41e9d27-8b35-4f60-a2d8-1f7b5e9c0a46",
    Title = "Send WebSocket API Event",
    Category = "WebSocket API"
)]
public sealed class SendWebSocketApiEventNode : Node
{
    /// <summary>The event name.</summary>
    [DataInput]
    [Label("Event name")]
    public string EventName = "MyEvent";

    /// <summary>Text sent with it.</summary>
    [DataInput]
    [Label("Text")]
    public string Text = string.Empty;

    /// <summary>A number sent with it.</summary>
    [DataInput]
    [Label("Number")]
    public float Number;

    /// <summary>Continues after sending.</summary>
    [FlowOutput]
    public Continuation? Exit;

    /// <summary>Sends the event.</summary>
    /// <returns>Where the flow continues.</returns>
    [FlowInput]
    public Continuation? Enter()
    {
        WebSocketApiPlugin.Instance?.Publish(
            WarudoEvents.BlueprintEvent,
            new JObject
            {
                ["name"] = EventName,
                ["text"] = Text,
                ["number"] = Number,
            }
        );
        return Exit;
    }
}
