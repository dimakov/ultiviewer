using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Exchange;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>Export a notebook or section as an ordered zip (with a JSON manifest) and import such zips.</summary>
public sealed partial class MainViewModel
{
    [RelayCommand]
    private async Task ExportZip(TreeNodeViewModel? node)
    {
        var target = node ?? SelectedNode ?? (ActiveTab?.Path is { } path ? NotebookOf(path) : null) ?? Notebooks.FirstOrDefault();
        if (target is null)
        {
            _dialogs.ShowInfo("Open a notebook first.");
            return;
        }

        // Pages and files export as their folder; export the notebook they belong to.
        if (target.Kind is NodeKind.Page or NodeKind.File)
        {
            target = target.Root;
        }

        var zipPath = _dialogs.PickSaveFile(
            target.Kind == NodeKind.Notebook ? "Export notebook as zip" : "Export section as zip",
            $"{NotebookExporter.SafeName(target.Name)} {DateTime.Now:yyyy-MM-dd}.zip",
            "Zip archive|*.zip");
        if (zipPath is null)
        {
            return;
        }

        // Unsaved edits of pages in this folder go into the export.
        foreach (var tab in Tabs.Where(t => t.IsDirty && t.Path is not null && FileSystemMoves.IsSameOrInside(t.Path, target.Path)).ToList())
        {
            SaveTab(tab, interactive: true);
        }

        StatusMessage = $"Exporting \"{target.Name}\"…";
        var folder = target.Path;
        var name = target.Name;
        var isNotebook = target.Kind == NodeKind.Notebook;
        try
        {
            var manifest = await Task.Run(() => NotebookExporter.Export(folder, name, zipPath, isNotebook));
            var pages = CountPages(manifest.Root);
            StatusMessage = $"Exported {pages} page{(pages == 1 ? "" : "s")} of \"{name}\" to {Path.GetFileName(zipPath)}";
            ShellService.RevealInExplorer(zipPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "";
            _dialogs.ShowError($"Couldn't export \"{name}\":\n{ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ImportZip()
    {
        var zipPath = _dialogs.PickFile("Import a notebook from a zip", "Zip archive|*.zip");
        if (zipPath is null)
        {
            return;
        }

        var parent = _dialogs.PickFolder("Choose where to put the imported notebook", AppPaths.DefaultNotebooksDirectory);
        if (parent is null)
        {
            return;
        }

        StatusMessage = $"Importing {Path.GetFileName(zipPath)}…";
        try
        {
            var folder = await Task.Run(() => NotebookImporter.Import(zipPath, parent));
            var root = OpenNotebook(folder);
            root.IsExpanded = true;
            StatusMessage = $"Imported \"{root.Name}\" into {folder}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            StatusMessage = "";
            _dialogs.ShowError($"Couldn't import {Path.GetFileName(zipPath)}:\n{ex.Message}");
        }
    }

    private static int CountPages(ExportItem item) =>
        (item.Kind == "page" ? 1 : 0) + (item.Children?.Sum(CountPages) ?? 0);
}
