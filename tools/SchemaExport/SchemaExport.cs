#:package System.Reflection.MetadataLoadContext
#:property GenerateDocumentationFile=false
#:property PublishAot=false
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;

// Reads the asset and node types a Warudo install declares, core and mods, from the [AssetType] and [NodeType]
// attributes and their [DataInput], [DataOutput], [FlowInput], [FlowOutput] and [Trigger] members, and writes
// schema/warudo-types.txt for Warudo.Client's type generator. It reads metadata only; Warudo need not run.
// Installed mods are included with --mods; the committed table describes Warudo itself.
//
//   dotnet run tools/SchemaExport/SchemaExport.cs -- [--warudo <install folder>] [--mods] [--out schema/warudo-types.txt]

string warudo = Arg("--warudo") ?? @"C:\Program Files (x86)\Steam\steamapps\common\Warudo";
string output = Arg("--out") ?? Path.Combine("schema", "warudo-types.txt");
bool withMods = args.Contains("--mods");
string managed = Path.Combine(warudo, "Warudo_Data", "Managed");
if (!File.Exists(Path.Combine(managed, "Warudo.Core.dll")))
{
    Console.Error.WriteLine($"Warudo is not installed at {warudo}; pass --warudo.");
    return 2;
}

// Mods ship their assembly inside the .warudo zip; extract them beside the install's assemblies.
string modDir = Directory.CreateTempSubdirectory("warudo-mods").FullName;

// Warudo's own types live in Warudo.Core and every assembly that references it (Assembly-CSharp holds the
// built-in trackers and receivers).
List<string> sources =
[
    .. Directory.GetFiles(managed, "*.dll").Where(static path => DeclaresWarudoTypes(path)),
];
string workshop = Path.GetFullPath(
    Path.Combine(warudo, "..", "..", "workshop", "content", "2079120")
);
foreach (
    string mod in new[]
    {
        Path.Combine(warudo, "Warudo_Data", "StreamingAssets", "Plugins"),
        workshop,
    }
        .Where(d => withMods && Directory.Exists(d))
        .SelectMany(d => Directory.EnumerateFiles(d, "*.warudo", SearchOption.AllDirectories))
)
{
    sources.AddRange(ExtractAssemblies(mod, modDir));
}

string[] resolve =
[
    .. Directory.GetFiles(managed, "*.dll"),
    .. sources,
    .. Directory.GetFiles(
        System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),
        "*.dll"
    ),
];
MetadataLoadContext context = new(
    new PathAssemblyResolver(resolve.DistinctBy(Path.GetFileName)),
    "netstandard"
);

JsonArray assets = [];
JsonArray nodes = [];
foreach (string source in sources.Distinct())
{
    Assembly assembly = context.LoadFromAssemblyPath(source);
    foreach (
        Type type in Types(assembly)
            .Where(static t => !t.IsAbstract)
            .OrderBy(static t => t.FullName, StringComparer.Ordinal)
    )
    {
        if (Attribute(type, "AssetTypeAttribute") is { } asset)
        {
            assets.Add(Describe(type, asset, node: false, Path.GetFileName(source)));
        }
        else if (Attribute(type, "NodeTypeAttribute") is { } node)
        {
            nodes.Add(Describe(type, node, node: true, Path.GetFileName(source)));
        }
    }
}

string unity =
    System
        .Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(warudo, "Warudo.exe"))
        .ProductVersion
    ?? "unknown";
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
await File.WriteAllTextAsync(output, Table(unity, assets, nodes));
Console.WriteLine(
    $"Wrote {assets.Count} asset types and {nodes.Count} node types from {sources.Count} assemblies to {output}"
);
context.Dispose();
Directory.Delete(modDir, recursive: true);
return 0;

