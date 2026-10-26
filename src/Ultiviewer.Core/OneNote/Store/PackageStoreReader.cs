using System.Buffers.Binary;

namespace Ultiviewer.Core.OneNote.Store;

/// <summary>
/// Reads the packaged revision store that OneNote for the web and OneDrive write ([MS-ONESTORE] 2.8 with the
/// [MS-FSSHTTPB] storage it wraps) into the same <see cref="OneStoreFile"/> model as <see cref="RevisionStoreReader"/>.
/// The file is a tree of stream objects; its data elements are a storage index (cells → cell manifests,
/// revision ids → revision manifests), a storage manifest (the header and data root cells, and whether this is
/// a section or a table of contents), revision manifests (a revision's roots, object groups and base revision),
/// object groups (objects in partitions: 4 = JCID, 1 = property set with its references, 2 = file data) and
/// object data blobs (file bytes). Each cell is an object space, named by its cell id's second half.
/// </summary>
public sealed class PackageStoreReader
{
    private static readonly Guid SectionSchema = new("1F937CB4-B26F-445F-B9F8-17E20160E461");
    private static readonly ExGuid HeaderRoot = new(new Guid("1A5A319C-C26B-41AA-B9C5-9BD8C44E07D4"), 1);
    private static readonly ExGuid DataRoot = new(new Guid("84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073"), 2);
    private static readonly Guid RevisionRoles = new("4A3717F8-1C14-49E7-9526-81D942DE1741");
    private const uint RoleContent = 1;
    private const uint RoleMetadata = 2;
    private const uint PictureFileExtension = 0x24003424;

    // Stream object types ([MS-FSSHTTPB] 2.2.1.5.1).
    private const int DataElement = 0x01, ObjectDataBlob = 0x02, ObjectGroupDataExcluded = 0x03, ObjectGroupDataBlob = 0x05;
    private const int RevisionManifestRoot = 0x0A, CellManifest = 0x0B, StorageManifest = 0x0C;
    private const int StorageIndexRevisionMapping = 0x0D, StorageIndexCellMapping = 0x0E, StorageIndexManifestMapping = 0x11;
    private const int DataElementPackage = 0x15, ObjectGroupDataObject = 0x16, ObjectGroupObject = 0x18;
    private const int RevisionManifestGroupReference = 0x19, RevisionManifest = 0x1A, ObjectGroupBlobReference = 0x1C;
    private const int ObjectGroupDeclaration = 0x1D, ObjectGroupData = 0x1E, StorageManifestRoot = 0x07;
    private const int DataElementFragment = 0x6A, ObjectGroupMetadata = 0x78, ObjectGroupMetadataBlock = 0x79, OneNotePackaging = 0x7A;

    private readonly byte[] _data;
    private long _at;

    private readonly Dictionary<ExGuid, StorageIndex> _storageIndexes = [];
    private Dictionary<CellId, (ExGuid Id, Serial Serial)> _cellMappings = [];
    private Dictionary<ExGuid, (ExGuid Revision, Serial Serial)> _revisionMappings = [];
    private readonly Dictionary<ExGuid, ExGuid> _cellManifests = [];
    private readonly Dictionary<ExGuid, Manifest> _revisionManifests = [];
    private readonly Dictionary<ExGuid, Group> _groups = [];
    private readonly Dictionary<ExGuid, (long Offset, long Length)> _blobs = [];
    private Guid _schema;
    private readonly Dictionary<ExGuid, CellId> _roots = [];

    private PackageStoreReader(byte[] data, long start)
    {
        _data = data;
        _at = start;
    }

    /// <param name="start">Where the packaging starts: 0, or after the header and transaction log of a desktop-style file.</param>
    public static OneStoreFile Read(byte[] data, long start = 0) => new PackageStoreReader(data, start).ReadFile();

