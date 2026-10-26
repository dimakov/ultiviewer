using System.Buffers.Binary;

namespace Ultiviewer.Core.OneNote.Store;

/// <summary>
/// Reads an ObjectSpaceObjectPropSet ([MS-ONESTORE] 2.6.1): three streams of compact IDs (objects, then object
/// spaces and contexts when present) followed by the property set ([MS-ONESTORE] 2.6.7). Both store formats use
/// it; they differ only in how a stream's compact IDs become <see cref="ExGuid"/>s.
/// </summary>
internal static class PropertySetReader
{
    public enum IdStream
    {
        Objects,
        Spaces,
        Contexts,
    }

    /// <param name="resolve">Turns one stream's compact IDs, in order, into ids (same count, or fewer when unknown).</param>
    public static PropertySet Read(byte[] data, long at, long length, Func<IdStream, uint[], List<ExGuid>> resolve)
    {
        Need(data, at, length);
        var header = U32(data, at);
        at += 4;
        var objects = resolve(IdStream.Objects, CompactIds(data, ref at, header & 0xFFFFFF));
        List<ExGuid> spaces = [], contexts = [];
        if (header >> 31 == 0) // the object space ID stream is present
        {
            var spaceHeader = U32(data, at);
            at += 4;
            spaces = resolve(IdStream.Spaces, CompactIds(data, ref at, spaceHeader & 0xFFFFFF));
            if ((spaceHeader >> 30 & 1) != 0) // ...and a context ID stream follows
            {
                var contextHeader = U32(data, at);
                at += 4;
                contexts = resolve(IdStream.Contexts, CompactIds(data, ref at, contextHeader & 0xFFFFFF));
            }
        }

        return ReadSet(data, ref at, new Streams(objects, spaces, contexts), 0);
    }

    public static OneNoteFormatException Corrupt(string detail) => new($"The OneNote file is damaged or not supported: {detail}.");

    private static uint[] CompactIds(byte[] data, ref long at, uint count)
    {
        Need(data, at, 4L * count);
        var ids = new uint[count];
        for (var k = 0; k < ids.Length; k++, at += 4)
        {
            ids[k] = U32(data, at);
        }

        return ids;
    }

    private static PropertySet ReadSet(byte[] data, ref long at, Streams streams, int depth)
    {
        if (depth > 32)
        {
            throw Corrupt("property sets are nested too deeply");
        }

        var count = U16(data, at);
        at += 2;
        var ids = new uint[count];
        for (var k = 0; k < count; k++, at += 4)
        {
            ids[k] = U32(data, at);
        }

        var set = new PropertySet();
        foreach (var id in ids)
        {
            PropertyValue value;
            switch ((id >> 26) & 0x1F)
            {
                case 0x1: value = new NoValue(); break;
                case 0x2: value = new BoolValue(id >> 31 != 0); break;
                case 0x3: Need(data, at, 1); value = new NumberValue(data[at], 1); at += 1; break;
                case 0x4: value = new NumberValue(U16(data, at), 2); at += 2; break;
                case 0x5: value = new NumberValue(U32(data, at), 4); at += 4; break;
                case 0x6: value = new NumberValue(U64(data, at), 8); at += 8; break;
                case 0x7:
                {
                    var length = U32(data, at);
                    at += 4;
                    Need(data, at, length);
                    value = new BytesValue(data.AsSpan((int)at, (int)length).ToArray());
                    at += length;
                    break;
                }

                case 0x8: value = new ReferencesValue(streams.TakeObjects(1)); break;
                case 0x9: value = new ReferencesValue(streams.TakeObjects(U32(data, at))); at += 4; break;
                case 0xA: value = new ReferencesValue(streams.TakeSpaces(1)); break;
                case 0xB: value = new ReferencesValue(streams.TakeSpaces(U32(data, at))); at += 4; break;
                case 0xC: value = new ReferencesValue(streams.TakeContexts(1)); break;
                case 0xD: value = new ReferencesValue(streams.TakeContexts(U32(data, at))); at += 4; break;
                case 0x10:
                {
                    var sets = U32(data, at);
                    at += 8; // the count, then the element type's PropertyID
                    var values = new List<PropertySet>();
                    for (var k = 0u; k < sets && k < 100_000; k++)
                    {
                        values.Add(ReadSet(data, ref at, streams, depth + 1));
                    }

                    value = new PropertySetsValue(values);
                    break;
                }

                case 0x11: value = new PropertySetsValue([ReadSet(data, ref at, streams, depth + 1)]); break;
                default: throw Corrupt($"unknown property type {(id >> 26) & 0x1F:X}");
            }

            set.Add(id, value);
        }

        return set;
    }

    private static ushort U16(byte[] data, long at)
    {
        Need(data, at, 2);
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)at));
    }

    private static uint U32(byte[] data, long at)
    {
        Need(data, at, 4);
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)at));
    }

    private static ulong U64(byte[] data, long at)
    {
        Need(data, at, 8);
        return BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan((int)at));
    }

    private static void Need(byte[] data, long at, long length)
    {
        if (at < 0 || length < 0 || at + length > data.Length)
        {
            throw Corrupt("a reference points outside the file");
        }
    }

    /// <summary>The ids a property set's references take, in the order its properties use them.</summary>
    private sealed class Streams(List<ExGuid> objects, List<ExGuid> spaces, List<ExGuid> contexts)
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
}
