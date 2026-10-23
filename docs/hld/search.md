# HLD: Notebook search and page switcher

Design background: [research/onenote-search.md](../../research/onenote-search.md) (how OneNote 16 indexes and searches).

## What it does

- **Find in notebooks** (Ctrl+Shift+F, prefilled with the editor selection): a panel under the editor that searches as you type (250 ms debounce), scoped to all notebooks, this notebook, this section or this page. Results are grouped by page with every matching line and the matches highlighted; clicking a line opens the page with the match selected. Recent searches are kept in the session.
- **Query syntax**: words (all must appear somewhere on the page, title included), `"exact phrase"`, `a OR b`, `-exclude`, `#tag` (also matches `#tag/child`), `is:todo`, `is:done`, `is:task`.
- **Matching**: case- and accent-insensitive (Hebrew points too). Terms of 3+ characters match anywhere, including inside words — so `מטבח` finds `במטבח` and `parse` finds `ParseQuery`; shorter terms match at word starts.
- **Go to page** (Ctrl+E): fuzzy title match over all pages and open files; recent pages first when the box is empty.
- Unsaved edits are searched (the open buffer wins over the file), and open files outside notebooks are searched too when the scope is "All notebooks".

## Architecture

```
search box ─► SearchQuery.Parse ─► AndNode
                 │
SearchService.SearchAsync, per notebook in scope:
   1. NotebookIndex.Update()            incremental: stat files, re-index changed (size/mtime + SHA-256), drop deleted
   2. NotebookIndex.Candidates(query)   query tree compiled to parameterised SQL over FTS5
   3. + dirty open buffers in scope     the "delta" the index can't know about
   4. TextMatcher on the real text      final decision + highlight ranges (batches of 32, in parallel)
   ─► IAsyncEnumerable<PageResult> streamed to the panel
```

If an index can't be opened or queried, that notebook falls back to scanning all its page files (logged), so search always works.

| File | Responsibility |
|---|---|
| `Core/Search/SearchQuery.cs` | Tokenizer/parser → `AndNode`/`OrNode`/`NotNode`/`TermNode`/`TagNode`/`TaskNode`. |
| `Core/Search/TextFolding.cs` | Lower-case + strip combining marks, with a map back to original character positions. |
| `Core/Search/TextMatcher.cs` | Page-level evaluation (title + lines + facts) and per-line highlight ranges. |
| `Core/Search/PageFacts.cs` | `#tags` (inline, front matter `tags:`), task items; code is ignored. |
| `Core/Search/MarkdownBlocks.cs` | Paragraph / heading / list item / table / fenced block splitting with start lines. |
| `Core/Search/NotebookIndex.cs` | SQLite schema, incremental indexing, SQL compiler, tags and tasks queries. |
| `Core/Search/SearchService.cs` | Index lifecycle per notebook, the four-step search, fallback. |
| `Core/Search/FuzzyMatcher.cs` | Subsequence scoring for Ctrl+E. |
| `App/ViewModels/MainViewModel.Search.cs` | Panel state, debounce/cancel, scope resolution, keeping indexes fresh, recent pages, switcher items. |
| `App/Views/SearchPanel.xaml(.cs)`, `HighlightedText.cs`, `QuickOpenWindow.xaml(.cs)` | UI. |

## Index storage contract

One SQLite file per notebook: `%LOCALAPPDATA%\Ultiviewer\Index\<first 16 hex of SHA-256(upper-case root path)>.db` (WAL mode). It is a cache: deleting it is always safe; a schema-version or root mismatch, or a corrupt file, rebuilds it.

```sql
Meta(key, value)                                   -- schema = 1, root = <notebook path>
Pages(id, path UNIQUE NOCASE, title, title_folded, mtime, size, hash)   -- path relative, '/' separators
Blocks(id, page_id, line, text)                    -- text is FOLDED (TextFolding), one row per block
Tags(page_id, line, tag)                           -- lower case, without '#'
Tasks(page_id, line, done, text)
BlocksTri / TitlesTri    fts5 trigram, external content   -- terms >= 3 chars: substring match
BlocksWords / TitlesWords fts5 unicode61, prefix='1 2'     -- terms < 3 chars: word-prefix match
triggers keep the FTS tables in sync with Blocks/Pages
```

Storing folded text means the FTS tables never need accent/case support of their own and always propose a superset of what `TextMatcher` accepts.

Query compilation examples (values are bound parameters, never concatenated):

| Query node | SQL |
|---|---|
| term ≥ 3 | `id IN (SELECT page_id FROM Blocks WHERE id IN (SELECT rowid FROM BlocksTri WHERE BlocksTri MATCH $p) UNION SELECT rowid FROM TitlesTri WHERE TitlesTri MATCH $p)` with `$p = "folded term"` |
| term < 3 | same over `BlocksWords`/`TitlesWords` with `"te"*` |
| `#tag` | `id IN (SELECT page_id FROM Tags WHERE tag = $t OR tag LIKE $t/% ESCAPE '\')` |
| `is:todo` | `id IN (SELECT page_id FROM Tasks WHERE done = 0)` |
| AND / OR / NOT | `( … AND … )`, `( … OR … )`, `NOT ( … )` |
| scope | `path LIKE 'Section/%'` or `path = 'Section/Page.md'` |

## Keeping indexes fresh

- On start and whenever the notebook list changes: `SetNotebooks` + `UpdateAll` in the background.
- After the app saves a page: `Refresh(path)`.
- On watcher file events: page file → `Refresh(path)`; anything else (folder renamed/moved/deleted) → `Update()` of that notebook.
- Before every search: `Update()` of each notebook in scope (cheap when nothing changed).

## Extending safely

- New query syntax: add a node in `SearchQuery`, evaluate it in `TextMatcher.Evaluate` **and** compile it in `NotebookIndex.Compile` — the SQL must never exclude a page the matcher would accept.
- Schema changes: bump `NotebookIndex.SchemaVersion`; old indexes rebuild automatically.
- `TextMatcher` is the single source of truth for what matches; keep the index a superset.
