using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;
using Warudo.Core.Data;
using Warudo.Core.Graphs;
using Warudo.Core.Plugins;
using Warudo.Core.Scenes;
using Warudo.Core.Serializations;
using Warudo.Core.Server;
using Warudo.Plugins.Core.Assets;
using Warudo.Plugins.Core.Assets.Character;

namespace Warudo.WebSocketApi;

/// <summary>The API on Warudo's scene model; every call runs on the main thread.</summary>
internal sealed class WarudoApi : IWarudoApi
{
    private const string ProtocolVersion = "1";
    private const string DefaultFlowInput = "Enter";
    private readonly WebSocketApiPlugin _plugin;
    private readonly Dictionary<Guid, List<Guid>> _watches = new();

    public WarudoApi(WebSocketApiPlugin plugin) => _plugin = plugin;

    public Task<JToken> ApiVersionAsync() =>
        Result(
            new JObject
            {
                ["plugin"] = WebSocketApiPlugin.PluginVersion,
                ["protocol"] = ProtocolVersion,
                ["unity"] = Application.unityVersion,
                ["warudo"] = Application.version,
            }
        );

    // Scenes and plugins.

    public Task<JToken> SceneGetAsync()
    {
        Scene scene = OpenedScene();
        return Result(
            new JObject
            {
                ["name"] = scene.Name,
                ["assets"] = new JArray(
                    scene
                        .GetAssetList()
                        .Select(static a => new JObject
                        {
                            ["id"] = a.Id.ToString(),
                            ["name"] = a.Name,
                            ["type"] = a.GetTypeMeta()?.Id,
                            ["typeTitle"] = a.GetTypeMeta()?.AssetType?.title,
                            ["active"] = a.Active,
                        })
                ),
                ["blueprints"] = new JArray(
                    scene
                        .GetGraphList()
                        .Select(static g => new JObject
                        {
                            ["id"] = g.Id.ToString(),
                            ["name"] = g.Name,
                            ["enabled"] = g.Enabled,
                        })
                ),
            }
        );
    }

    public Task<JToken> SceneListAsync() =>
        Result(
            new JArray(
                Context
                    .SceneManager.GetScenes()
                    .Select(static s => new JObject
                    {
                        ["name"] = s.name,
                        ["lastModified"] = s.lastModifiedTime,
                    })
            )
        );

    public async Task SceneOpenAsync(string name)
    {
        if (!Context.SceneManager.HasScene(name))
        {
            throw new RpcException(RpcException.NotFound, $"No saved scene '{name}'.");
        }

        ClearWatches();
        await Context.SceneManager.OpenScene(name);
    }

    public async Task SceneSaveAsync() => await OpenedScene().Save();

    public Task<JToken> SceneSelectionAsync()
    {
        Scene scene = OpenedScene();
        return Result(
            new JObject
            {
                ["assetId"] = scene.GetSelectedAsset()?.Id.ToString(),
                ["blueprintId"] = scene.GetSelectedGraph()?.Id.ToString(),
            }
        );
    }

    public Task<JToken> SceneAssetTypesAsync() =>
        Result(JToken.FromObject(Context.AssetTypeRegistry.Serialize()));

    public Task<JToken> SceneNodeTypesAsync() =>
        Result(JToken.FromObject(Context.NodeTypeRegistry.Serialize()));

    public Task<JToken> PluginListAsync() =>
        Result(JToken.FromObject(Context.PluginManager.Serialize()));

    public Task PluginMessageAsync(string pluginId, string action, string? payload)
    {
        Plugin plugin =
            Context.PluginManager.GetPlugin(pluginId)
            ?? throw new RpcException(RpcException.NotFound, $"No plugin '{pluginId}'.");
        plugin.OnMessageReceived(action, payload ?? string.Empty);
        return Task.CompletedTask;
    }

    // Assets.

    public Task<Guid> AssetAddAsync(string typeId, string? name)
    {
        Scene scene = OpenedScene();
        Asset asset =
            scene.AddAsset(typeId)
            ?? throw new RpcException(RpcException.NotFound, $"No asset type '{typeId}'.");
        if (!string.IsNullOrEmpty(name))
        {
            asset.Name = name!;
        }

        Context.Service.BroadcastOpenedScene();
        return Task.FromResult(asset.Id);
    }

