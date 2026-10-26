namespace Ultiviewer.Core.OneNote.Store;

/// <summary>Opens a .one or .onetoc2 file in whichever container format it uses.</summary>
public static class OneStoreLoader
{
    public static OneStoreFile Load(byte[] data, string? folder)
    {
        var format = RevisionStoreReader.FormatOf(data);
        if (format == RevisionStoreReader.DesktopFormat)
        {
            return RevisionStoreReader.Read(data, folder);
        }

        if (format == RevisionStoreReader.PackagedFormat)
        {
            throw new OneNoteFormatException("This section was saved by OneNote for the web (OneDrive), a format the import doesn't read yet.");
        }

        throw new OneNoteFormatException("This is not a OneNote file.");
    }
}
