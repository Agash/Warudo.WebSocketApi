using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Warudo.Client;

/// <summary>An asset, node or plugin in the opened scene, addressed by its id.</summary>
/// <param name="client">The connection it is reached through.</param>
/// <param name="id">Its id.</param>
public abstract class WarudoEntity(WarudoClient client, Guid id)
{
    /// <summary>The connection it is reached through.</summary>
    public WarudoClient Client { get; } = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>Its id.</summary>
    public Guid Id { get; } = id;

    /// <summary>Its type, data inputs with values, and triggers.</summary>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>The entity as the plugin describes it.</returns>
    public Task<JsonElement> DescribeAsync(CancellationToken ct = default) =>
        Client.EntityGetAsync(Id, ct);

    /// <summary>Sends <c>entity.inputChanged</c> when one of these data inputs changes (all when omitted).</summary>
    /// <param name="keys">The data inputs.</param>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>When the watch is in place.</returns>
    public Task WatchAsync(IReadOnlyList<string>? keys = null, CancellationToken ct = default) =>
        Client.EntityWatchAsync(Id, keys, ct);

    /// <summary>A data input's value in Warudo's serialized form.</summary>
    /// <param name="key">The data input.</param>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>Its value.</returns>
    protected Task<JsonElement> GetInputAsync(string key, CancellationToken ct) =>
        Client.EntityGetInputAsync(Id, key, ct);

    /// <summary>Sets a data input.</summary>
    /// <param name="key">The data input.</param>
    /// <param name="value">Its value in Warudo's serialized form.</param>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>When it is set.</returns>
    protected Task SetInputAsync(string key, JsonNode? value, CancellationToken ct) =>
        Client.EntitySetInputAsync(Id, key, value ?? JsonValue.Create((string?)null)!, ct);

    /// <summary>Invokes a trigger.</summary>
    /// <param name="key">The trigger.</param>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>When it ran.</returns>
    protected Task InvokeTriggerAsync(string key, CancellationToken ct) =>
        Client.EntityInvokeTriggerAsync(Id, key, ct);
}

/// <summary>An asset in the opened scene. Generated subclasses add a typed accessor per data input and trigger.</summary>
/// <param name="client">The connection it is reached through.</param>
/// <param name="id">Its id.</param>
public abstract class WarudoAsset(WarudoClient client, Guid id) : WarudoEntity(client, id)
{
    /// <summary>Renames it.</summary>
    /// <param name="name">The new name.</param>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>When it is renamed.</returns>
    public Task RenameAsync(string name, CancellationToken ct = default) =>
        Client.AssetRenameAsync(Id, name, ct);

    /// <summary>Removes it from the scene.</summary>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>When it is removed.</returns>
    public Task RemoveAsync(CancellationToken ct = default) => Client.AssetRemoveAsync(Id, ct);

    /// <summary>It in Warudo's serialized form.</summary>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>The export.</returns>
    public Task<JsonElement> ExportAsync(CancellationToken ct = default) =>
        Client.AssetExportAsync(Id, ct);
}

/// <summary>
/// A node type with its inputs. Set the inputs, then run it once or add it to a blueprint. Generated subclasses
/// add a typed property per data input and a typed result per data output.
/// </summary>
public abstract class WarudoNode
{
    /// <summary>The node type's id.</summary>
    public abstract string TypeId { get; }

    /// <summary>Adds this node to a blueprint with the inputs set here.</summary>
    /// <param name="client">The connection.</param>
    /// <param name="blueprintId">The blueprint.</param>
    /// <param name="x">Its position in the blueprint.</param>
    /// <param name="y">Its position in the blueprint.</param>
    /// <param name="ct">Cancels the wait for the replies.</param>
    /// <returns>The new node's id.</returns>
    public async Task<Guid> AddToBlueprintAsync(
        WarudoClient client,
        Guid blueprintId,
        double? x = null,
        double? y = null,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        Guid node = await client
            .BlueprintAddNodeAsync(blueprintId, TypeId, x, y, ct)
            .ConfigureAwait(false);
        foreach (KeyValuePair<string, JsonNode?> input in Inputs())
        {
            await client
                .EntitySetInputAsync(node, input.Key, input.Value!, ct)
                .ConfigureAwait(false);
        }

        return node;
    }