    public Task AssetRemoveAsync(Guid assetId)
    {
        Scene scene = OpenedScene();
        _ = Asset(assetId);
        scene.RemoveAsset(assetId);
        Context.Service.BroadcastOpenedScene();
        return Task.CompletedTask;
    }

    public Task AssetRenameAsync(Guid assetId, string name)
    {
        Asset(assetId).Name = name;
        Context.Service.BroadcastOpenedScene();
        return Task.CompletedTask;
    }

    public Task<JToken> AssetExportAsync(Guid assetId) =>
        Result(JToken.FromObject(Asset(assetId).Serialize()));

    public Task<Guid> AssetImportAsync(JToken asset)
    {
        SerializedAsset serialized =
            Renumber(asset).ToObject<SerializedAsset>()
            ?? throw new RpcException(
                RpcException.InvalidParams,
                "The asset is not in Warudo's serialized form."
            );
        Asset added = OpenedScene().DeserializeAsset(serialized);
        Context.Service.BroadcastOpenedScene();
        return Task.FromResult(added.Id);
    }

    // Entities: assets, nodes and plugins alike.

    public Task<JToken> EntityGetAsync(Guid entityId)
    {
        Entity entity = Find(entityId);
        JObject inputs = new();
        foreach (
            KeyValuePair<string, DataInputPort> port in entity.DataInputPortCollection.GetPorts()
        )
        {
            inputs[port.Key] = new JObject
            {
                ["type"] = port.Value.Type?.FullName,
                ["value"] = Value(port.Value),
            };
        }

        return Result(
            new JObject
            {
                ["id"] = entity.Id.ToString(),
                ["type"] = entity switch
                {
                    Asset a => a.GetTypeMeta()?.Id,
                    Node n => n.GetTypeMeta()?.Id,
                    Plugin p => p.PluginId,
                    _ => null,
                },
                ["typeTitle"] = entity switch
                {
                    Asset a => a.GetTypeMeta()?.AssetType?.title,
                    Node n => n.GetTypeMeta()?.NodeType?.title,
                    _ => null,
                },
                ["name"] = entity switch
                {
                    Asset a => a.Name,
                    Node n => n.Name,
                    _ => null,
                },
                ["inputs"] = inputs,
                ["triggers"] = new JArray(entity.TriggerPortCollection.GetPorts().Keys),
            }
        );
    }

    public Task<JToken> EntityGetInputAsync(Guid entityId, string key) =>
        Result(Value(Port(Find(entityId), key)));

    public Task EntitySetInputAsync(Guid entityId, string key, JToken value)
    {
        Entity entity = Find(entityId);
        SetSerialized(entity, Port(entity, key), value);
        entity.BroadcastDataInput(key);
        return Task.CompletedTask;
    }

    public Task EntityResetInputAsync(Guid entityId, string key)
    {
        Entity entity = Find(entityId);
        _ = Port(entity, key);
        entity.ResetDataInput(key, true);
        return Task.CompletedTask;
    }

    public Task EntityInvokeTriggerAsync(Guid entityId, string trigger)
    {
        Entity entity = Find(entityId);
        if (entity.GetTriggerPort(trigger) is null)
        {
            throw new RpcException(
                RpcException.NotFound,
                $"Entity {entityId} has no trigger '{trigger}'."
            );
        }

        entity.InvokeTrigger(trigger);
        return Task.CompletedTask;
    }

    public Task EntityWatchAsync(Guid entityId, string[]? keys)
    {
        Entity entity = Find(entityId);
        Unwatch(entity);
        List<Guid> handles = new();
        foreach (string key in keys ?? entity.DataInputPortCollection.GetPorts().Keys.ToArray())
        {
            DataInputPort port = Port(entity, key);
            handles.Add(
                _plugin.Watch(
                    entity,
                    key,
                    () =>
                        _plugin.Publish(
                            WarudoEvents.InputChanged,
                            new JObject
                            {
                                ["entityId"] = entity.Id.ToString(),
                                ["key"] = key,
                                ["value"] = Value(port),
                            }
                        ),
                    false
                )
            );
        }

        _watches[entityId] = handles;
        return Task.CompletedTask;
    }

    public Task EntityUnwatchAsync(Guid entityId)
    {
        Unwatch(Find(entityId));
        return Task.CompletedTask;
    }

