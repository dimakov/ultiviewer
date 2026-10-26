using System.Buffers.Binary;
using System.Text;

namespace Ultiviewer.Core.OneNote.Store;

/// <summary>
/// Reads the OneNote desktop file format, the "revision store" of [MS-ONESTORE] section 2. OneNote 2010 and later
/// write it for .one sections and .onetoc2 notebook tables of contents, and .onepkg packages contain such files.
/// </summary>
/// <remarks>
/// The file is a set of <i>file node lists</i> (chains of fragments) linked from the header. The root list names
/// the object spaces (one per page, plus the section's own) and the file data store (embedded pictures and files).
/// Each object space has revisions; a revision lists object groups whose objects are declared with a JCID (type)
/// and a property set. Revisions may build on a parent revision, and role labels say which revision is current.
/// Only committed nodes are read: the transaction log gives each list's node count.
/// </remarks>
public sealed class RevisionStoreReader
{
    public static readonly Guid DesktopFormat = new("109ADD3F-911B-49F5-A5D0-1791EDC8AED8");
    public static readonly Guid PackagedFormat = new("638DE92F-A6D4-4BC1-9A36-B3FC2511A5B7");
    private static readonly Guid TocFileType = new("43FF2FA1-EFD9-4C76-9EE2-10EA5722765F");
    private static readonly Guid FileDataHeader = new("BDE316E7-2665-4511-A4C4-8D4D0B7A9EAC");

    private const uint RoleContent = 1;
    private const uint RoleMetadata = 2;

    private readonly byte[] _data;
    private readonly string? _folder;
    private readonly Dictionary<uint, int> _nodeCounts = [];
    private readonly Dictionary<Guid, (long Offset, long Length)> _fileData = [];

    private RevisionStoreReader(byte[] data, string? folder)
    {
        _data = data;
        _folder = folder;
    }

    /// <summary>Reads a desktop .one or .onetoc2 file; <paramref name="folder"/> resolves attachments stored beside it.</summary>
    public static OneStoreFile Read(byte[] data, string? folder = null) => new RevisionStoreReader(data, folder).ReadFile();

    /// <summary>The container format named in the header ([MS-ONESTORE] 2.3.1 guidFileFormat), or null when too short.</summary>
    public static Guid? FormatOf(ReadOnlySpan<byte> data) => data.Length >= 64 ? new Guid(data.Slice(48, 16)) : null;

    private OneStoreFile ReadFile()
    {
        if (FormatOf(_data) is not { } format)
        {
            throw new OneNoteFormatException("The file is too short to be a OneNote file.");
        }

        if (format != DesktopFormat)
        {
            throw new OneNoteFormatException("The file is not in the OneNote desktop format.");
        }

        var isToc = Guid(0) == TocFileType;
        ReadTransactionLog(Fcr64x32(160));
        var root = Fcr64x32(172);
        if (root.IsNilOrZero)
        {
            throw new OneNoteFormatException("The file has no content (empty root list).");
        }

        var rootNodes = ReadList(root.Stp, root.Cb);
        var rootSpace = ExGuid.Nil;
        var spaceNodes = new List<(ExGuid Id, Node Node)>();
        foreach (var node in rootNodes)
        {
            switch (node.Id)
            {
                case 0x004: // ObjectSpaceManifestRootFND
                    rootSpace = ExGuidAt(node.Data);
                    break;
                case 0x008: // ObjectSpaceManifestListReferenceFND
                    spaceNodes.Add((ExGuidAt(node.Data), node));
                    break;
                case 0x090: // FileDataStoreListReferenceFND
                    ReadFileDataStore(node);
                    break;
            }
        }

        var spaces = new Dictionary<ExGuid, ObjectSpace>();
        foreach (var (id, node) in spaceNodes)
        {
            if (ReadObjectSpace(id, node) is { } space)
            {
                spaces[id] = space;
            }
        }

        // A space without revisions (an empty table of contents) reads as empty.
        var rootObjectSpace = spaces.GetValueOrDefault(rootSpace) ?? new ObjectSpace(rootSpace, new Dictionary<ExGuid, StoreObject>(), null, null);

        return new OneStoreFile(isToc, rootObjectSpace, spaces);
    }

