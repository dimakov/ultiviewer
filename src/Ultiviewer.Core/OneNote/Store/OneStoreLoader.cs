namespace Ultiviewer.Core.OneNote.Store;

/// <summary>Opens a .one or .onetoc2 file in whichever container format it uses.</summary>
public static class OneStoreLoader
{
    public static OneStoreFile Load(byte[] data, string? folder)
    {
        var format = RevisionStoreReader.FormatOf(data);
        if (format == RevisionStoreReader.DesktopFormat)
        {
            // Newer OneDrive downloads keep a desktop header but store a package after it.
            return PackageStoreReader.EmbeddedPackageOffset(data) is { } offset
                ? PackageStoreReader.Read(data, offset)
                : RevisionStoreReader.Read(data, folder);
        }

        if (format == RevisionStoreReader.PackagedFormat)
        {
            return PackageStoreReader.Read(data);
        }

        throw new OneNoteFormatException("This is not a OneNote file.");
    }
}