    public Task<JToken> NodeRunAsync(string typeId, JToken? inputs, string? flowInput)
    {
        Scene scene = OpenedScene();
        Graph graph = new() { Name = "WebSocket API (temporary)" };
        scene.AddGraph(graph);
        try
        {
            Node node =
                graph.AddNode(typeId)
                ?? throw new RpcException(RpcException.NotFound, $"No node type '{typeId}'.");
            if (inputs is JObject values)
            {
                foreach (KeyValuePair<string, JToken?> input in values)
                {
                    SetSerialized(node, Port(node, input.Key), input.Value ?? JValue.CreateNull());
                }
            }

            graph.Enabled = true;
            graph.InvokeFlowAtInput(
                node,
                string.IsNullOrEmpty(flowInput) ? DefaultFlowInput : flowInput,
                true
            );
            JObject outputs = new();
            foreach (
                KeyValuePair<
                    string,
                    DataOutputPort
                > output in node.DataOutputPortCollection.GetPorts()
            )
            {
                object? computed = output.Value.ComputedValue?.Invoke();
                outputs[output.Key] = computed is null ? JValue.CreateNull() : SafeToken(computed);
            }

            return Result(outputs);
        }
        finally
        {
            scene.RemoveGraph(graph.Id);
        }
    }

    // Blueprints.

    public Task<JToken> BlueprintExportAsync(Guid blueprintId) =>
        Result(JToken.FromObject(Blueprint(blueprintId).Serialize()));

    public Task<Guid> BlueprintImportAsync(JToken blueprint)
    {
        SerializedGraph serialized =
            Renumber(blueprint).ToObject<SerializedGraph>()
            ?? throw new RpcException(
                RpcException.InvalidParams,
                "The blueprint is not in Warudo's serialized form."
            );
        Graph graph = OpenedScene().DeserializeGraph(serialized);
        Context.Service.BroadcastOpenedScene();
        return Task.FromResult(graph.Id);
    }

    public Task<Guid> BlueprintCreateAsync(string name)
    {
        Graph graph = new() { Name = name };
        OpenedScene().AddGraph(graph);
        Context.Service.BroadcastOpenedScene();
        return Task.FromResult(graph.Id);
    }

    public Task BlueprintRemoveAsync(Guid blueprintId)
    {
        _ = Blueprint(blueprintId);
        OpenedScene().RemoveGraph(blueprintId);
        Context.Service.BroadcastOpenedScene();
        return Task.CompletedTask;
    }

    public Task BlueprintRenameAsync(Guid blueprintId, string name)
    {
        Blueprint(blueprintId).Name = name;
        Context.Service.BroadcastOpenedScene();
        return Task.CompletedTask;
    }

    public Task BlueprintSetEnabledAsync(Guid blueprintId, bool enabled)
    {
        Blueprint(blueprintId).Enabled = enabled;
        Context.Service.BroadcastOpenedScene();
        return Task.CompletedTask;
    }

    public Task<Guid> BlueprintAddNodeAsync(Guid blueprintId, string typeId, double? x, double? y)
    {
        Node node =
            Blueprint(blueprintId).AddNode(typeId)
            ?? throw new RpcException(RpcException.NotFound, $"No node type '{typeId}'.");
        node.GraphPosition = new Vector2((float)(x ?? 0), (float)(y ?? 0));
        Context.Service.BroadcastOpenedScene();
        return Task.FromResult(node.Id);
    }

    public Task BlueprintRemoveNodeAsync(Guid blueprintId, Guid nodeId)
    {
        Graph graph = Blueprint(blueprintId);
        _ = NodeIn(graph, nodeId);
        graph.RemoveNode(nodeId);
        Context.Service.BroadcastOpenedScene();
        return Task.CompletedTask;
    }

    public Task<bool> BlueprintConnectFlowAsync(
        Guid blueprintId,
        Guid fromNode,
        string fromPort,
        Guid toNode,
        string toPort
    )
    {
        Graph graph = Blueprint(blueprintId);
        bool connected = graph.AddFlowConnection(
            NodeIn(graph, fromNode),
            fromPort,
            NodeIn(graph, toNode),
            toPort
        );
        Context.Service.BroadcastOpenedScene();
        return Task.FromResult(connected);
    }

    public Task BlueprintDisconnectFlowAsync(
        Guid blueprintId,
        Guid fromNode,
        string fromPort,
        Guid toNode,
        string toPort
    )
    {
        Graph graph = Blueprint(blueprintId);
        graph.RemoveFlowConnection(
            NodeIn(graph, fromNode),
            fromPort,
            NodeIn(graph, toNode),
            toPort
        );
        Context.Service.BroadcastOpenedScene();
        return Task.CompletedTask;
    }

