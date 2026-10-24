# HLD: Page properties, saved searches and the results table

## What it does

- **Properties.** Pages can carry properties:
  - front matter `key: value` lines, including `[a, b]` lists and `- item` lists;
  - Dataview-style fields in the text: a `key:: value` line, or `[key:: value]` / `(key:: value)` inside a
    sentence.

  Search can query them:

  | Query | Matches |
  |---|---|
  | `status:done` | equal, case- and accent-insensitive |
  | `priority:>=2`, `<`, `<=`, `>` | numbers numerically, other values as text, so ISO dates work: `due:<2026-11-01` |
  | `project:*` | the page has the property |
  | `title:*plan*` | wildcards |
  | `client:"Acme Corp"` | a value with spaces |
  | `-status:draft` | exclusion |

  A list property matches if any of its values does. Property queries combine with words, tags, `OR` and `is:todo`
  as usual. Some colon words stay plain text:
  - URLs and paths (`https://…`, `C:\…`), because the value starts with `/` or `\`;
  - `TODO:` with nothing after the colon;
  - times such as `10:30`, because the key must start with a letter;
  - `is:…`;
  - a quoted `"status:done"`.
- **Results table.** The **Table** button in the search panel shows one row per page with these columns:
  - Location;
  - up to 8 property columns: first the keys the query names, then the most common keys among the results;
  - Modified;
  - Matches.

  Click a header to sort by it, and click again to reverse. Numbers sort numerically, and empty values stay last
  in both directions. Clicking a row opens the page.
- **Saved searches.** The star button saves the current query with its scope and view (list or table) under a
  name. Saved searches appear in a collapsible **Saved searches** section above the notebook tree:
  - Click or Enter runs one.
  - F2 renames it, Del deletes it, and the context menu also has Move up.
  - A saved "This notebook / section / page" scope is stored as that path. Running the search adds an
    "In Notebook › Section" choice to the scope box.

## Main code

| File | Responsibility |
|---|---|
| `Core/Search/PageFacts.cs` | `Properties` extraction (front matter and inline fields; code ignored; keys lower-cased). |
| `Core/Search/SearchQuery.cs` | `PropertyNode` / `PropertyOp` parsing, `key:"quoted"` tokens, `PropertyKeys`. |
| `Core/Search/PropertyValues.cs` | `Matches`, `Compare`, `CompareForSort`, `Fold`, `TryNumber`. Shared by the matcher, the index and the table. |
| `Core/Search/TextMatcher.cs` | Evaluates property nodes; highlights the property lines. |
| `Core/Search/NotebookIndex.cs` | `Properties` table (schema 3), insertion, SQL compilation (`PropertyCondition`). |
| `Core/Search/SearchService.cs` | `PageResult.Properties` and `Modified`. |
| `Core/Settings/SavedSearch.cs`, `AppSettings.SavedSearches` | The stored searches (`settings.json`, roaming). |
| `Core/Settings/SessionState.cs` | `SearchAsTable`, `SavedSearchesExpanded`. |
| `App/ViewModels/MainViewModel.SavedSearches.cs` | Save, run, rename, delete and reorder; the fixed scope choice; `TableColumns`. |
| `App/ViewModels/SearchResultViewModels.cs` | `SearchPageViewModel.Properties` (`PropertyBag`), `Modified`; `SearchScopeOption.Fixed`. |
| `App/Views/SearchPanel.xaml`, `SearchPanel.Table.cs` | Table view, columns, sorting (`ResultComparer`), opening rows. |
| `App/Views/SavedSearchesPane.xaml(.cs)` | The list above the tree. |

## Data flow

1. **Indexing.** `PageFacts.Extract` returns tags, tasks and properties. `NotebookIndex` stores each property value
   twice: folded (`PropertyValues.Fold`) and, when it parses as a number (invariant culture), as `num`.
2. **Searching.** The index proposes candidates with SQL that mirrors `PropertyValues.Matches`:
   - A numeric query compares `num` and falls back to text for non-numeric values.
   - A text query compares `value_folded`.
   - A wildcard becomes `LIKE`.

   `TextMatcher` then makes the final decision on the page text and returns the matching property lines as hits.
3. **Display.** The service attaches the page's properties and its modified time to every `PageResult`. After the
   search completes, `UpdateTableColumns` picks the columns and publishes them as one new list. The view
   rebuilds its `GridView` once and reapplies the current sort through a `ListCollectionView.CustomSort`. The
   table's view is separate from the tree's, so sorting doesn't reorder the line view.

## Contracts

- **`settings.json → savedSearches`:** `[{ "name", "query", "scopePath" (null = all notebooks), "showAsTable" }]`,
  in display order.
- **Index schema 3:** `Properties(page_id, line, key NOCASE, value_folded, num)`. An index with an older schema
  rebuilds on first use.

## Extending safely

- **New operators** (for example "contains"): add them to `PropertyOp` and the parser, then implement them in
  `PropertyValues.Matches` **and** `NotebookIndex.PropertyCondition`. The SQL must stay a superset of the
  matcher.
- **More property sources** (for example task metadata or tags as a property): add them in
  `PageFacts.Extract`, then bump `NotebookIndex.SchemaVersion` so existing indexes pick them up.
- **Table columns** come only from `MainViewModel.TableColumns`. Fixed columns are added in
  `SearchPanel.BuildColumns` with a sort key that `ResultComparer` understands.
- **Testing:** the snapshot tool steps `table`, `sortBy`, `prompt` + `saveSearch` and `runSaved`, and
  `print: "table"` / `"saved"`, cover these off-screen.