    /// <summary>
    /// Where a file with a desktop header keeps a package instead of a revision store (newer OneDrive downloads):
    /// right after its transaction log, when the header names no legacy version. Null when it doesn't.
    /// </summary>
    public static long? EmbeddedPackageOffset(byte[] data)
    {
        if (data.Length < 1024 || new Guid(data.AsSpan(32, 16)) != Guid.Empty)
        {
            return null;
        }

        var end = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(160)) + BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(168));
        return end < (ulong)data.Length - 64 && new Guid(data.AsSpan((int)end + 48, 16)) == RevisionStoreReader.PackagedFormat ? (long)end : null;
    }

    private OneStoreFile ReadFile()
    {
        // OneStorePackaging ([MS-ONESTORE] 2.8.1): file type, file, legacy version and format GUIDs, padding, then the package.
        _at += 64;
        if (U32() != 0)
        {
            throw Corrupt("the package header is malformed");
        }

        StartObject(OneNotePackaging);
        var storageIndex = ExGuidValue();
        _at += 16; // cell schema
        ReadPackage();

        // A package can hold older storage indexes too; the header names the current one.
        var index = _storageIndexes.GetValueOrDefault(storageIndex) ?? _storageIndexes.Values.FirstOrDefault()
            ?? throw Corrupt("the package has no storage index");
        (_cellMappings, _revisionMappings) = (index.Cells, index.Revisions);

        if (!_roots.TryGetValue(DataRoot, out var dataRootCell))
        {
            throw Corrupt("the package has no data root");
        }

        var root = ReadSpace(dataRootCell) ?? new ObjectSpace(dataRootCell.Key, new Dictionary<ExGuid, StoreObject>(), null, null);
        var spaces = new Dictionary<ExGuid, ObjectSpace> { [root.Id] = root };
        _roots.TryGetValue(HeaderRoot, out var headerCell);
        foreach (var cell in _cellMappings.Keys)
        {
            if (cell != dataRootCell && cell != headerCell && ReadSpace(cell) is { } space)
            {
                spaces[space.Id] = space;
            }
        }

        return new OneStoreFile(_schema != SectionSchema, root, spaces);
    }

    // ---- Data elements ([MS-FSSHTTPB] 2.2.1.12) ----

    private void ReadPackage()
    {
        StartObject(DataElementPackage);
        if (U8() != 0)
        {
            throw Corrupt("the data element package is malformed");
        }

        while (!AtEnd8(DataElementPackage))
        {
            StartObject(DataElement);
            var id = ExGuidValue();
            ReadSerial();
            switch (CompactU64())
            {
                case 0x01: _storageIndexes[id] = ReadStorageIndex(); break;
                case 0x02: ReadStorageManifest(); break;
                case 0x03:
                    StartObject(CellManifest);
                    _cellManifests[id] = ExGuidValue();
                    EndObject8(DataElement);
                    break;
                case 0x04: _revisionManifests[id] = ReadRevisionManifest(); break;
                case 0x05: _groups[id] = ReadObjectGroup(); break;
                case 0x06: // a fragment of a data element that didn't fit; the import doesn't need them
                    StartObject(DataElementFragment);
                    ExGuidValue();
                    var size = CompactU64();
                    CompactU64();
                    CompactU64();
                    Skip(size);
                    break;
                case 0x0A:
                {
                    StartObject(ObjectDataBlob);
                    var length = (long)CompactU64();
                    Need(length);
                    _blobs[id] = (_at, length);
                    _at += length;
                    EndObject8(DataElement);
                    break;
                }

                case var other:
                    throw Corrupt($"unknown data element type {other:X}");
            }
        }

        EndObject8(DataElementPackage);
    }

    private StorageIndex ReadStorageIndex()
    {
        var index = new StorageIndex();
        while (!AtEnd8(DataElement))
        {
            switch (StartObject16())
            {
                case StorageIndexManifestMapping:
                    ExGuidValue();
                    ReadSerial();
                    break;
                case StorageIndexCellMapping:
                    index.Cells[CellIdValue()] = (ExGuidValue(), ReadSerial());
                    break;
                case StorageIndexRevisionMapping:
                    index.Revisions[ExGuidValue()] = (ExGuidValue(), ReadSerial());
                    break;
                case var other:
                    throw Corrupt($"unexpected object {other:X} in the storage index");
            }
        }

        EndObject8(DataElement);
        return index;
    }

    private void ReadStorageManifest()
    {
        StartObject(StorageManifest);
        _schema = GuidValue();
        while (!AtEnd8(DataElement))
        {
            StartObject(StorageManifestRoot);
            var root = ExGuidValue();
            _roots[root] = CellIdValue();
        }

        EndObject8(DataElement);
    }

    private Manifest ReadRevisionManifest()
    {
        StartObject(RevisionManifest);
        var manifest = new Manifest(ExGuidValue(), ExGuidValue());
        while (!AtEnd8(DataElement))
        {
            switch (StartObject16())
            {
                case RevisionManifestRoot:
                    manifest.Roots.Add((ExGuidValue(), ExGuidValue()));
                    break;
                case RevisionManifestGroupReference:
                    manifest.Groups.Add(ExGuidValue());
                    break;
                case var other:
                    throw Corrupt($"unexpected object {other:X} in a revision manifest");
            }
        }

        EndObject8(DataElement);
        return manifest;
    }

    private Group ReadObjectGroup()
    {
        var group = new Group();
        StartObject(ObjectGroupDeclaration);
        while (!AtEnd8(ObjectGroupDeclaration))
        {
            switch (StartObjectAny())
            {
                case ObjectGroupObject:
                    group.Declarations.Add((ExGuidValue(), CompactU64()));
                    CompactU64(); // data size
                    CompactU64(); // object reference count
                    CompactU64(); // cell reference count
                    break;
                case ObjectGroupDataBlob:
                    var id = ExGuidValue();
                    ExGuidValue(); // blob id
                    group.Declarations.Add((id, CompactU64()));
                    CompactU64();
                    CompactU64();
                    break;
                case var other:
                    throw Corrupt($"unexpected object {other:X} in an object group declaration");
            }
        }

        EndObject8(ObjectGroupDeclaration);

        var next = StartObjectAny();
        if (next == ObjectGroupMetadataBlock)
        {
            while (!AtEnd8(ObjectGroupMetadataBlock))
            {
                StartObject(ObjectGroupMetadata);
                CompactU64(); // change frequency
            }

            EndObject8(ObjectGroupMetadataBlock);
            next = StartObjectAny();
        }

        if (next != ObjectGroupData)
        {
            throw Corrupt($"unexpected object {next:X} in an object group");
        }

        while (!AtEnd8(ObjectGroupData))
        {
            switch (StartObjectAny())
            {
                case ObjectGroupDataExcluded:
                    ExGuidArray();
                    CellIdArray();
                    CompactU64();
                    group.Data.Add(new GroupData(null, [], [], null));
                    break;
                case ObjectGroupDataObject:
                {
                    var references = ExGuidArray();
                    var cells = CellIdArray();
                    var length = (long)CompactU64();
                    Need(length);
                    group.Data.Add(new GroupData((_at, length), references, cells, null));
                    _at += length;
                    break;
                }

                case ObjectGroupBlobReference:
                {
                    var references = ExGuidArray();
                    var cells = CellIdArray();
                    group.Data.Add(new GroupData(null, references, cells, ExGuidValue()));
                    break;
                }

                case var other:
                    throw Corrupt($"unexpected object {other:X} in object group data");
            }
        }

        EndObject8(ObjectGroupData);
        EndObject8(DataElement);
        return group;
    }

    // ---- Object spaces ----

    /// <summary>A cell's object space: its newest revision's roots and objects, then its base revisions' other objects.</summary>
    private ObjectSpace? ReadSpace(CellId cell)
    {
        if (!_cellMappings.TryGetValue(cell, out var mapping) || mapping.Id.IsNil)
        {
            return null;
        }

        var revision = ResolveCellRevision(mapping.Id) ?? RevisionBySerial(mapping.Serial);
        var objects = new Dictionary<ExGuid, StoreObject>();
        ExGuid? content = null, metadata = null;
        var visited = new HashSet<ExGuid>();
        while (revision is { } id && visited.Add(id) && _revisionManifests.TryGetValue(id, out var manifest))
        {
            foreach (var (role, objectId) in manifest.Roots)
            {
                if (role.Guid == RevisionRoles && role.N == RoleContent)
                {
                    content ??= objectId;
                }
                else if (role.Guid == RevisionRoles && role.N == RoleMetadata)
                {
                    metadata ??= objectId;
                }
            }

            foreach (var groupId in manifest.Groups)
            {
                if (_groups.TryGetValue(groupId, out var group))
                {
                    ReadObjects(group, cell.Space, objects);
                }
            }

            revision = manifest.Base.IsNil ? null : ResolveRevision(manifest.Base);
        }

        return new ObjectSpace(cell.Key, objects, content, metadata);
    }

    private ExGuid? ResolveRevision(ExGuid id) =>
        _revisionMappings.TryGetValue(id, out var mapping) ? mapping.Revision : _revisionManifests.ContainsKey(id) ? id : null;

    private ExGuid? ResolveCellRevision(ExGuid cellManifest) =>
        (_cellManifests.TryGetValue(cellManifest, out var revision) ? ResolveRevision(revision) : null) ?? ResolveRevision(cellManifest);

    private ExGuid? RevisionBySerial(Serial serial) =>
        _revisionMappings.Values.Where(m => m.Serial == serial).Select(m => (ExGuid?)m.Revision).FirstOrDefault();

    /// <summary>The group's objects not already known from a newer revision.</summary>
    private void ReadObjects(Group group, ExGuid space, Dictionary<ExGuid, StoreObject> objects)
    {
        var parts = new Dictionary<(ExGuid Id, ulong Partition), GroupData>();
        for (var k = 0; k < group.Declarations.Count && k < group.Data.Count; k++)
        {
            parts.TryAdd(group.Declarations[k], group.Data[k]);
        }

        foreach (var (id, _) in group.Declarations)
        {
            if (objects.ContainsKey(id)
                || !parts.TryGetValue((id, 4), out var jcid) || jcid.Bytes is not { } jcidBytes || jcidBytes.Length < 4
                || !parts.TryGetValue((id, 1), out var body) || body.Bytes is not { } bodyBytes)
            {
                continue;
            }

            // References of the property set: objects in order; cells in this space are contexts, others spaces.
            var contexts = body.Cells.Where(c => c.Space == space).Select(c => c.Context).ToList();
            var spaces = body.Cells.Where(c => c.Space != space).Select(c => c.Key).ToList();
            var properties = PropertySetReader.Read(_data, bodyBytes.Offset, bodyBytes.Length, (stream, ids) => stream switch
            {
                PropertySetReader.IdStream.Objects => Positional(ids, body.References),
                PropertySetReader.IdStream.Spaces => Positional(ids, spaces),
                _ => Positional(ids, contexts),
            });
            var value = new StoreObject(BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)jcidBytes.Offset)), properties);
            if (parts.TryGetValue((id, 2), out var file) && file.Blob is { } blob)
            {
                if (_blobs.TryGetValue(blob, out var bytes))
                {
                    value.FileData = _data.AsSpan((int)bytes.Offset, (int)bytes.Length).ToArray();
                    value.FileExtension = properties.String(PictureFileExtension);
                }
                else
                {
                    value.FileDataProblem = "its data is missing from the section file";
                }
            }

            objects[id] = value;
        }
    }

    /// <summary>
    /// The k-th compact ID of a stream is the k-th reference of the object. Beyond those, a compact ID that
    /// appeared earlier means the same id again ([MS-ONESTORE] 2.7.8).
    /// </summary>
    private static List<ExGuid> Positional(uint[] ids, IReadOnlyList<ExGuid> references)
    {
        var resolved = new List<ExGuid>(ids.Length);
        var known = new Dictionary<uint, ExGuid>();
        for (var k = 0; k < ids.Length; k++)
        {
            if (k < references.Count)
            {
                known.TryAdd(ids[k], references[k]);
                resolved.Add(references[k]);
            }
            else
            {
                resolved.Add(known.GetValueOrDefault(ids[k], ExGuid.Nil));
            }
        }

        return resolved;
    }

    // ---- Stream objects ([MS-FSSHTTPB] 2.2.1.5) ----

    /// <summary>A 16- or 32-bit stream object start; returns its type.</summary>
    private int StartObjectAny()
    {
        Need(1);
        return (_data[_at] & 0b11) switch
        {
            0 => StartObject16(),
            2 => StartObject32(),
            _ => throw Corrupt("a stream object header is malformed"),
        };
    }

    private int StartObject16()
    {
        var header = U16();
        if ((header & 0b11) != 0)
        {
            throw Corrupt("a stream object header is malformed");
        }

        return (header >> 3) & 0x3F;
    }

    private int StartObject32()
    {
        var header = U32();
        if ((header & 0b11) != 2)
        {
            throw Corrupt("a stream object header is malformed");
        }

        if (header >> 17 == 0x7FFF)
        {
            CompactU64(); // a large length; the content is read by structure, not length
        }

        return (int)((header >> 3) & 0x3FFF);
    }

    private void StartObject(int type)
    {
        var actual = StartObjectAny();
        if (actual != type)
        {
            throw Corrupt($"expected stream object {type:X}, found {actual:X}");
        }
    }

    private bool AtEnd8(int type)
    {
        Need(1);
        var b = _data[_at];
        return (b & 0b11) == 1 && b >> 2 == type;
    }

    private void EndObject8(int type)
    {
        if (!AtEnd8(type))
        {
            throw Corrupt($"stream object {type:X} doesn't end where expected");
        }

        _at++;
    }

    // ---- Basic types ([MS-FSSHTTPB] 2.2.1) ----

    private ulong CompactU64()
    {
        Need(1);
        var first = _data[_at];
        if (first == 0)
        {
            _at++;
            return 0;
        }

        // The number of trailing zero bits says how many bytes follow (up to 7); 0x80 is a plain 64-bit value.
        if (first == 0x80)
        {
            _at++;
            return U64();
        }

        var bytes = System.Numerics.BitOperations.TrailingZeroCount(first) + 1;
        Need(bytes);
        ulong value = 0;
        for (var k = bytes - 1; k >= 0; k--)
        {
            value = value << 8 | _data[_at + k];
        }

        _at += bytes;
        return value >> bytes;
    }

    private ExGuid ExGuidValue()
    {
        var first = U8();
        if (first == 0)
        {
            return ExGuid.Nil;
        }

        uint value;
        if ((first & 0b111) == 4)
        {
            value = (uint)(first >> 3);
        }
        else if ((first & 0b11_1111) == 32)
        {
            value = (uint)(U8() << 2 | first >> 6);
        }
        else if ((first & 0b111_1111) == 64)
        {
            value = (uint)(U16() << 1 | first >> 7);
        }
        else if (first == 128)
        {
            value = U32();
        }
        else
        {
            throw Corrupt("an extended GUID is malformed");
        }

        return new ExGuid(GuidValue(), value);
    }

    private List<ExGuid> ExGuidArray()
    {
        var count = CompactU64();
        var values = new List<ExGuid>((int)Math.Min(count, 10_000));
        for (var k = 0UL; k < count; k++)
        {
            values.Add(ExGuidValue());
        }

        return values;
    }

    private CellId CellIdValue() => new(ExGuidValue(), ExGuidValue());

    private List<CellId> CellIdArray()
    {
        var count = CompactU64();
        var values = new List<CellId>((int)Math.Min(count, 10_000));
        for (var k = 0UL; k < count; k++)
        {
            values.Add(CellIdValue());
        }

        return values;
    }

    private Serial ReadSerial()
    {
        if (U8() == 0)
        {
            return default;
        }

        return new Serial(GuidValue(), U64());
    }

    private Guid GuidValue()
    {
        Need(16);
        var value = new Guid(_data.AsSpan((int)_at, 16));
        _at += 16;
        return value;
    }

    private byte U8()
    {
        Need(1);
        return _data[_at++];
    }

    private ushort U16()
    {
        Need(2);
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan((int)_at));
        _at += 2;
        return value;
    }

    private uint U32()
    {
        Need(4);
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)_at));
        _at += 4;
        return value;
    }

    private ulong U64()
    {
        Need(8);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan((int)_at));
        _at += 8;
        return value;
    }

    private void Skip(ulong length)
    {
        Need((long)Math.Min(length, long.MaxValue));
        _at += (long)length;
    }

    private void Need(long length)
    {
        if (length < 0 || _at + length > _data.Length)
        {
            throw Corrupt("the package ends early");
        }
    }

    private static OneNoteFormatException Corrupt(string detail) => PropertySetReader.Corrupt(detail);

    /// <summary>
    /// A cell: an object space within a context ([MS-FSSHTTPB] 2.2.1.10). One space can be in several contexts
    /// (a page's content and its version history), so the model names each cell by both, XORed together.
    /// </summary>
    private readonly record struct CellId(ExGuid Context, ExGuid Space)
    {
        public ExGuid Key
        {
            get
            {
                Span<byte> bytes = stackalloc byte[16], other = stackalloc byte[16];
                Space.Guid.TryWriteBytes(bytes);
                Context.Guid.TryWriteBytes(other);
                for (var k = 0; k < 16; k++)
                {
                    bytes[k] ^= other[k];
                }

                return new ExGuid(new Guid(bytes), Space.N ^ Context.N);
            }
        }
    }

    private readonly record struct Serial(Guid Guid, ulong Value);

    /// <summary>Cells → cell manifests, revision ids → revision manifests ([MS-FSSHTTPB] 2.2.1.12.2).</summary>
    private sealed class StorageIndex
    {
        public Dictionary<CellId, (ExGuid Id, Serial Serial)> Cells { get; } = [];

        public Dictionary<ExGuid, (ExGuid Revision, Serial Serial)> Revisions { get; } = [];
    }

    private sealed record Manifest(ExGuid Id, ExGuid Base)
    {
        public List<(ExGuid Role, ExGuid Object)> Roots { get; } = [];

        public List<ExGuid> Groups { get; } = [];
    }

    private sealed class Group
    {
        /// <summary>Object id and partition, in the order of <see cref="Data"/>.</summary>
        public List<(ExGuid Id, ulong Partition)> Declarations { get; } = [];

        public List<GroupData> Data { get; } = [];
    }

    /// <param name="Bytes">Where the object's data is in the file; null for excluded data and blob references.</param>
    private sealed record GroupData((long Offset, long Length)? Bytes, List<ExGuid> References, List<CellId> Cells, ExGuid? Blob);
}
