using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// Packs one plugin assembly into a .warudo mod that Warudo's mod loader (uMod 2) loads, without the Unity
// editor or the Warudo SDK: a "UMOD" header, then a stored (uncompressed) zip of modinfo.dat, an empty
// shared asset bundle, and assemblymodules.dat carrying the assembly.

Dictionary<string, string> options = Parse(args);
if (!options.TryGetValue("dll", out string? dll) || !options.TryGetValue("out", out string? output))
{
    Console.Error.WriteLine(
        "usage: Warudo.ModPacker --dll <plugin.dll> --out <mod.warudo> [--name ..] [--version ..] [--description ..] [--author ..]"
    );
    Console.Error.WriteLine(
        "       [--warudo-version 2.9.0] [--unity-version 2021.3.45f2] [--umod-version 0.14.3]"
    );
    return 2;
}

byte[] assembly = await File.ReadAllBytesAsync(dll);
byte[] modInfo = ModInfo(
    Option("name", Path.GetFileNameWithoutExtension(dll)),
    Option("version", "0.1.0"),
    Option("description", string.Empty),
    Option("author", string.Empty),
    Option("warudo-version", "2.9.0"),
    Option("unity-version", "2021.3.45f2"),
    Option("umod-version", "0.14.3")
);
byte[] modules = AssemblyModules(assembly);

using MemoryStream zip = new();
using (ZipArchive archive = new(zip, ZipArchiveMode.Create, leaveOpen: true))
{
    await AddAsync(archive, "modinfo.dat", modInfo);
    // The loader reads the shared asset bundle's meta even for a code-only mod: it lists no assets, and the
    // bundle itself stays empty.
    await AddAsync(archive, "sharedassets.bin", []);
    await AddAsync(
        archive,
        "sharedassets.meta",
        BundleMeta(Option("name", Path.GetFileNameWithoutExtension(dll)))
    );
    await AddAsync(archive, "assemblymodules.dat", modules);
}

await using (FileStream file = File.Create(output))
{
    file.Write("UMOD"u8);
    file.Write([0xF3, 0xF0, 0x1C, 0x04, 0x00, 0x00, 0x00, 0x00]);
    zip.Position = 0;
    await zip.CopyToAsync(file);
}

Console.WriteLine($"Packed {Path.GetFileName(dll)} ({assembly.Length} bytes) into {output}");
return 0;

string Option(string name, string fallback) =>
    options.TryGetValue(name, out string? value) ? value : fallback;

static Dictionary<string, string> Parse(string[] args)
{
    Dictionary<string, string> parsed = new(StringComparer.OrdinalIgnoreCase);
    for (int i = 0; i + 1 < args.Length; i += 2)
    {
        if (args[i].StartsWith("--", StringComparison.Ordinal))
        {
            parsed[args[i][2..]] = args[i + 1];
        }
    }

    return parsed;
}

// modinfo.dat: "UMOD", the build time as a Windows FILETIME, three format numbers, then length-prefixed strings.
static byte[] ModInfo(
    string name,
    string version,
    string description,
    string author,
    string warudoVersion,
    string unityVersion,
    string umodVersion
)
{
    using MemoryStream stream = new();
    using BinaryWriter writer = new(stream, Encoding.UTF8);
    writer.Write("UMOD"u8);
    writer.Write(DateTime.UtcNow.ToFileTimeUtc());
    writer.Write(3);
    writer.Write(4);
    writer.Write(3);
    // BinaryWriter.Write(string) writes the 7-bit encoded length prefix the loader reads.
    writer.Write(warudoVersion);
    writer.Write(unityVersion);
    writer.Write(umodVersion);
    writer.Write(name);
    writer.Write(version);
    writer.Write(description);
    writer.Write(author);
    writer.Write((byte)0);
    writer.Write((byte)0);
    writer.Flush();
    return stream.ToArray();
}

// sharedassets.meta: the bundle's root folder, then the count of asset paths and each path.
static byte[] BundleMeta(string name)
{
    using MemoryStream stream = new();
    using BinaryWriter writer = new(stream, Encoding.UTF8);
    writer.Write("Assets/" + string.Concat(name.Where(char.IsLetterOrDigit)));
    writer.Write(0);
    writer.Flush();
    return stream.ToArray();
}

// assemblymodules.dat: the module count, then per module its name, origin, flags, a size hint from the PE
// optional header (code plus initialized data plus headers), and the assembly bytes.
static byte[] AssemblyModules(byte[] assembly)
{
    int optionalHeader = BinaryPrimitives.ReadInt32LittleEndian(assembly.AsSpan(0x3C)) + 0x18;
    uint sizeHint = 0;
    foreach (int offset in (ReadOnlySpan<int>)[0x04, 0x08, 0x3C])
    {
        sizeHint += BinaryPrimitives.ReadUInt32LittleEndian(
            assembly.AsSpan(optionalHeader + offset)
        );
    }

    using MemoryStream stream = new();
    using BinaryWriter writer = new(stream, Encoding.UTF8);
    writer.Write(1u);
    writer.Write($"umod-compiled-{Guid.NewGuid()}");
    writer.Write("<Unknown>");
    writer.Write(1u);
    writer.Write(sizeHint);
    writer.Write(assembly);
    writer.Flush();
    return stream.ToArray();
}

static async Task AddAsync(ZipArchive archive, string name, byte[] content)
{
    ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.NoCompression);
    await using Stream stream = await entry.OpenAsync();
    await stream.WriteAsync(content);
}