    // ---- Transaction log ([MS-ONESTORE] 2.3.3): the committed node count of every file node list ----

    /// <remarks>
    /// Every entry of every fragment counts, and a list's largest count wins. Newer files log transactions past
    /// the header's cTransactionsInLog, so stopping there would cut lists short.
    /// </remarks>
    private void ReadTransactionLog(ChunkReference first)
    {
        var reference = first;
        var guard = 0;
        while (!reference.IsNilOrZero && guard++ < 100_000)
        {
            Need(reference.Stp, reference.Cb);
            var entries = (int)((reference.Cb - 12) / 8);
            for (var i = 0; i < entries; i++)
            {
                var source = U32(reference.Stp + 8 * i);
                var count = (int)Math.Min(U32(reference.Stp + 8 * i + 4), int.MaxValue);
                if (source != 1 && count > _nodeCounts.GetValueOrDefault(source)) // 1 = the sentinel ending a transaction
                {
                    _nodeCounts[source] = count;
                }
            }

            reference = Fcr64x32(reference.Stp + 8L * entries);
        }
    }

    // ---- File node lists ([MS-ONESTORE] 2.4) ----

    /// <summary>A file node: its type, where its own data starts (after any chunk reference), and that reference.</summary>
    private readonly record struct Node(uint Id, long Data, ChunkReference Reference);

    private readonly record struct ChunkReference(long Stp, long Cb, bool IsNil)
    {
        public bool IsNilOrZero => IsNil || Stp == 0 && Cb == 0;
    }

    private List<Node> ReadList(long stp, long cb)
    {
        var nodes = new List<Node>();
        var remaining = -1;
        var fragments = 0;
        while (true)
        {
            if (++fragments > 100_000)
            {
                throw Corrupt("a file node list doesn't end");
            }

            Need(stp, cb);
            if (cb < 36 || U64(stp) != 0xA4567AB1F5F7F4C4)
            {
                throw Corrupt("a file node list fragment has no header");
            }

            if (remaining < 0)
            {
                remaining = _nodeCounts.TryGetValue(U32(stp + 8), out var count) ? count : int.MaxValue;
            }

            var position = stp + 16;
            var end = stp + cb - 20;
            while (end - position >= 4 && remaining > 0)
            {
                var header = U32(position);
                var id = header & 0x3FF;
                if (id == 0x0FF)
                {
                    break; // ChunkTerminatorFND: the rest of this fragment is unused
                }

                if (id == 0)
                {
                    position += 4; // padding
                    continue;
                }

                var size = (int)((header >> 10) & 0x1FFF);
                if (size < 4 || position + size > end)
                {
                    throw Corrupt($"file node {id:X} has an invalid size {size}");
                }

                var data = position + 4;
                var reference = default(ChunkReference);
                var baseType = (header >> 27) & 0xF;
                if (baseType is 1 or 2)
                {
                    reference = NodeReference(ref data, (header >> 23) & 3, (header >> 25) & 3);
                }

                nodes.Add(new Node(id, data, reference));
                remaining--;
                position += size;
            }

            var next = Fcr64x32(stp + cb - 20);
            if (U64(stp + cb - 8) != 0x8BC215C38233BA4B)
            {
                throw Corrupt("a file node list fragment has no footer");
            }

            if (next.IsNilOrZero || remaining == 0)
            {
                return nodes;
            }

            (stp, cb) = (next.Stp, next.Cb);
        }
    }