    public Task<bool> BlueprintConnectDataAsync(
        Guid blueprintId,
        Guid fromNode,
        string fromPort,
        Guid toNode,
        string toPort
    )
    {
        Graph graph = Blueprint(blueprintId);
        bool connected = graph.AddDataConnection(
            NodeIn(graph, fromNode),
            fromPort,
            NodeIn(graph, toNode),
            toPort
        );
        Context.Service.BroadcastOpenedScene();
        return Task.FromResult(connected);
    }

    public Task BlueprintDisconnectDataAsync(
        Guid blueprintId,
        Guid fromNode,
        string fromPort,
        Guid toNode,
        string toPort
    )
    {
        Graph graph = Blueprint(blueprintId);
        graph.RemoveDataConnection(
            NodeIn(graph, fromNode),
            fromPort,
            NodeIn(graph, toNode),
            toPort
        );
        Context.Service.BroadcastOpenedScene();
        return Task.CompletedTask;
    }

    public Task BlueprintInvokeFlowAsync(Guid blueprintId, Guid nodeId, string? flowInput)
    {
        Graph graph = Blueprint(blueprintId);
        graph.InvokeFlowAtInput(
            NodeIn(graph, nodeId),
            string.IsNullOrEmpty(flowInput) ? DefaultFlowInput : flowInput,
            false
        );
        return Task.CompletedTask;
    }

    public Task<int> BlueprintFireEventAsync(string name, string[]? texts, double[]? numbers)
    {
        int receivers = 0;
        foreach (Graph graph in OpenedScene().GetGraphList().Where(static g => g.Enabled))
        {
            foreach (
                OnWebSocketApiEventNode node in graph
                    .GetNodes()
                    .Values.OfType<OnWebSocketApiEventNode>()
            )
            {
                if (string.Equals(node.EventName, name, StringComparison.Ordinal))
                {
                    node.Fire(texts ?? Array.Empty<string>(), numbers ?? Array.Empty<double>());
                    receivers++;
                }
            }
        }

        return Task.FromResult(receivers);
    }

    // Characters.

    public Task<string[]> CharacterExpressionsAsync(Guid characterId) =>
        Task.FromResult(Character(characterId).ExpressionNames.ToArray());

    public Task<string[]> CharacterActiveExpressionsAsync(Guid characterId) =>
        Task.FromResult(Character(characterId).ActiveExpressions.ToArray());

    public Task CharacterSetExpressionAsync(
        Guid characterId,
        string name,
        bool active,
        bool? transient
    )
    {
        CharacterAsset character = Character(characterId);
        if (!character.HasExpression(name))
        {
            throw new RpcException(
                RpcException.NotFound,
                $"The character has no expression '{name}'."
            );
        }

        if (active)
        {
            character.EnterExpression(name, transient ?? false);
        }
        else
        {
            character.ExitExpression(name);
        }

        return Task.CompletedTask;
    }

    public Task CharacterExitAllExpressionsAsync(Guid characterId, bool? fade)
    {
        Character(characterId).ExitAllExpressions(fade ?? true);
        return Task.CompletedTask;
    }

    // GameObjects under an asset.

    public Task<JToken> GameObjectHierarchyAsync(Guid assetId, int? depth)
    {
        Transform root = GameObjectOf(assetId).transform;
        return Result(Describe(root, string.Empty, Math.Max(0, depth ?? 8)));
    }

    public Task GameObjectSetActiveAsync(Guid assetId, string path, bool active)
    {
        Child(assetId, path).gameObject.SetActive(active);
        return Task.CompletedTask;
    }

    public Task GameObjectSetBlendShapeAsync(Guid assetId, string path, string name, double weight)
    {
        SkinnedMeshRenderer renderer =
            Child(assetId, path).GetComponent<SkinnedMeshRenderer>()
            ?? throw new RpcException(RpcException.NotFound, $"'{path}' has no skinned mesh.");
        int index = renderer.sharedMesh != null ? renderer.sharedMesh.GetBlendShapeIndex(name) : -1;
        if (index < 0)
        {
            throw new RpcException(RpcException.NotFound, $"'{path}' has no blend shape '{name}'.");
        }

        renderer.SetBlendShapeWeight(index, (float)weight);
        return Task.CompletedTask;
    }

