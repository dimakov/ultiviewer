using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Ultiviewer.App.Services;
using Ultiviewer.Core.Notebooks;
using Ultiviewer.Core.Settings;

namespace Ultiviewer.App.ViewModels;

/// <summary>
/// Root view model. Split by area into partial files: notebooks (tree), documents (tabs), search, and so on.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly IDialogService _dialogs;
    private readonly NotebookOperations _ops;

    public MainViewModel(AppSettings settings, SessionState session, IDialogService dialogs, NotebookOperations ops)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = settings;
        Session = session;
        _dialogs = dialogs;
        _ops = ops;
        InitializeGroups();
    }

    public AppSettings Settings { get; }

    public SessionState Session { get; }

    [ObservableProperty]
    private string _statusMessage = "";

    public void Initialize(IEnumerable<string> startupFiles)
    {
        LoadNotebooks();
        InitializeSearch();
        InitializeSummaries();
        _syncingSelection = true;
        try
        {
            RestoreSelection();
        }
        finally
        {
            _syncingSelection = false;
        }

        InitializeDocuments(startupFiles);
        InitializeOutline();
    }

    /// <summary>Collects UI state into <see cref="Session"/> and writes settings and session to disk.</summary>
    public void SaveState()
    {
        CaptureNotebookState();
        CaptureDocumentState();
        Session.Save();
        Settings.Save();
    }

    public void Dispose()
    {
        DisposeWatchers();
        _searchCancellation?.Cancel();
        _search?.Dispose();
    }

    /// <summary>Runs a file operation and shows its error to the user instead of throwing.</summary>
    private bool TryRun(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (NotebookOperationException ex)
        {
            _dialogs.ShowError(ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(ex.Message);
        }

        return false;
    }
}