    /// <summary>A FileNodeChunkReference ([MS-ONESTORE] 2.2.4.2) in one of its compressed encodings.</summary>
    private ChunkReference NodeReference(ref long position, uint stpFormat, uint cbFormat)
    {
        ulong stp;
        bool stpNil;
        switch (stpFormat)
        {
            case 0: stp = U64(position); stpNil = stp == ulong.MaxValue; position += 8; break;
            case 1: stp = U32(position); stpNil = stp == uint.MaxValue; position += 4; break;
            case 2: stp = U16(position); stpNil = stp == ushort.MaxValue; stp *= 8; position += 2; break;
            default: stp = U32(position); stpNil = stp == uint.MaxValue; stp *= 8; position += 4; break;
        }

        ulong cb;
        switch (cbFormat)
        {
            case 0: cb = U32(position); position += 4; break;
            case 1: cb = U64(position); position += 8; break;
            case 2: cb = _data[Check(position, 1)] * 8UL; position += 1; break;
            default: cb = U16(position) * 8UL; position += 2; break;
        }

        return new ChunkReference((long)Math.Min(stp, long.MaxValue), (long)Math.Min(cb, long.MaxValue), stpNil && cb == 0);
    }

    // ---- File data store ([MS-ONESTORE] 2.5.21, 2.6.13): pictures and files embedded in the section ----

    private void ReadFileDataStore(Node store)
    {
        foreach (var node in ReadList(store.Reference.Stp, store.Reference.Cb))
        {
            if (node.Id != 0x094 || node.Reference.IsNilOrZero)
            {
                continue;
            }

            var at = node.Reference.Stp;
            Need(at, 36);
            if (Guid(at) != FileDataHeader)
            {
                continue;
            }

            var length = (long)U64(at + 16);
            Need(at + 36, length);
            _fileData[Guid(node.Data)] = (at + 36, length);
        }
    }

    // ---- Object spaces and revisions ([MS-ONESTORE] 2.1.4 – 2.1.9) ----

    private sealed record Revision(
        ExGuid Id, ExGuid Parent, ExGuid Context, uint Role,
        Dictionary<uint, ExGuid> Roots, Dictionary<ExGuid, StoreObject> Objects, Dictionary<uint, Guid> IdMap);

    private ObjectSpace? ReadObjectSpace(ExGuid id, Node reference)
    {
        // The space's manifest list names its revision manifest lists; the last one is current.
        var manifest = ReadList(reference.Reference.Stp, reference.Reference.Cb);
        var lastList = manifest.LastOrDefault(n => n.Id == 0x010);
        if (lastList.Id == 0)
        {
            return null;
        }

        var nodes = ReadList(lastList.Reference.Stp, lastList.Reference.Cb);
        var revisions = new Dictionary<ExGuid, Revision>();
        var labels = new Dictionary<(ExGuid Context, uint Role), ExGuid>();
        var i = 0;
        while (i < nodes.Count)
        {
            var node = nodes[i];
            switch (node.Id)
            {
                case 0x01B or 0x01E or 0x01F: // RevisionManifestStart4/6/7FND
                    var revision = ReadRevision(nodes, ref i, revisions);
                    revisions[revision.Id] = revision;
                    labels[(revision.Context, revision.Role)] = revision.Id;
                    break;
                case 0x05C: // RevisionRoleDeclarationFND
                    labels[(ExGuid.Nil, U32(node.Data + 20))] = ExGuidAt(node.Data);
                    i++;
                    break;
                case 0x05D: // RevisionRoleAndContextDeclarationFND
                    labels[(Context(ExGuidAt(node.Data + 24)), U32(node.Data + 20))] = ExGuidAt(node.Data);
                    i++;
                    break;
                default:
                    i++;
                    break;
            }
        }

        // The current content: the revision labelled with the default context and role 1, plus its ancestors.
        if (!labels.TryGetValue((ExGuid.Nil, RoleContent), out var active) || !revisions.ContainsKey(active))
        {
            if (revisions.Count == 0)
            {
                return null;
            }

            active = revisions.Values.Last().Id;
        }

        var chain = new List<Revision>();
        for (var current = active; !current.IsNil && revisions.TryGetValue(current, out var revision) && chain.Count < 10_000; current = revision.Parent)
        {
            chain.Add(revision);
        }

        var roots = new Dictionary<uint, ExGuid>();
        var objects = new Dictionary<ExGuid, StoreObject>();
        for (var r = chain.Count - 1; r >= 0; r--)
        {
            foreach (var (role, root) in chain[r].Roots)
            {
                roots[role] = root;
            }

            foreach (var (objectId, value) in chain[r].Objects)
            {
                objects[objectId] = value;
            }
        }

        return new ObjectSpace(id, objects, roots.TryGetValue(RoleContent, out var content) ? content : null,
            roots.TryGetValue(RoleMetadata, out var metadata) ? metadata : null);
    }

