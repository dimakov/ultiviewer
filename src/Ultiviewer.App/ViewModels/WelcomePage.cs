namespace Ultiviewer.App.ViewModels;

/// <summary>Content of the first page in the first notebook a user creates.</summary>
public static class WelcomePage
{
    public const string Text = """
        # Welcome to Ultiviewer

        Your notes are plain Markdown files in a normal folder. Explorer, git, OneDrive and other editors all work with them.

        ## How notebooks map to files

        | In the tree | On disk |
        |---|---|
        | Notebook | a folder |
        | Section | a subfolder |
        | Page | `Title.md` |
        | Subpages | a folder named like the page, next to it |
        | Pasted images | `Title.assets/` next to the page |

        Drag items in the tree to reorder them, move them to another section, or drop a page onto another page to make it a subpage.

        ## Handy keys

        - [ ] **Ctrl+N** new page, **Ctrl+Shift+N** new section, **F2** rename
        - [ ] **Ctrl+E** jump to any page by title
        - [ ] **Ctrl+Shift+F** search all notebooks; **Ctrl+F** / **Ctrl+H** find and replace in the page
        - [ ] **Ctrl+Shift+V** switch between Edit, Split and Preview
        - [ ] **Ctrl+1** toggle a to-do on the current line
        - [ ] **Alt+Shift+D** insert today's date
        - [ ] Paste a screenshot with **Ctrl+V** — it is saved next to the page

        Pages save themselves as you type. Link pages with `[[Page title]]` and tag them with `#tags`.
        """;
}
