using System.Buffers.Binary;
using System.Text;

namespace Ultiviewer.Core.OneNote.Store;

/// <summary>
/// An extended GUID ([MS-ONESTORE] 2.2.1): a GUID plus a number, the identity of every object, object space and
/// revision in a OneNote file.
/// </summary>
public readonly record struct ExGuid(Guid Guid, uint N)
{
    public static ExGuid Nil { get; } = new(Guid.Empty, 0);

    public bool IsNil => Guid == Guid.Empty && N == 0;

    public override string ToString() => $"{{{Guid}}},{N}";
}

/// <summary>
/// One OneNote object after the container is decoded: its type (JCID, [MS-ONE] 2.1.13), its properties with every
/// reference already resolved to an <see cref="ExGuid"/>, and the bytes of a file it carries (pictures, attachments).
/// </summary>
public sealed class StoreObject(uint jcid, PropertySet properties)
{
    public uint Jcid { get; } = jcid;

    public PropertySet Properties { get; } = properties;

    /// <summary>The file's bytes for picture and embedded-file containers; null when absent or stored elsewhere.</summary>
    public byte[]? FileData { get; set; }

    /// <summary>The file extension recorded with the data (".png"), when the container records one.</summary>
    public string? FileExtension { get; set; }

    /// <summary>Why <see cref="FileData"/> is missing, when the file references data it doesn't contain.</summary>
    public string? FileDataProblem { get; set; }
}

/// <summary>A property's value ([MS-ONESTORE] 2.6.7). References are resolved to ids; nested sets stay nested.</summary>
public abstract record PropertyValue;

public sealed record NoValue : PropertyValue;

public sealed record BoolValue(bool Value) : PropertyValue;

/// <summary>A 1-, 2-, 4- or 8-byte value.</summary>
public sealed record NumberValue(ulong Value, int Size) : PropertyValue;

public sealed record BytesValue(byte[] Value) : PropertyValue;

/// <summary>Object, object space or context references, in order.</summary>
public sealed record ReferencesValue(IReadOnlyList<ExGuid> Ids) : PropertyValue;

public sealed record PropertySetsValue(IReadOnlyList<PropertySet> Sets) : PropertyValue;

/// <summary>A set of properties keyed by property id (the low 26 bits of a PropertyID, [MS-ONESTORE] 2.6.6).</summary>
public sealed class PropertySet
{
    private readonly Dictionary<uint, PropertyValue> _values = [];

    public static PropertySet Empty { get; } = new();

    public IReadOnlyDictionary<uint, PropertyValue> Values => _values;

    public void Add(uint id, PropertyValue value) => _values.TryAdd(id & 0x3FFFFFF, value);

    public PropertyValue? Get(uint property) => _values.GetValueOrDefault(property & 0x3FFFFFF);

    public bool Bool(uint property) => Get(property) is BoolValue { Value: true };

    public bool? OptionalBool(uint property) => Get(property) is BoolValue value ? value.Value : null;

    public ulong? Number(uint property) => Get(property) is NumberValue value ? value.Value : null;

    public float? Float(uint property) =>
        Get(property) is NumberValue { Size: 4 } value ? BitConverter.Int32BitsToSingle((int)value.Value) : null;

    public byte[]? Bytes(uint property) => Get(property) is BytesValue value ? value.Value : null;

    /// <summary>A UTF-16 string property (most text in OneNote), without a trailing NUL.</summary>
    public string? String(uint property) =>
        Bytes(property) is { } bytes ? Encoding.Unicode.GetString(bytes, 0, bytes.Length & ~1).TrimEnd('\0') : null;

    public IReadOnlyList<ExGuid> References(uint property) => Get(property) is ReferencesValue value ? value.Ids : [];

    public ExGuid? Reference(uint property) => References(property) is [var first, ..] && !first.IsNil ? first : null;

    public IReadOnlyList<PropertySet> Sets(uint property) => Get(property) switch
    {
        PropertySetsValue sets => sets.Sets,
        _ => [],
    };

    /// <summary>An array of 32-bit numbers stored as bytes (text run indexes).</summary>
    public IReadOnlyList<uint> UInt32Array(uint property)
    {
        if (Bytes(property) is not { } bytes)
        {
            return [];
        }

        var result = new uint[bytes.Length / 4];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4));
        }

        return result;
    }
}

/// <summary>An object space: one page, or a section's own data. Its roots point at the content and the metadata.</summary>
public sealed class ObjectSpace(ExGuid id, IReadOnlyDictionary<ExGuid, StoreObject> objects, ExGuid? contentRoot, ExGuid? metadataRoot)
{
    public ExGuid Id { get; } = id;

    public IReadOnlyDictionary<ExGuid, StoreObject> Objects { get; } = objects;

    public ExGuid? ContentRoot { get; } = contentRoot;

    public ExGuid? MetadataRoot { get; } = metadataRoot;

    public StoreObject? Get(ExGuid? id) => id is { } key && Objects.TryGetValue(key, out var value) ? value : null;
}

/// <summary>A decoded .one (section) or .onetoc2 (notebook table of contents) file.</summary>
public sealed class OneStoreFile(bool isTableOfContents, ObjectSpace root, IReadOnlyDictionary<ExGuid, ObjectSpace> spaces)
{
    public bool IsTableOfContents { get; } = isTableOfContents;

    /// <summary>The root object space: the section's data, or the notebook's table of contents.</summary>
    public ObjectSpace Root { get; } = root;

    public IReadOnlyDictionary<ExGuid, ObjectSpace> Spaces { get; } = spaces;

    public ObjectSpace? Space(ExGuid id) => Spaces.GetValueOrDefault(id);
}

/// <summary>A file that isn't a readable OneNote file, with a message meant for the user.</summary>
public sealed class OneNoteFormatException(string message) : Exception(message);