    private static ExGuid Context(ExGuid context) => context.IsNil ? ExGuid.Nil : context;

    private Revision ReadRevision(List<Node> nodes, ref int i, Dictionary<ExGuid, Revision> revisions)
    {
        var start = nodes[i++];
        var id = ExGuidAt(start.Data);
        var parentId = ExGuidAt(start.Data + 20);
        var (role, context) = start.Id switch
        {
            0x01B => (U32(start.Data + 48), ExGuid.Nil),            // rid, ridDependent, timeCreation, RevisionRole
            0x01F => (U32(start.Data + 40), Context(ExGuidAt(start.Data + 46))),
            _ => (U32(start.Data + 40), ExGuid.Nil),
        };

        var parent = parentId.IsNil ? null : revisions.GetValueOrDefault(parentId);
        var idMap = parent is null ? new Dictionary<uint, Guid>() : new Dictionary<uint, Guid>(parent.IdMap);
        var objects = new Dictionary<ExGuid, StoreObject>();
        var roots = new Dictionary<uint, ExGuid>();
        Dictionary<uint, Guid>? lastTable = null;

        while (i < nodes.Count && nodes[i].Id != 0x01C)
        {
            var node = nodes[i];
            switch (node.Id)
            {
                case 0x0B0: // ObjectGroupListReferenceFND
                    ReadObjectGroup(ReadList(node.Reference.Stp, node.Reference.Cb), objects);
                    i++;
                    break;
                case 0x0B4: // an object group written inline
                {
                    var group = new List<Node>();
                    while (i < nodes.Count && nodes[i].Id != 0x0B8)
                    {
                        group.Add(nodes[i++]);
                    }

                    i++;
                    ReadObjectGroup(group, objects);
                    break;
                }

                case 0x021 or 0x022: // a global ID table, then the objects declared with it
                    var table = ReadIdTable(nodes, ref i, parent?.IdMap);
                    foreach (var (index, guid) in table)
                    {
                        idMap[index] = guid;
                    }

                    lastTable = table;
                    while (i < nodes.Count)
                    {
                        var item = nodes[i];
                        if (Declare(item, table) is { } declared)
                        {
                            objects[declared.Id] = declared.Object;
                        }
                        else if (item.Id is 0x041 or 0x042)
                        {
                            ApplyObjectRevision(item, table, objects, parent, revisions);
                        }
                        else if (item.Id is not (0x08C or 0x084))
                        {
                            break;
                        }

                        i++;
                    }

                    break;
                case 0x05A: // RootObjectReference3FND: ExGuid, role
                    roots[U32(node.Data + 20)] = ExGuidAt(node.Data);
                    i++;
                    break;
                case 0x059: // RootObjectReference2FNDX: CompactID, role
                    roots[U32(node.Data + 4)] = Resolve(U32(node.Data), lastTable ?? idMap);
                    i++;
                    break;
                default:
                    i++;
                    break;
            }
        }

        i++; // RevisionManifestEndFND
        return new Revision(id, parentId, context, role, roots, objects, idMap);
    }