    public Task GameObjectSetMaterialPropertyAsync(
        Guid assetId,
        string path,
        string property,
        JToken value,
        int? materialIndex
    )
    {
        Renderer renderer =
            Child(assetId, path).GetComponent<Renderer>()
            ?? throw new RpcException(RpcException.NotFound, $"'{path}' has no renderer.");
        Material[] materials = renderer.materials;
        int index = materialIndex ?? 0;
        if (index < 0 || index >= materials.Length)
        {
            throw new RpcException(
                RpcException.InvalidParams,
                $"'{path}' has {materials.Length} materials; {index} is out of range."
            );
        }

        Material material = materials[index];
        if (!material.HasProperty(property))
        {
            throw new RpcException(
                RpcException.NotFound,
                $"Material '{material.name}' has no property '{property}'."
            );
        }

        switch (value)
        {
            case JArray array when array.Count is 3 or 4 && material.HasColor(property):
                material.SetColor(
                    property,
                    new Color(
                        F(array, 0),
                        F(array, 1),
                        F(array, 2),
                        array.Count == 4 ? F(array, 3) : 1f
                    )
                );
                break;
            case JArray array:
                material.SetVector(
                    property,
                    new Vector4(F(array, 0), F(array, 1), F(array, 2), F(array, 3))
                );
                break;
            case JValue { Type: JTokenType.Boolean } flag:
                material.SetFloat(property, flag.Value<bool>() ? 1f : 0f);
                break;
            case JValue { Type: JTokenType.Integer or JTokenType.Float } number:
                material.SetFloat(property, number.Value<float>());
                break;
            default:
                throw new RpcException(
                    RpcException.InvalidParams,
                    "A material value is a number, a boolean, or an array of 3 or 4 numbers."
                );
        }

        return Task.CompletedTask;
    }

    // Warudo's UI.

    public Task UiToastAsync(string header, string message, string? severity, double? seconds)
    {
        ToastSeverity level = severity?.ToLowerInvariant() switch
        {
            null or "" or "info" => ToastSeverity.Info,
            "success" => ToastSeverity.Success,
            "warning" => ToastSeverity.Warning,
            "error" => ToastSeverity.Error,
            _ => throw new RpcException(
                RpcException.InvalidParams,
                $"Unknown severity '{severity}'; use success, info, warning or error."
            ),
        };
        Context.Service.Toast(level, header, message, null, TimeSpan.FromSeconds(seconds ?? 5));
        return Task.CompletedTask;
    }

    // The session answers these itself; the dispatcher never reaches them.
    public Task<string[]> EventsSubscribeAsync(string[] events) =>
        throw new RpcException(RpcException.InternalError, "Handled by the session.");

    public Task EventsUnsubscribeAsync(string[] events) =>
        throw new RpcException(RpcException.InternalError, "Handled by the session.");

    /// <summary>Drops every watch, as a scene change does.</summary>
    internal void ClearWatches()
    {
        foreach (KeyValuePair<Guid, List<Guid>> watch in _watches)
        {
            if (Context.EntityStore.Get(watch.Key) is { } entity)
            {
                foreach (Guid handle in watch.Value)
                {
                    _plugin.Unwatch(entity, handle);
                }
            }
        }

        _watches.Clear();
    }

    private void Unwatch(Entity entity)
    {
        if (_watches.TryGetValue(entity.Id, out List<Guid>? handles))
        {
            foreach (Guid handle in handles)
            {
                _plugin.Unwatch(entity, handle);
            }

            _ = _watches.Remove(entity.Id);
        }
    }

    private static Task<JToken> Result(JToken value) => Task.FromResult(value);

    private static Scene OpenedScene() =>
        Context.OpenedScene ?? throw new RpcException(RpcException.NotFound, "No scene is open.");

    private static Entity Find(Guid id) =>
        Context.EntityStore.Get(id)
        ?? throw new RpcException(RpcException.NotFound, $"No entity {id}.");

    private static Asset Asset(Guid id) =>
        OpenedScene().GetAsset(id)
        ?? throw new RpcException(RpcException.NotFound, $"No asset {id}.");

    private static Graph Blueprint(Guid id) =>
        OpenedScene().GetGraph(id)
        ?? throw new RpcException(RpcException.NotFound, $"No blueprint {id}.");

    private static Node NodeIn(Graph graph, Guid id) =>
        graph.GetNode(id)
        ?? throw new RpcException(RpcException.NotFound, $"Blueprint {graph.Id} has no node {id}.");

