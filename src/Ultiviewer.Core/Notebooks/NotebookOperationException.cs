namespace Ultiviewer.Core.Notebooks;

/// <summary>A notebook operation that can't be performed; the message is shown to the user.</summary>
public sealed class NotebookOperationException(string message, Exception? inner = null) : Exception(message, inner);