    private void ReadObjectGroup(List<Node> nodes, Dictionary<ExGuid, StoreObject> objects)
    {
        var i = nodes.FindIndex(n => n.Id is 0x021 or 0x022);
        if (i < 0)
        {
            return;
        }

        var table = ReadIdTable(nodes, ref i, null);
        for (; i < nodes.Count && nodes[i].Id != 0x0B8; i++)
        {
            if (Declare(nodes[i], table) is { } declared)
            {
                objects[declared.Id] = declared.Object;
            }
        }
    }

    /// <summary>A global ID table ([MS-ONESTORE] 2.1.3): the GUIDs that compact IDs index.</summary>
    private Dictionary<uint, Guid> ReadIdTable(List<Node> nodes, ref int i, Dictionary<uint, Guid>? parent)
    {
        var table = new Dictionary<uint, Guid>();
        for (i++; i < nodes.Count; i++)
        {
            var node = nodes[i];
            switch (node.Id)
            {
                case 0x024: // GlobalIdTableEntryFNDX: index, GUID
                    table[U32(node.Data)] = Guid(node.Data + 4);
                    break;
                case 0x025: // GlobalIdTableEntry2FNDX: copy one entry from the parent revision's table
                    if (parent?.GetValueOrDefault(U32(node.Data)) is { } guid)
                    {
                        table[U32(node.Data + 4)] = guid;
                    }

                    break;
                case 0x026: // GlobalIdTableEntry3FNDX: copy a range
                    var (from, count, to) = (U32(node.Data), U32(node.Data + 4), U32(node.Data + 8));
                    for (var k = 0u; k < count && k < 1_000_000; k++)
                    {
                        if (parent?.GetValueOrDefault(from + k) is { } copied)
                        {
                            table[to + k] = copied;
                        }
                    }

                    break;
                case 0x028: // GlobalIdTableEndFNDX
                    i++;
                    return table;
            }
        }

        return table;
    }

    /// <summary>An object declaration node, decoded; null for other nodes.</summary>
    private (ExGuid Id, StoreObject Object)? Declare(Node node, Dictionary<uint, Guid> table)
    {
        switch (node.Id)
        {
            case 0x0A4 or 0x0A5 or 0x0C4 or 0x0C5: // ObjectDeclaration2(Large)RefCountFND, read-only variants
                return (Resolve(U32(node.Data), table), new StoreObject(U32(node.Data + 4), ReadObjectPropSet(node.Reference, table)));
            case 0x02D or 0x02E: // ObjectDeclarationWithRefCountFNDX: a 10-bit JCI; these objects are property sets
                return (Resolve(U32(node.Data), table), new StoreObject((U32(node.Data + 4) & 0x3FF) | 0x20000, ReadObjectPropSet(node.Reference, table)));
            case 0x072 or 0x073: // ObjectDeclarationFileData3(Large)RefCountFND: a file, by reference
            {
                var at = node.Data + 8 + (node.Id == 0x072 ? 1 : 4);
                var reference = StorageString(ref at);
                var extension = StorageString(ref at);
                var value = new StoreObject(U32(node.Data + 4), PropertySet.Empty) { FileExtension = extension };
                LoadFileData(value, reference);
                return (Resolve(U32(node.Data), table), value);
            }

            default:
                return null;
        }
    }

    /// <summary>New properties for an object declared earlier in this revision or one it depends on.</summary>
    private void ApplyObjectRevision(Node node, Dictionary<uint, Guid> table, Dictionary<ExGuid, StoreObject> objects,
        Revision? parent, Dictionary<ExGuid, Revision> revisions)
    {
        var id = Resolve(U32(node.Data), table);
        var declared = objects.GetValueOrDefault(id);
        for (var ancestor = parent; declared is null && ancestor is not null; ancestor = ancestor.Parent.IsNil ? null : revisions.GetValueOrDefault(ancestor.Parent))
        {
            declared = ancestor.Objects.GetValueOrDefault(id);
        }

        if (declared is not null)
        {
            objects[id] = new StoreObject(declared.Jcid, ReadObjectPropSet(node.Reference, table))
            {
                FileData = declared.FileData,
                FileExtension = declared.FileExtension,
                FileDataProblem = declared.FileDataProblem,
            };
        }
    }

