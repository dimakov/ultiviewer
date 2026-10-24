# HLD: Backlinks and heading links

## What it does

- **Links pane** (left panel → Links) for the active page:
  - *Linked from*: every line of another page that links here, `[[wiki links]]` (resolved like when they are
    followed) and Markdown links (`[text](Page.md#x)`) alike;
  - *Links on this page*: where each link leads, broken ones in red (`missing`);
  - *Unlinked mentions*: lines that mention the page's title as plain text (outside links and code); **Link**
    turns the mention into `[[Title]]` (or `[[Title|as written]]`), in the open tab or directly in the file.
  - Clicking a row opens that page at the line (or follows the link). The pane refreshes when the active page
    changes, after edits to it, and when the search index updates.
- **Heading links**: typing `#` after `[[Page` (or `[[#` for the current page) offers the target page's headings;
  `[[Page#Heading]]` opens the page at that heading (as before).

## Main code

| File | Responsibility |
|---|---|
| `Core/Links/PageLinks.cs` | `Outgoing`, `LinksTo`, `Mentions`, `LinkMention`. |
| `App/ViewModels/MainViewModel.Backlinks.cs` | Candidates from the index, background reading, rows, opening, linking mentions. |
| `App/Views/LinksPane.xaml(.cs)` | The pane. |
| `App/Views/EditorHost.Conveniences.cs` | `[[` page completion and the new `#` heading completion. |

## Design notes

- Backlinks are not stored: candidates are the index's pages that contain all words of the title (a superset
  that also catches `My%20Page.md` style links), verified with the same link parsing and resolution used to follow
  links; at most 300 candidates per notebook. Open tabs' unsaved text is used.
- Pages are read on a background thread; anything from the tree (locations, notebook roots) is collected on the
  UI thread before and after.
- Titles shorter than three characters don't produce unlinked mentions (too many false hits).

## Extending safely

- A graph view or a "broken links in the notebook" report can reuse `PageLinks.Outgoing` over all pages.
- If link resolution rules change (`WikiLinkResolver`), backlinks follow automatically.