    private static DataInputPort Port(Entity entity, string key) =>
        entity.GetDataInputPort(key)
        ?? throw new RpcException(
            RpcException.NotFound,
            $"Entity {entity.Id} has no data input '{key}'."
        );

    private static CharacterAsset Character(Guid id) =>
        Find(id) as CharacterAsset
        ?? throw new RpcException(RpcException.NotFound, $"Entity {id} is not a character.");

    private static GameObject GameObjectOf(Guid id) =>
        (Find(id) as GameObjectAsset)?.GameObject
        ?? throw new RpcException(RpcException.NotFound, $"Entity {id} has no GameObject.");

    private static Transform Child(Guid assetId, string path)
    {
        Transform root = GameObjectOf(assetId).transform;
        return string.IsNullOrEmpty(path)
            ? root
            : root.Find(path)
                ?? throw new RpcException(
                    RpcException.NotFound,
                    $"No object '{path}' under asset {assetId}."
                );
    }

    private static JObject Describe(Transform transform, string path, int depth)
    {
        JObject node = new()
        {
            ["name"] = transform.name,
            ["path"] = path,
            ["active"] = transform.gameObject.activeSelf,
        };
        if (
            transform.TryGetComponent(out SkinnedMeshRenderer skinned)
            && skinned.sharedMesh != null
        )
        {
            Mesh mesh = skinned.sharedMesh;
            node["blendShapes"] = new JArray(
                Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName)
            );
        }

        if (transform.TryGetComponent(out Renderer renderer))
        {
            node["materials"] = new JArray(
                renderer.sharedMaterials.Where(static m => m != null).Select(static m => m.name)
            );
        }

        if (depth > 0 && transform.childCount > 0)
        {
            JArray children = new();
            foreach (Transform child in transform)
            {
                children.Add(
                    Describe(
                        child,
                        path.Length == 0 ? child.name : path + "/" + child.name,
                        depth - 1
                    )
                );
            }

            node["children"] = children;
        }

        return node;
    }

    // Warudo's own serialized form, so every port type it knows (vectors, assets, enums) is accepted.
    private static void SetSerialized(Entity entity, DataInputPort port, JToken value)
    {
        string serialized =
            value.Type == JTokenType.String
                ? value.ToString()
                : value.ToString(Newtonsoft.Json.Formatting.None);
        port.SetSerializedValue(serialized, OpenedScene(), entity.StructuredDataParent);
    }

    // Every id in an exported asset or blueprint is replaced with a new one, so importing it twice, or into the
    // scene it came from, cannot collide with entities that exist.
    private static JToken Renumber(JToken exported)
    {
        string text = exported.ToString(Newtonsoft.Json.Formatting.None);
        HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);
        CollectIds(exported, ids);
        StringBuilder renumbered = new(text);
        foreach (string id in ids)
        {
            _ = renumbered.Replace(id, Guid.NewGuid().ToString());
        }

        return JToken.Parse(renumbered.ToString());
    }

    private static void CollectIds(JToken token, HashSet<string> ids)
    {
        switch (token)
        {
            case JObject obj:
                foreach (JProperty property in obj.Properties())
                {
                    if (
                        property.Name == "id"
                        && property.Value.Type == JTokenType.String
                        && Guid.TryParse(property.Value.ToString(), out _)
                    )
                    {
                        _ = ids.Add(property.Value.ToString());
                    }

                    CollectIds(property.Value, ids);
                }

                break;
            case JArray array:
                foreach (JToken item in array)
                {
                    CollectIds(item, ids);
                }

                break;
        }
    }

    private static float F(JArray array, int index) =>
        index < array.Count ? array[index].Value<float>() : 0f;

    // A port's value in Warudo's serialized form, parsed as JSON when it is JSON.
    private static JToken Value(DataInputPort port)
    {
        string? serialized = port.SerializeValue();
        if (serialized is null)
        {
            return JValue.CreateNull();
        }

        try
        {
            return JToken.Parse(serialized);
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return new JValue(serialized);
        }
    }

    private static JToken SafeToken(object value)
    {
        try
        {
            return JToken.FromObject(value);
        }
        catch (Exception ex)
            when (ex
                    is Newtonsoft.Json.JsonException
                        or InvalidOperationException
                        or NotSupportedException
            )
        {
            // Unity objects do not serialize; their name is what a caller can use.
            return new JValue(value.ToString());
        }
    }
}
