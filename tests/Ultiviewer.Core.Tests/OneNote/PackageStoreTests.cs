using System.Buffers.Binary;
using System.Text;
using Ultiviewer.Core.OneNote;
using Ultiviewer.Core.OneNote.Store;

namespace Ultiviewer.Core.Tests.OneNote;

/// <summary>
/// The packaged (OneNote for the web / OneDrive) store format. The public samples of it can't be committed
/// (their licenses), so the tests build a small package with <see cref="PackageBuilder"/>.
/// </summary>
public class PackageStoreTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadsTheNewestRevisionOfEachCell(bool embeddedAfterDesktopHeader)
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "Web section.one");
        File.WriteAllBytes(path, SectionPackage(embeddedAfterDesktopHeader));

        var section = OneNoteReader.ReadSection(path);

        Assert.Null(section.Problem);
        Assert.Equal("Web section", section.Name); // stored as "Web section.one"
        var page = Assert.Single(section.Pages);
        Assert.Equal("Plan", page.Title); // the newer revision's metadata, not "Old title"
        Assert.Equal(2, page.Level);
        Assert.Collection(
            page.Blocks,
            outline =>
            {
                var element = Assert.Single(Assert.IsType<OneNoteOutline>(outline).Elements);
                Assert.Equal("Hello from the web", Assert.IsType<OneNoteText>(Assert.Single(element.Contents)).Text);
            },
            picture =>
            {
                var image = Assert.IsType<OneNoteImage>(Assert.IsType<OneNoteFreeContent>(picture).Content);
                Assert.Equal(Png, image.Data);
                Assert.Equal(".png", image.Extension);
                Assert.Equal("A chart", image.AltText);
            });
    }

    [Fact]
    public void ReportsDamagedPackages()
    {
        using var temp = new TempDirectory();
        var bytes = SectionPackage(false);
        var path = Path.Combine(temp.Path, "Cut.one");
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        var section = OneNoteReader.ReadSection(path);

        Assert.Empty(section.Pages);
        Assert.Contains("damaged", section.Problem);
    }

    /// <summary>
    /// A section: the data root (section → page series → page cell) and a page cell with two revisions. The
    /// page's object space also has a version-history cell in another context, listed after it.
    /// </summary>
    private static byte[] SectionPackage(bool embedded)
    {
        var b = new PackageBuilder();
        var defaultContext = b.Id();
        var versionContext = b.Id();
        var rootSpace = b.Id();
        var pageSpace = b.Id();

        // Data root: one revision.
        var section = b.Id();
        var series = b.Id();
        var sectionMeta = b.Id();
        var rootGroup = b.Group(
            new PackageBuilder.Obj(section, 0x00060007, [b.Refs(0x24001C20, series)]),
            new PackageBuilder.Obj(series, 0x00060008, [b.SpaceRefs(0x2C001D63, (defaultContext, pageSpace))]),
            new PackageBuilder.Obj(sectionMeta, 0x00020031, [PackageBuilder.Text(0x1C00349B, "Web section.one")]));
        var rootRevision = b.Revision(PackageBuilder.ExGuid.Nil, (1, section), (2, sectionMeta), rootGroup);

        // Page: a base revision and a newer one that changes the title and text and adds a picture.
        var manifest = b.Id();
        var pageNode = b.Id();
        var metadata = b.Id();
        var outline = b.Id();
        var element = b.Id();
        var text = b.Id();
        var image = b.Id();
        var container = b.Id();
        var baseGroup = b.Group(
            new PackageBuilder.Obj(manifest, 0x00060037, [b.Refs(0x24001C1F, pageNode)]),
            new PackageBuilder.Obj(pageNode, 0x0006000B, [b.Refs(0x24001C20, outline)]),
            new PackageBuilder.Obj(metadata, 0x00020030, [PackageBuilder.Text(0x1C001CF3, "Old title"), PackageBuilder.U32(0x14001DFF, 2)]),
            new PackageBuilder.Obj(outline, 0x0006000C, [b.Refs(0x24001C20, element)]),
            new PackageBuilder.Obj(element, 0x0006000D, [b.Refs(0x24001C1F, text)]),
            new PackageBuilder.Obj(text, 0x0006000E, [PackageBuilder.Text(0x1C001C22, "Old text")]));
        var baseRevision = b.Revision(PackageBuilder.ExGuid.Nil, (1, manifest), (2, metadata), baseGroup);
        var blob = b.Blob(Png);
        var newGroup = b.Group(
            new PackageBuilder.Obj(pageNode, 0x0006000B, [b.Refs(0x24001C20, outline, image)]),
            new PackageBuilder.Obj(metadata, 0x00020030, [PackageBuilder.Text(0x1C001CF3, "Plan"), PackageBuilder.U32(0x14001DFF, 2)]),
            new PackageBuilder.Obj(text, 0x0006000E, [PackageBuilder.Text(0x1C001C22, "Hello from the web")]),
            new PackageBuilder.Obj(image, 0x00060011, [b.Refs(0x20001C3F, container), PackageBuilder.Text(0x1C001E58, "A chart")]),
            new PackageBuilder.Obj(container, 0x00080039, [PackageBuilder.Text(0x1C003424, ".png")]) { Blob = blob });
        var newRevision = b.Revision(baseRevision, null, null, newGroup);

        // Version history of the same page space, in another context: only a placeholder object.
        var history = b.Id();
        var historyRevision = b.Revision(PackageBuilder.ExGuid.Nil, (1, history), null, b.Group(new PackageBuilder.Obj(history, 0x0006003C, [])));

        b.Cell(defaultContext, rootSpace, rootRevision);
        b.Cell(defaultContext, pageSpace, newRevision);
        b.Cell(versionContext, pageSpace, historyRevision);
        return b.Build((defaultContext, rootSpace), embedded);
    }

    /// <summary>Writes [MS-FSSHTTPB] stream objects and data elements wrapped in [MS-ONESTORE] 2.8 packaging.</summary>
    private sealed class PackageBuilder
    {
        private static readonly Guid RoleGuid = new("4A3717F8-1C14-49E7-9526-81D942DE1741");
        private readonly List<byte[]> _elements = [];
        private readonly List<(ExGuid Context, ExGuid Space, ExGuid Revision)> _cells = [];
        private readonly Dictionary<ExGuid, ExGuid> _revisionElements = [];
        private int _next = 1;

        public readonly record struct ExGuid(Guid Guid, uint N)
        {
            public static ExGuid Nil => default;
        }

        /// <summary>An object: JCID, properties (written in order), and optionally file data in a blob.</summary>
        public sealed record Obj(ExGuid Id, uint Jcid, IReadOnlyList<Property> Properties)
        {
            public ExGuid? Blob { get; init; }
        }

        /// <summary>A property: its id, its data, and the objects or cells it references.</summary>
        public sealed record Property(uint Id, byte[] Data, IReadOnlyList<ExGuid> Objects, IReadOnlyList<(ExGuid Context, ExGuid Space)> Cells);

        public ExGuid Id() => new(new Guid(_next++, 0x1111, 0x2222, 1, 2, 3, 4, 5, 6, 7, 8), (uint)_next % 7 + 1);

        public Property Refs(uint id, params ExGuid[] objects) =>
            new(id, (id >> 26 & 0x1F) == 0x9 ? BitConverter.GetBytes((uint)objects.Length) : [], objects, []);

        public Property SpaceRefs(uint id, params (ExGuid Context, ExGuid Space)[] cells) => new(id, BitConverter.GetBytes((uint)cells.Length), [], cells);

        public static Property Text(uint id, string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value);
            return new Property(id, [.. BitConverter.GetBytes((uint)bytes.Length), .. bytes], [], []);
        }

        public static Property U32(uint id, uint value) => new(id, BitConverter.GetBytes(value), [], []);

        public ExGuid Blob(byte[] data)
        {
            var id = Id();
            var body = new Writer();
            body.Start16(0x02).Compact((ulong)data.Length).Bytes(data).End8(0x01);
            Element(id, 0x0A, body);
            return id;
        }

        public ExGuid Group(params Obj[] objects)
        {
            var body = new Writer();
            body.Start16(0x1D);
            foreach (var o in objects)
            {
                body.Start16(0x18).ExGuid(o.Id).Compact(4).Compact(4).Compact(0).Compact(0);
                body.Start16(0x18).ExGuid(o.Id).Compact(1).Compact(0).Compact((ulong)o.Properties.Sum(p => p.Objects.Count)).Compact(0);
                if (o.Blob is { } blob)
                {
                    body.Start16(0x05).ExGuid(o.Id).ExGuid(blob).Compact(2).Compact(0).Compact(0);
                }
            }

            body.End8(0x1D).Start16(0x1E);
            foreach (var o in objects)
            {
                body.Start16(0x16).Compact(0).Compact(0).Compact(4).U32(o.Jcid);
                var cells = o.Properties.SelectMany(p => p.Cells).ToList();
                var references = o.Properties.SelectMany(p => p.Objects).ToList();
                body.Start16(0x16).Compact((ulong)references.Count);
                foreach (var reference in references)
                {
                    body.ExGuid(reference);
                }

                body.Compact((ulong)cells.Count);
                foreach (var (context, space) in cells)
                {
                    body.ExGuid(context).ExGuid(space);
                }

                var data = PropSet(o.Properties, references.Count, cells.Count);
                body.Compact((ulong)data.Length).Bytes(data);
                if (o.Blob is { } blob)
                {
                    body.Start16(0x1C).Compact(0).Compact(0).ExGuid(blob);
                }
            }

            body.End8(0x1E).End8(0x01);
            var id = Id();
            Element(id, 0x05, body);
            return id;
        }

        /// <summary>A revision manifest; returns its revision id.</summary>
        public ExGuid Revision(ExGuid baseRevision, (uint Role, ExGuid Object)? content, (uint Role, ExGuid Object)? metadata, ExGuid group)
        {
            var revision = Id();
            var body = new Writer();
            body.Start16(0x1A).ExGuid(revision).ExGuid(baseRevision);
            foreach (var root in new[] { content, metadata })
            {
                if (root is { } r)
                {
                    body.Start16(0x0A).ExGuid(new ExGuid(RoleGuid, r.Role)).ExGuid(r.Object);
                }
            }

            body.Start16(0x19).ExGuid(group).End8(0x01);
            var element = Id();
            Element(element, 0x04, body);
            _revisionElements[revision] = element;
            return revision;
        }

        public void Cell(ExGuid context, ExGuid space, ExGuid revision) => _cells.Add((context, space, revision));

        public byte[] Build((ExGuid Context, ExGuid Space) dataRoot, bool embedded)
        {
            var index = new Writer();
            foreach (var (context, space, revision) in _cells)
            {
                var cellManifest = Id();
                var manifest = new Writer();
                manifest.Start16(0x0B).ExGuid(revision).End8(0x01);
                Element(cellManifest, 0x03, manifest);
                index.Start16(0x0E).ExGuid(context).ExGuid(space).ExGuid(cellManifest).U8(0);
            }

            foreach (var (revision, element) in _revisionElements)
            {
                index.Start16(0x0D).ExGuid(revision).ExGuid(element).U8(0);
            }

            index.End8(0x01);
            var indexId = Id();
            Element(indexId, 0x01, index);

            var storage = new Writer();
            storage.Start16(0x0C).Guid(new Guid("1F937CB4-B26F-445F-B9F8-17E20160E461"))
                .Start16(0x07).ExGuid(new ExGuid(new Guid("84DEFAB9-AAA3-4A0D-A3A8-520C77AC7073"), 2)).ExGuid(dataRoot.Context).ExGuid(dataRoot.Space)
                .End8(0x01);
            Element(Id(), 0x02, storage);

            var package = new Writer();
            package.Guid(Guid.NewGuid()).Guid(Guid.NewGuid()).Guid(Guid.Empty).Guid(RevisionStoreReader.PackagedFormat).U32(0);
            package.U32(2 | 0x7Au << 3).ExGuid(indexId).Guid(Guid.NewGuid());
            package.Start16(0x15).U8(0);
            foreach (var element in _elements)
            {
                package.Bytes(element);
            }

            package.End8(0x15).U16(0x7A << 2 | 3);
            if (!embedded)
            {
                return package.ToArray();
            }

            // A desktop header with no legacy version whose transaction log ends where the package starts.
            var header = new byte[1024];
            RevisionStoreReader.DesktopFormat.TryWriteBytes(header.AsSpan(48));
            BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(160), 1000);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(168), 24);
            return [.. header, .. package.ToArray()];
        }

        private void Element(ExGuid id, ulong type, Writer body)
        {
            var element = new Writer();
            element.Start16(0x01).ExGuid(id).U8(0).Compact(type).Bytes(body.ToArray());
            _elements.Add(element.ToArray());
        }

        /// <summary>An ObjectSpaceObjectPropSet: the object and object-space ID streams, then the property set.</summary>
        private static byte[] PropSet(IReadOnlyList<Property> properties, int objects, int spaces)
        {
            var w = new Writer();
            w.U32((uint)objects | (spaces == 0 ? 1u << 31 : 0));
            for (var k = 0; k < objects; k++)
            {
                w.U32((uint)(k + 1) << 8);
            }

            if (spaces > 0)
            {
                w.U32((uint)spaces);
                for (var k = 0; k < spaces; k++)
                {
                    w.U32((uint)(k + 1) << 8);
                }
            }

            w.U16((ushort)properties.Count);
            foreach (var p in properties)
            {
                w.U32(p.Id);
            }

            foreach (var p in properties)
            {
                w.Bytes(p.Data);
            }

            return w.ToArray();
        }
    }

    private sealed class Writer
    {
        private readonly MemoryStream _stream = new();

        public Writer Start16(int type) => U16((ushort)(type << 3));

        public Writer End8(int type) => U8((byte)(type << 2 | 1));

        public Writer U8(byte value)
        {
            _stream.WriteByte(value);
            return this;
        }

        public Writer U16(ushort value) => Bytes(BitConverter.GetBytes(value));

        public Writer U32(uint value) => Bytes(BitConverter.GetBytes(value));

        public Writer Bytes(byte[] value)
        {
            _stream.Write(value);
            return this;
        }

        public Writer Guid(Guid value) => Bytes(value.ToByteArray());

        /// <summary>[MS-FSSHTTPB] 2.2.1.1: here the 7- and 14-bit forms are enough.</summary>
        public Writer Compact(ulong value) =>
            value == 0 ? U8(0) : value < 0x80 ? U8((byte)(value << 1 | 1)) : U16((ushort)(value << 2 | 2));

        /// <summary>[MS-FSSHTTPB] 2.2.1.7: the 5-bit form, or the 32-bit one.</summary>
        public Writer ExGuid(PackageBuilder.ExGuid value) =>
            value == PackageBuilder.ExGuid.Nil ? U8(0)
            : value.N < 32 ? U8((byte)(value.N << 3 | 4)).Guid(value.Guid)
            : U8(128).U32(value.N).Guid(value.Guid);

        public byte[] ToArray() => _stream.ToArray();
    }
}
