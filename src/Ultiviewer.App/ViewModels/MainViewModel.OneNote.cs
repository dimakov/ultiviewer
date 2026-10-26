using System.IO;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.Core.OneNote;
using Ultiviewer.Core.OneNote.Store;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>File → Import from OneNote: opens a .onepkg, .one or .onetoc2 in the import window.</summary>
public sealed partial class MainViewModel
{
    public event Action<OneNoteImportViewModel>? OneNoteImportRequested;

    /// <param name="path">A file dropped on the window; null asks for one.</param>
    [RelayCommand]
    private async Task ImportOneNote(string? path)
    {
        path ??= _dialogs.PickFile("Import from OneNote",
            "OneNote notebook or section|*.onepkg;*.one;*.onetoc2|OneNote package (*.onepkg)|*.onepkg|OneNote section (*.one)|*.one|All files|*.*");
        if (path is null)
        {
            return;
        }

        StatusMessage = $"Opening {Path.GetFileName(path)}…";
        try
        {
            var source = await Task.Run(() => OneNoteSource.Open(path));
            StatusMessage = "";
            OneNoteImportRequested?.Invoke(new OneNoteImportViewModel(source, path, AppPaths.DefaultNotebooksDirectory, _dialogs, OnOneNoteImported));
        }
        catch (Exception ex) when (ex is OneNoteFormatException or IOException or UnauthorizedAccessException)
        {
            StatusMessage = "";
            _dialogs.ShowError($"Couldn't open {Path.GetFileName(path)}:\n{ex.Message}");
        }
    }

    private void OnOneNoteImported(OneNoteImportResult result)
    {
        var root = OpenNotebook(result.Folder);
        root.IsExpanded = true;
        StatusMessage = $"Imported {result.Pages} page{(result.Pages == 1 ? "" : "s")} from OneNote into {result.Folder}";
    }
}
