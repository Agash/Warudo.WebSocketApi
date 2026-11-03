# Warudo.WebSocketApi

A Warudo plugin that serves a JSON-RPC 2.0 API over WebSocket on localhost, and **Warudo.Client**, the .NET
client for it. Streaming and automation tools use it to read and drive the opened Warudo scene: assets, data
inputs, triggers, blueprints, nodes, characters, and the objects inside an asset.

| Package / file | What it is |
| --- | --- |
| `WebSocketApi.warudo` | The plugin. Drop it into Warudo's `Warudo_Data/StreamingAssets/Plugins` folder. |
| `Warudo.Client` | .NET 10 and 11 client, Native AOT compatible. |

## Why a plugin

Warudo's own editor connection (port 19053) needs a token that changes every launch and appears only in
`Player.log`, answers commands without request ids, and has no subscriptions or run-time operations. This
plugin pairs once with a token that persists, answers every call with a result or a JSON-RPC error, pushes
events, and adds operations the editor protocol lacks: running any node type, expressions, blend shapes,
materials, input watches, and blueprint events in both directions.

## Pairing

The plugin's settings in Warudo show the port (19071 by default) and a pairing token. A client presents the
token once per connection, either as `?token=` in the connect URL or with `auth.pair`. "New pairing token"
makes a new one and disconnects every client.

```csharp
await using WarudoClient warudo = new(new WarudoClientOptions { Token = token });
await warudo.ConnectAsync();

JsonElement scene = await warudo.SceneGetAsync();
await warudo.EntitySetInputAsync(cameraId, "FieldOfView", JsonValue.Create(40));
await warudo.CharacterSetExpressionAsync(characterId, "Joy", active: true);
int receivers = await warudo.BlueprintFireEventAsync("Raid", ["SomeStreamer"], [42]);

warudo.EventReceived += (_, e) => Console.WriteLine($"{e.Name}: {e.Data}");
await warudo.EventsSubscribeAsync(["scene.changed", "entity.inputChanged", "blueprint.event"]);
```

## The API

Every method is one line of [`protocol/warudo-api.txt`](protocol/warudo-api.txt). A source generator reads that
table and emits both the plugin's dispatcher and the client's typed methods, so the two cannot drift.

| Area | Methods |
| --- | --- |
| Scene | `scene.get`, `scene.list`, `scene.open`, `scene.save`, `scene.selection`, `scene.assetTypes`, `scene.nodeTypes` |
| Plugins | `plugin.list`, `plugin.message` |
| Assets | `asset.add`, `asset.remove`, `asset.rename`, `asset.export`, `asset.import` |
| Entities | `entity.get`, `entity.getInput`, `entity.setInput`, `entity.resetInput`, `entity.invokeTrigger`, `entity.watch`, `entity.unwatch` |
| Nodes | `node.run` runs any node type once with inputs and returns its outputs |
| Blueprints | `blueprint.export`, `blueprint.import`, `blueprint.create`, `blueprint.remove`, `blueprint.rename`, `blueprint.setEnabled`, `blueprint.addNode`, `blueprint.removeNode`, `blueprint.connectFlow`, `blueprint.disconnectFlow`, `blueprint.connectData`, `blueprint.disconnectData`, `blueprint.invokeFlow`, `blueprint.fireEvent` |
| Characters | `character.expressions`, `character.activeExpressions`, `character.setExpression`, `character.exitAllExpressions` |
| Objects in an asset | `gameObject.hierarchy`, `gameObject.setActive`, `gameObject.setBlendShape`, `gameObject.setMaterialProperty` |
| Warudo UI | `ui.toast` |
| Events | `events.subscribe`, `events.unsubscribe`: `scene.changed`, `entity.inputChanged`, `blueprint.event` |

Data input values use Warudo's own serialized form, which is JSON for every port type: strings quoted, numbers
bare, enums as `{"value": n}`, asset references as `{"id": "..."}`. Every port type Warudo knows works, mod
types included.

## Typed assets and nodes

On top of the generic methods, Warudo.Client has a typed class for each of Warudo's 54 asset types and 530
node types, generated from [`schema/warudo-types.txt`](schema/warudo-types.txt):

```csharp
CameraAsset camera = new(warudo, cameraId);
await camera.SetFieldOfViewAsync(40);
await camera.SetControlModeAsync(CameraAsset.CameraControlMode.Orbit);
await camera.SetFocusCharacterAsync(characterId);

PropAsset prop = await PropAsset.AddAsync(warudo, "Confetti cannon");

StringContainsNode.Outputs found = await new StringContainsNode { A = message, B = "!raid", IgnoreCase = true }.RunAsync(warudo);
await new ShowToastNode { Header = "Raid", Caption = name, Severity = ShowToastNode.ToastSeverity.Success }.RunAsync(warudo);
Guid node = await new ThrowPropNode { TargetType = ThrowPropNode.TargetTypeOption.Character, Character = characterId }.AddToBlueprintAsync(warudo, blueprintId);
```

`tools/SchemaExport` writes the table from a Warudo install's assemblies without running Warudo: every
`[AssetType]` and `[NodeType]` with its `[DataInput]`, `[DataOutput]`, `[FlowInput]`, `[FlowOutput]` and
`[Trigger]` members. After a Warudo update, re-export and rebuild. `--mods` adds installed mods' types for a
local build. Ports a type adds at run time are not in the table and stay reachable through `entity.*`.

```powershell
dotnet run tools/SchemaExport/SchemaExport.cs -- [--warudo <install folder>] [--mods]
```
Inside Warudo, the **On WebSocket API Event** node fires for `blueprint.fireEvent`, and **Send WebSocket API
Event** pushes `blueprint.event` to subscribed clients.

## Building the plugin

The plugin compiles against Warudo's own assemblies, which are not redistributable, so it builds on a machine
with Warudo installed. CI builds and tests everything else.

```powershell
./build/package-plugin.ps1 -Install          # builds, packs artifacts/WebSocketApi.warudo, installs it
```

`tools/Warudo.ModPacker` writes the `.warudo` mod without Unity or the Warudo SDK. Things learned while
building it:

- **Restart Warudo after replacing the plugin.** Warudo hot-loads new mods, but a second build of an assembly
  it already loaded in the session does not register its plugin type, and nothing is logged.
- **The mod sandbox fails silently.** Warudo rejects an assembly that references a blocked API
  (`System.IO`, `System.Reflection`, which includes `Type.Name`, `System.Runtime.InteropServices` and
  others) without a log line. The plugin uses none of them.
- A code-only mod still needs the shared asset bundle files; the packer writes a bundle meta that lists no
  assets.

## Testing against Warudo

`samples/Smoke` exercises a running Warudo: pass the pairing token as an argument or in `WARUDO_API_TOKEN`.

```powershell
dotnet run samples/Smoke/Smoke.cs -- <token>
```

## License

MIT