static string Table(string unity, JsonArray assets, JsonArray nodes)
{
    const char Tab = '\t';
    StringBuilder table = new();
    table.AppendLine(
        System.Globalization.CultureInfo.InvariantCulture,
        $"# Asset and node types Warudo declares, exported by tools/SchemaExport from Unity {unity}."
    );
    table.AppendLine(
        "# kind<TAB>class<TAB>type id<TAB>title<TAB>category, then one indented line per port:"
    );
    table.AppendLine(
        "#   input|output<TAB>key<TAB>kind<TAB>CLR type<TAB>label<TAB>enum values;  flowInput|flowOutput|trigger<TAB>key<TAB>label"
    );
    foreach ((string kind, JsonArray types) in new[] { ("asset", assets), ("node", nodes) })
    {
        foreach (JsonNode? type in types)
        {
            table
                .AppendJoin(
                    Tab,
                    kind,
                    Text(type!["class"]),
                    Text(type["id"]),
                    Text(type["title"]),
                    Text(type["category"])
                )
                .Append('\n');
            foreach (string section in new[] { "inputs", "outputs" })
            {
                foreach (JsonNode? port in type[section]?.AsArray() ?? [])
                {
                    string values = port!["values"] is JsonArray list
                        ? string.Join(",", list.Select(static v => v!.GetValue<string>()))
                        : string.Empty;
                    table
                        .Append(Tab)
                        .AppendJoin(
                            Tab,
                            section.TrimEnd('s'),
                            Text(port["key"]),
                            Text(port["type"]),
                            Text(port["clrType"]),
                            Text(port["label"]),
                            values
                        )
                        .Append('\n');
                }
            }

            foreach (string section in new[] { "flowInputs", "flowOutputs" })
            {
                foreach (JsonNode? key in type[section]?.AsArray() ?? [])
                {
                    table.Append(Tab).AppendJoin(Tab, section.TrimEnd('s'), Text(key)).Append('\n');
                }
            }

            foreach (JsonNode? trigger in type["triggers"]?.AsArray() ?? [])
            {
                table
                    .Append(Tab)
                    .AppendJoin(Tab, "trigger", Text(trigger!["key"]), Text(trigger["label"]))
                    .Append('\n');
            }
        }
    }

    return table.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
}

static string Text(JsonNode? node) =>
    node?.GetValue<string>().Replace('\t', ' ').Replace('\n', ' ') ?? string.Empty;

string? Arg(string name) =>
    Array.IndexOf(args, name) is >= 0 and var i && i + 1 < args.Length ? args[i + 1] : null;

static IEnumerable<Type> Types(Assembly assembly)
{
    try
    {
        return assembly.GetTypes();
    }
    catch (ReflectionTypeLoadException ex)
    {
        // A mod type whose base lives in an assembly that is not installed is skipped; the rest still export.
        Console.Error.WriteLine(
            $"{assembly.GetName().Name}: {ex.LoaderExceptions.Length} types could not be read and are skipped."
        );
        return ex.Types.Where(static t => t is not null)!;
    }
}

static CustomAttributeData? Attribute(MemberInfo member, string name) =>
    member
        .GetCustomAttributesData()
        .FirstOrDefault(a =>
            a.AttributeType.Namespace == "Warudo.Core.Attributes" && a.AttributeType.Name == name
        );

static string? Named(CustomAttributeData attribute, string name) =>
    attribute.NamedArguments.FirstOrDefault(a => a.MemberName == name).TypedValue.Value?.ToString();

static JsonObject Describe(Type type, CustomAttributeData declared, bool node, string source)
{
    // Base types first, so ports read in the order Warudo shows them.
    List<Type> chain = [];
    for (
        Type? t = type;
        t is not null && t.Namespace?.StartsWith("System", StringComparison.Ordinal) != true;
        t = t.BaseType
    )
    {
        chain.Insert(0, t);
    }

    const BindingFlags members =
        BindingFlags.Public
        | BindingFlags.NonPublic
        | BindingFlags.Instance
        | BindingFlags.DeclaredOnly;
    JsonArray inputs = [],
        outputs = [],
        flowInputs = [],
        flowOutputs = [],
        triggers = [];
    foreach (Type level in chain)
    {
        foreach (FieldInfo field in level.GetFields(members))
        {
            if (Attribute(field, "DataInputAttribute") is not null)
            {
                inputs.Add(Port(field.Name, field.FieldType, field));
            }
            else if (Attribute(field, "FlowOutputAttribute") is not null)
            {
                flowOutputs.Add(field.Name);
            }
        }

        foreach (MethodInfo method in level.GetMethods(members))
        {
            if (Attribute(method, "DataOutputAttribute") is not null)
            {
                outputs.Add(Port(method.Name, method.ReturnType, method));
            }
            else if (Attribute(method, "FlowInputAttribute") is not null)
            {
                flowInputs.Add(method.Name);
            }
            else if (Attribute(method, "TriggerAttribute") is not null)
            {
                triggers.Add(new JsonObject { ["key"] = method.Name, ["label"] = Label(method) });
            }
        }
    }

    JsonObject described = new()
    {
        ["id"] = Named(declared, "Id"),
        ["title"] = Named(declared, "Title"),
        ["category"] = Named(declared, "Category"),
        ["class"] = type.Name,
        ["source"] = source,
        ["inputs"] = inputs,
    };
    if (node)
    {
        described["outputs"] = outputs;
        described["flowInputs"] = flowInputs;
        described["flowOutputs"] = flowOutputs;
    }

    described["triggers"] = triggers;
    return described;
}

