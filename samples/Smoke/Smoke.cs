#:project ../../src/Warudo.Client/Warudo.Client.csproj
#:property GenerateDocumentationFile=false
using System.Text.Json;
using Warudo.Client;

// Exercises a running Warudo with the WebSocket API plugin: pass the pairing token from the plugin's settings.
string token =
    args.Length > 0
        ? args[0]
        : Environment.GetEnvironmentVariable("WARUDO_API_TOKEN") ?? string.Empty;
await using WarudoClient client = new(new WarudoClientOptions { Token = token });
await client.ConnectAsync();

Console.WriteLine($"version: {await client.ApiVersionAsync()}");
JsonElement scene = await client.SceneGetAsync();
Console.WriteLine(
    $"scene: {scene.GetProperty("name")}, {scene.GetProperty("assets").GetArrayLength()} assets, {scene.GetProperty("blueprints").GetArrayLength()} blueprints"
);
Console.WriteLine($"saved scenes: {(await client.SceneListAsync()).GetArrayLength()}");
Console.WriteLine(
    $"node types: {(await client.SceneNodeTypesAsync()).ToString().Length} chars, asset types: {(await client.SceneAssetTypesAsync()).ToString().Length} chars"
);

foreach (JsonElement asset in scene.GetProperty("assets").EnumerateArray())
{
    Guid id = Guid.Parse(asset.GetProperty("id").GetString()!);
    JsonElement entity = await client.EntityGetAsync(id);
    Console.WriteLine(
        $"  {asset.GetProperty("typeTitle")} '{asset.GetProperty("name")}': {entity.GetProperty("inputs").EnumerateObject().Count()} inputs"
    );
    if (asset.GetProperty("typeTitle").GetString() == "CHARACTER")
    {
        Console.WriteLine(
            $"    expressions: {string.Join(", ", await client.CharacterExpressionsAsync(id))}"
        );
        Console.WriteLine(
            $"    active: {string.Join(", ", await client.CharacterActiveExpressionsAsync(id))}"
        );
        JsonElement tree = await client.GameObjectHierarchyAsync(id, 2);
        Console.WriteLine(
            $"    children: {(tree.TryGetProperty("children", out JsonElement c) ? c.GetArrayLength() : 0)}"
        );
    }

    if (asset.GetProperty("typeTitle").GetString() == "CAMERA")
    {
        JsonElement fov = await client.EntityGetInputAsync(id, "FieldOfView");
        TaskCompletionSource<WarudoEventArgs> changed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        client.EventReceived += (_, e) =>
        {
            if (e.Name == "entity.inputChanged")
                changed.TrySetResult(e);
        };
        await client.EventsSubscribeAsync(["entity.inputChanged"]);
        await client.EntityWatchAsync(id, ["FieldOfView"]);
        await client.EntitySetInputAsync(
            id,
            "FieldOfView",
            System.Text.Json.Nodes.JsonValue.Create(fov.GetDouble() + 5)
        );
        WarudoEventArgs e = await changed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Console.WriteLine(
            $"    FieldOfView {fov} -> {await client.EntityGetInputAsync(id, "FieldOfView")}, event {e.Data.GetProperty("key")}={e.Data.GetProperty("value")}"
        );
        await client.EntityUnwatchAsync(id);
        await client.EntitySetInputAsync(
            id,
            "FieldOfView",
            System.Text.Json.Nodes.JsonValue.Create(fov.GetDouble())
        );
    }
}

Guid blueprint = await client.BlueprintCreateAsync("WebSocket API smoke");
JsonElement exported = await client.BlueprintExportAsync(blueprint);
Guid copy = await client.BlueprintImportAsync(
    System.Text.Json.Nodes.JsonNode.Parse(exported.GetRawText())!
);
Console.WriteLine(
    $"fireEvent receivers: {await client.BlueprintFireEventAsync("smoke", ["hello"], [1])}"
);
Console.WriteLine($"blueprint created {blueprint}, imported copy {copy}");
await client.BlueprintRemoveAsync(copy);
await client.BlueprintRemoveAsync(blueprint);
await client.UiToastAsync("WebSocket API", "Smoke test passed", "success");
Console.WriteLine("ok");

// The generated typed layer.
Warudo.Client.Assets.CameraAsset camera = new(
    client,
    Guid.Parse(
        scene
            .GetProperty("assets")
            .EnumerateArray()
            .First(a => a.GetProperty("typeTitle").GetString() == "CAMERA")
            .GetProperty("id")
            .GetString()!
    )
);
double fieldOfView = await camera.GetFieldOfViewAsync();
Warudo.Client.Assets.CameraAsset.CameraControlMode mode = await camera.GetControlModeAsync();
Guid? focus = await camera.GetFocusCharacterAsync();
await camera.SetFocusCharacterAsync(focus);
Console.WriteLine(
    $"typed camera: FieldOfView {fieldOfView}, ControlMode {mode}, FocusCharacter {focus} (set back {await camera.GetFocusCharacterAsync() == focus})"
);

Warudo.Client.Nodes.BooleanEqualNode.Outputs equal = await new Warudo.Client.Nodes.BooleanEqualNode
{
    A = true,
    B = true,
}.RunAsync(client);
Console.WriteLine($"typed data node: true == true is {equal.Result}");
Warudo.Client.Nodes.StringContainsNode.Outputs contains =
    await new Warudo.Client.Nodes.StringContainsNode
    {
        A = "Hello Warudo",
        B = "warudo",
        IgnoreCase = true,
    }.RunAsync(client);
Console.WriteLine($"typed string node: contains is {contains.Result}");
await new Warudo.Client.Nodes.ShowToastNode
{
    Header = "WebSocket API",
    Caption = "Typed node run",
    Severity = Warudo.Client.Nodes.ShowToastNode.ToastSeverity.Success,
}.RunAsync(client);
Console.WriteLine("typed flow node: ran Show Toast");

Warudo.Client.Assets.PropAsset prop = await Warudo.Client.Assets.PropAsset.AddAsync(
    client,
    "WebSocket API smoke prop"
);
Console.WriteLine($"typed asset add: {prop.Id}, enabled {await prop.GetEnabledAsync()}");
await prop.RemoveAsync();
Console.WriteLine("typed ok");