    private void LoadFileData(StoreObject value, string reference)
    {
        if (reference.StartsWith("<ifndf>", StringComparison.Ordinal))
        {
            if (System.Guid.TryParse(reference["<ifndf>".Length..], out var guid) && _fileData.TryGetValue(guid, out var location))
            {
                value.FileData = _data.AsSpan((int)location.Offset, (int)location.Length).ToArray();
            }
            else
            {
                value.FileDataProblem = "its data is missing from the section file";
            }
        }
        else if (reference.StartsWith("<file>", StringComparison.Ordinal))
        {
            // Large files are kept outside the section, in the notebook's "onefiles" folder.
            var name = reference["<file>".Length..];
            var candidates = _folder is null ? [] : new[] { Path.Combine(_folder, name), Path.Combine(_folder, "onefiles", name) };
            if (candidates.FirstOrDefault(File.Exists) is { } path)
            {
                value.FileData = File.ReadAllBytes(path);
            }
            else
            {
                value.FileDataProblem = $"it is stored outside the section file ({name}) and wasn't found";
            }
        }
        else
        {
            value.FileDataProblem = "OneNote marked its data as unavailable";
        }
    }

    // ---- Property sets ([MS-ONESTORE] 2.6.1, 2.6.7) ----

    private sealed class IdStreams(List<ExGuid> objects, List<ExGuid> spaces, List<ExGuid> contexts)
    {
        private int _object, _space, _context;

        public IReadOnlyList<ExGuid> TakeObjects(uint count) => Take(objects, ref _object, count);

        public IReadOnlyList<ExGuid> TakeSpaces(uint count) => Take(spaces, ref _space, count);

        public IReadOnlyList<ExGuid> TakeContexts(uint count) => Take(contexts, ref _context, count);

        private static IReadOnlyList<ExGuid> Take(List<ExGuid> stream, ref int position, uint count)
        {
            var available = (int)Math.Min(count, (uint)Math.Max(0, stream.Count - position));
            var taken = stream.GetRange(position, available);
            position += available;
            return taken;
        }
    }

    private PropertySet ReadObjectPropSet(ChunkReference reference, Dictionary<uint, Guid> table)
    {
        if (reference.IsNilOrZero)
        {
            return PropertySet.Empty;
        }

        var at = reference.Stp;
        Need(at, reference.Cb);
        var header = U32(at);
        at += 4;
        var objects = CompactIds(ref at, header & 0xFFFFFF, table);
        List<ExGuid> spaces = [], contexts = [];
        if (header >> 31 == 0) // the object space ID stream is present
        {
            var spaceHeader = U32(at);
            at += 4;
            spaces = CompactIds(ref at, spaceHeader & 0xFFFFFF, table);
            if ((spaceHeader >> 30 & 1) != 0) // ...and a context ID stream follows
            {
                var contextHeader = U32(at);
                at += 4;
                contexts = CompactIds(ref at, contextHeader & 0xFFFFFF, table);
            }
        }

        return ReadPropertySet(ref at, new IdStreams(objects, spaces, contexts), 0);
    }

    private List<ExGuid> CompactIds(ref long at, uint count, Dictionary<uint, Guid> table)
    {
        Need(at, 4L * count);
        var ids = new List<ExGuid>((int)Math.Min(count, 100_000));
        for (var k = 0u; k < count; k++, at += 4)
        {
            ids.Add(Resolve(U32(at), table));
        }

        return ids;
    }