static JsonObject Port(string key, Type type, MemberInfo member)
{
    JsonObject port = new()
    {
        ["key"] = key,
        ["type"] = Kind(type),
        ["clrType"] = type.ToString(),
    };
    if (Label(member) is { } label)
    {
        port["label"] = label;
    }

    if (type.IsEnum)
    {
        port["values"] = new JsonArray([
            .. type.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(static f =>
                    (JsonNode)
                        $"{f.Name}={Convert.ToInt64(f.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture)}"
                ),
        ]);
    }

    if (Attribute(member, "HiddenAttribute") is not null)
    {
        port["hidden"] = true;
    }

    return port;
}

static string? Label(MemberInfo member) =>
    Attribute(member, "LabelAttribute") is { ConstructorArguments: [{ Value: string label }, ..] }
        ? label
        : null;

// The JSON shape a value of this type takes in Warudo's serialized form.
static string Kind(Type type) =>
    type.FullName switch
    {
        "System.Boolean" => "bool",
        "System.Int32" or "System.Int64" => "int",
        "System.Single" or "System.Double" => "number",
        "System.String" => "string",
        _ when type.IsEnum => "enum",
        _ when DerivesFrom(type, "Warudo.Core.Scenes.Asset") => "asset",
        _ when type.IsArray => "array",
        "UnityEngine.Vector2"
        or "UnityEngine.Vector3"
        or "UnityEngine.Vector4"
        or "UnityEngine.Quaternion"
        or "UnityEngine.Color" => "vector",
        _ => "json",
    };

static bool DeclaresWarudoTypes(string path)
{
    try
    {
        using System.Reflection.PortableExecutable.PEReader pe = new(File.OpenRead(path));
        if (!pe.HasMetadata)
        {
            return false;
        }

        System.Reflection.Metadata.MetadataReader md =
            System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        return Path.GetFileName(path) == "Warudo.Core.dll"
            || md.AssemblyReferences.Any(h =>
                md.GetString(md.GetAssemblyReference(h).Name) == "Warudo.Core"
            );
    }
    catch (BadImageFormatException)
    {
        // Native libraries sit in the same folder; they declare nothing.
        return false;
    }
}

static bool DerivesFrom(Type type, string baseName)
{
    for (Type? t = type; t is not null; t = t.BaseType)
    {
        if (t.FullName == baseName)
        {
            return true;
        }
    }

    return false;
}

static IEnumerable<string> ExtractAssemblies(string mod, string into)
{
    byte[] file = File.ReadAllBytes(mod);
    using ZipArchive zip = new(new MemoryStream(file, 12, file.Length - 12));
    ZipArchiveEntry? entry = zip.GetEntry("assemblymodules.dat");
    if (entry is null)
    {
        yield break;
    }

    using BinaryReader reader = new(entry.Open(), Encoding.UTF8);
    uint count = reader.ReadUInt32();
    for (uint i = 0; i < count; i++)
    {
        _ = reader.ReadString();
        _ = reader.ReadString();
        uint flags = reader.ReadUInt32();
        byte[] image = reader.ReadBytes(reader.ReadInt32());
        if ((flags & 2) != 0)
        {
            _ = reader.ReadBytes(reader.ReadInt32());
        }

        string path = Path.Combine(into, $"{Path.GetFileNameWithoutExtension(mod)}.{i}.dll");
        File.WriteAllBytes(path, image);
        yield return path;
    }
}