    /// <summary>Runs this node type once with the inputs set here.</summary>
    /// <param name="client">The connection.</param>
    /// <param name="flowInput">The flow input to run; data-only nodes have none.</param>
    /// <param name="ct">Cancels the wait for the reply.</param>
    /// <returns>Its data outputs.</returns>
    protected Task<JsonElement> RunCoreAsync(
        WarudoClient client,
        string? flowInput,
        CancellationToken ct
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.NodeRunAsync(TypeId, Inputs(), flowInput, ct);
    }

    /// <summary>Writes the inputs that were set.</summary>
    /// <param name="inputs">Where they go, by data input key.</param>
    protected abstract void WriteInputs(JsonObject inputs);

    private JsonObject Inputs()
    {
        JsonObject inputs = [];
        WriteInputs(inputs);
        return inputs;
    }
}

/// <summary>Reads Warudo's serialized data input values; the generated accessors use it.</summary>
public static class WarudoValue
{
    /// <summary>A boolean.</summary>
    /// <param name="value">The serialized value.</param>
    /// <returns>It.</returns>
    public static bool ToBoolean(JsonElement value) =>
        value.ValueKind is JsonValueKind.True
        || (value.ValueKind is JsonValueKind.String && bool.Parse(value.GetString()!));

    /// <summary>An integer.</summary>
    /// <param name="value">The serialized value.</param>
    /// <returns>It.</returns>
    public static int ToInt32(JsonElement value) =>
        value.ValueKind is JsonValueKind.String
            ? int.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : value.GetInt32();

    /// <summary>A number.</summary>
    /// <param name="value">The serialized value.</param>
    /// <returns>It.</returns>
    public static double ToDouble(JsonElement value) =>
        value.ValueKind is JsonValueKind.String
            ? double.Parse(value.GetString()!, CultureInfo.InvariantCulture)
            : value.GetDouble();

    /// <summary>A string.</summary>
    /// <param name="value">The serialized value.</param>
    /// <returns>It, or null.</returns>
    public static string? ToText(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };

    /// <summary>An enum's number: Warudo sends an object with a label and a value, or the number alone.</summary>
    /// <param name="value">The serialized value.</param>
    /// <returns>Its number.</returns>
    public static long ToEnumValue(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Object => value.GetProperty("value").GetInt64(),
            JsonValueKind.String => long.Parse(value.GetString()!, CultureInfo.InvariantCulture),
            _ => value.GetInt64(),
        };

    /// <summary>The id of a referenced asset: Warudo sends an object with its id and name, or nothing.</summary>
    /// <param name="value">The serialized value.</param>
    /// <returns>The id, or null when none is set.</returns>
    public static Guid? ToAssetId(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Object
                when value.TryGetProperty("id", out JsonElement id)
                    && id.ValueKind is JsonValueKind.String => System.Guid.Parse(
                id.GetString()!,
                CultureInfo.InvariantCulture
            ),
            JsonValueKind.String
                when System.Guid.TryParse(
                    value.GetString(),
                    CultureInfo.InvariantCulture,
                    out Guid id
                ) => id,
            _ => null,
        };

    /// <summary>An enum value, as a data input takes it: Warudo's serialized enum object.</summary>
    /// <param name="value">The enum's number.</param>
    /// <returns>The serialized value.</returns>
    public static JsonNode EnumReference(long value) => new JsonObject { ["value"] = value };

    /// <summary>A reference to an asset, as a data input takes it.</summary>
    /// <param name="id">The asset's id, or null for none.</param>
    /// <returns>The serialized reference: Warudo's asset identifier object, or null.</returns>
    public static JsonNode? AssetReference(Guid? id) =>
        id is { } value ? new JsonObject { ["id"] = value.ToString("D") } : null;

    /// <summary>A data output read from a node run's result.</summary>
    /// <param name="outputs">The outputs.</param>
    /// <param name="key">The data output.</param>
    /// <returns>Its value, or an undefined element when the node did not produce it.</returns>
    public static JsonElement Output(JsonElement outputs, string key) =>
        outputs.ValueKind is JsonValueKind.Object
        && outputs.TryGetProperty(key, out JsonElement value)
            ? value
            : default;
}