    private PropertySet ReadPropertySet(ref long at, IdStreams streams, int depth)
    {
        if (depth > 32)
        {
            throw Corrupt("property sets are nested too deeply");
        }

        var count = U16(at);
        at += 2;
        var ids = new uint[count];
        for (var k = 0; k < count; k++, at += 4)
        {
            ids[k] = U32(at);
        }

        var set = new PropertySet();
        foreach (var id in ids)
        {
            PropertyValue value;
            switch ((id >> 26) & 0x1F)
            {
                case 0x1: value = new NoValue(); break;
                case 0x2: value = new BoolValue(id >> 31 != 0); break;
                case 0x3: value = new NumberValue(_data[Check(at, 1)], 1); at += 1; break;
                case 0x4: value = new NumberValue(U16(at), 2); at += 2; break;
                case 0x5: value = new NumberValue(U32(at), 4); at += 4; break;
                case 0x6: value = new NumberValue(U64(at), 8); at += 8; break;
                case 0x7:
                {
                    var length = U32(at);
                    at += 4;
                    Need(at, length);
                    value = new BytesValue(_data.AsSpan((int)at, (int)length).ToArray());
                    at += length;
                    break;
                }

                case 0x8: value = new ReferencesValue(streams.TakeObjects(1)); break;
                case 0x9: value = new ReferencesValue(streams.TakeObjects(U32(at))); at += 4; break;
                case 0xA: value = new ReferencesValue(streams.TakeSpaces(1)); break;
                case 0xB: value = new ReferencesValue(streams.TakeSpaces(U32(at))); at += 4; break;
                case 0xC: value = new ReferencesValue(streams.TakeContexts(1)); break;
                case 0xD: value = new ReferencesValue(streams.TakeContexts(U32(at))); at += 4; break;
                case 0x10:
                {
                    var sets = U32(at);
                    at += 8; // the count, then the element type's PropertyID
                    var values = new List<PropertySet>();
                    for (var k = 0u; k < sets && k < 100_000; k++)
                    {
                        values.Add(ReadPropertySet(ref at, streams, depth + 1));
                    }

                    value = new PropertySetsValue(values);
                    break;
                }

                case 0x11: value = new PropertySetsValue([ReadPropertySet(ref at, streams, depth + 1)]); break;
                default: throw Corrupt($"unknown property type {(id >> 26) & 0x1F:X}");
            }

            set.Add(id, value);
        }

        return set;
    }

    // ---- Primitives ----

    private static ExGuid Resolve(uint compactId, Dictionary<uint, Guid> table) =>
        table.TryGetValue(compactId >> 8, out var guid) ? new ExGuid(guid, compactId & 0xFF) : ExGuid.Nil;

    private string StorageString(ref long at)
    {
        var characters = U32(at);
        at += 4;
        Need(at, characters * 2L);
        var text = Encoding.Unicode.GetString(_data, (int)at, (int)characters * 2);
        at += characters * 2L;
        return text;
    }

    private ChunkReference Fcr64x32(long at)
    {
        var stp = U64(at);
        var cb = U32(at + 8);
        return new ChunkReference((long)Math.Min(stp, long.MaxValue), cb, stp == ulong.MaxValue && cb == 0);
    }

    private ExGuid ExGuidAt(long at) => new(Guid(at), U32(at + 16));

    private Guid Guid(long at) => new(_data.AsSpan(Check(at, 16), 16));

    private ushort U16(long at) => BinaryPrimitives.ReadUInt16LittleEndian(_data.AsSpan(Check(at, 2)));

    private uint U32(long at) => BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Check(at, 4)));

    private ulong U64(long at) => BinaryPrimitives.ReadUInt64LittleEndian(_data.AsSpan(Check(at, 8)));

    private int Check(long at, long length)
    {
        Need(at, length);
        return (int)at;
    }

    private void Need(long at, long length)
    {
        if (at < 0 || length < 0 || at + length > _data.Length)
        {
            throw Corrupt("a reference points outside the file");
        }
    }

    private static OneNoteFormatException Corrupt(string detail) => new($"The OneNote file is damaged or not supported: {detail}.");
}
