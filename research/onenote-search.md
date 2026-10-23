# How OneNote search works, and what Ultiviewer takes from it

How this was found: static analysis of OneNote desktop **16.0.20430.20182**. Nothing was executed, and none of its code is copied into Ultiviewer; only design ideas are taken.

- `onmain.dll` (24.5 MB, OneNote's engine) and `ONFILTER.DLL`. Microsoft's public symbol server has their PDBs: about 112K function names. The binary was rearranged after linking, so addresses were translated through the PDB's OMAP tables.
- Strings embedded in the binary, including SQL statements and feature-flag names.
- Spot disassembly (capstone) of the functions that build queries and write the index.
- A read-only copy of the index OneNote keeps on this machine: `%LOCALAPPDATA%\Microsoft\OneNote\16.0\FullTextSearchIndex\*.db`. Only its structure and tokenization were looked at, not note content.

"Jot" is OneNote's internal codename. Every class below is `Jot::…`.

## 1. Two generations of indexing

| | Legacy (OneNote 2007–2016 era) | Current |
|---|---|---|
| Engine | Windows Search (WDS) | **SQLite FTS4**, statically linked |
| Who stores the index | The Windows Search service | OneNote itself: one `.db` per notebook in `%LOCALAPPDATA%` |
| How content gets in | OneNote pushes change batches to WDS (`CIndexWorkerThread::SendBatchToWDS`, with a watchdog timer). The crawler loads `ONFILTER.DLL` (`CProtocolHandler`, `CProtocolAccessor`, `CIndexingIFilter`), which pulls page text back from the running OneNote over a **named pipe** (`CIndexerServer::CreateNewListeningPipe`). | A background plugin indexer, `Jot::Indexer::SearchPluginSession::AddOrModifyPage`, writes rows directly. |
| Query | `SELECT … FROM SystemIndex..SCOPE() WHERE …` | A condition tree compiled to SQL with FTS `MATCH` |
| Feature flags | `EnableWDSIndexing` | `SQLite.IndexingEnabled`, `SQLite.SearchEnabled` |

During the migration Microsoft ran both engines side by side and compared the results (`SQLiteShadowWDSComparisonEnabled`, `IsShadowSQLiteComparisonEnabled`). The rest of this document describes the SQLite design.

## 2. The SQLite index

### Files

```
%LOCALAPPDATA%\Microsoft\OneNote\16.0\
  MasterIndex\Master.db                       plugin registry + global indexing checkpoint
  FullTextSearchIndex\{notebook-guid}{N}.db   one per notebook (search plugin, schema v15)
  AccessibilityCheckerIndex\{...}.db          a second plugin on the same indexer framework
```

- The index is **per notebook** (`ASqlDatabasePerNotebookPluginSessionBase`), **outside** the notebook, and treated as disposable. On schema mismatch or a corrupt database it is deleted and rebuilt (`IsNotebookSchemaValid`, `SqlException::IsDatabaseInvalid`, `PluginsManager::DeleteDatabase`).
- The indexer is generic. Plugins (`SearchIndexerPlugin`, `AccCheckerIndexerPlugin`) receive the same `AddOrModifyNotebook / Section / SectionGroup / Page` and `DeleteEntity` callbacks, and each one keeps its own database and schema version (the `Plugins` table in `Master.db`).
- Connection settings: `PRAGMA journal_mode = TRUNCATE`, `temp_store = MEMORY`, `recursive_triggers = ON`, plus a busy timeout.

### Schema (from the binary; it matches the live database)

```sql
CREATE TABLE Entities (rowid INTEGER PRIMARY KEY, Type INTEGER,          -- 1=page 2=section 4=notebook
  GOID, GUID, GOSID, ParentGOID, GrandparentGOIDs TEXT,                  -- ancestor ids, denormalized
  ContentRID, RootRevGenCount INTEGER,                                   -- revision stamp for "up to date?"
  LastModifiedTime, RecentTime, PinTime, Color, Title, EnterpriseIdentity);
CREATE VIRTUAL TABLE EntityContents      USING fts4(tokenize=simple '' ' *-', Terms);   -- titles
CREATE TABLE PageElements (rowid INTEGER PRIMARY KEY, GOID, Text, Jcid, EntityRowId);   -- one row per paragraph/image
CREATE VIRTUAL TABLE PageElementContents USING fts4(tokenize=simple '' ' *-', Terms);
CREATE TABLE NoteFlags (rowid, Type, Shape, Status, Label, PageElementRowId);           -- tags / to-dos
CREATE VIRTUAL TABLE NoteFlagContents    USING fts4(tokenize=simple '' ' *-', Terms);
CREATE TABLE Hashtags (rowid, PageElementRowId);
CREATE VIRTUAL TABLE HashtagContents     USING fts4(tokenize=simple '' ' *-', Hashtag);
CREATE TABLE Metadata (rowid, SchemaVersion);
-- AFTER DELETE triggers on each base table remove the matching FTS row (shared rowid).
```

**Granularity is the page element, not the page.** Each paragraph, and each image with OCR text, is its own row; this notebook averages about 7 per page. A hit points at a paragraph, which gives snippets, per-paragraph highlighting and "N matches on this page" almost for free. Here 1,544 rows were rich text (`jcid 0x6000E`) and 5 were images (`0x60011`).

### Tokenization: OneNote does it, not SQLite

- The FTS tables use the `simple` tokenizer, which splits only on space, `*` and `-`.
- OneNote runs the **Windows/Office word breaker for the text's language** first (`CWordBreakerPool`, `CSingleLocaleWordBreakerAdapter`, `IWordSink::PutWord/PutAltWord`). It then case-folds each token (`Jot::FoldCase`) and stores a space-separated token string.
- Each token is followed by its alternate form. Examples from the live index:

```
"http://norvig.com/21-days.html" → http http norvig norvig com com 21 21 days days html html
"1. בין הכיור"                  → 1 nn1 בין בין הכיור הכיור          (numbers get an "nnN" alt form)
"compared"                      → compared compared compare compare    (English stem added at index time)
```

- Russian and Hebrew words got **no** stems or alternates. Hebrew prefix letters stay attached (`במטבח`, "in the kitchen"), so a token search for `מטבח` ("kitchen") does not find that word through the index.
- Feature flags show this tokenization was tuned repeatedly: `WordBreakerStripPunctuation`, `TermSkipPunctuation`, `SplitFtsTermOnDisallowedChars`, `WordsSegmenterIndexing`, `WordsSegmenterLocaleSanitization`, `SearchMaxStemForms`, `SearchStemExpansionV2`, `PhraseSearchSkipIndexStems`.

### Incremental indexing

- Every entity stores the revision it was indexed at (`RootRevGenCount`). `IsEntityUpToDate` compares it with the live revision and skips unchanged pages.
- `Master.db` holds a global checkpoint: `IndexedRID` is the last fully indexed revision and `PendingIndexRID` is the one in progress. `IsResetting` marks a rebuild underway, so an interrupted run resumes instead of starting over.
- `CleanObsoleteNotebooks` removes databases for notebooks that are no longer open.
- Indexing runs on a background scheduler. Two flags prioritize work the user is waiting for: `SQLite.StartIndexingOnSearch` (a search kicks off `SqlIndexer::ForceIndex`) and `SQLite.IndexActivePage`.

## 3. The query pipeline

```
search box text
  → CSearchQuery::FromString        terms, "quoted phrases", operators
  → TermsToMatch                    Match tree: And / Or / Not / Phrase / Near / Prefix / Exact / Substring
       each term → Or(term, stems…) stems from StemCache (hash-map cache over the Windows IStemmer)
  → Conditions tree                 And/Or/Not + TitleMatch, PageElementTextMatch, NoteFlagMatch,
                                    HashtagMatch, LastModifiedTimeGreaterThan, IsEncrypted,
                                    UnderPage/Section/SectionGroup/Notebook/RecycleBin…
  → SQL compiler (recursive visitor, sub_5734b0)
```

The compiler emits these shapes. Values such as ids are bound as parameters (`CSqlQuery::CreateBinding`), not pasted into the SQL:

```sql
(…) AND (…)    (…) OR (…)    NOT (…)
-- title
Entities.rowid IN (SELECT EntityContents.rowid FROM EntityContents WHERE EntityContents.Terms MATCH ?)
-- body text
Entities.rowid IN (SELECT PageElements.EntityRowId FROM PageElements WHERE
  PageElements.rowid IN (SELECT PageElementContents.rowid FROM PageElementContents
                         WHERE PageElementContents.Terms MATCH ?))
-- tags / hashtags (prefix match)
… NoteFlagContents.Terms MATCH 'term*' …   … HashtagContents.Hashtag MATCH 'term*' …
-- scope
Entities.GOID = ?                                                          -- page
Entities.GOID = ? OR Entities.ParentGOID = ?                               -- section
… OR Entities.GrandparentGOIDs LIKE '%' || ? || '%'                        -- section group / notebook
```

The `MATCH` string comes from the Match tree (`sub_57293c`):

| Match node | FTS text |
|---|---|
| And | children separated by a space |
| Or | `" OR "` |
| Not | `-term` |
| Prefix | `term*` |
| Exact | `term` |
| Phrase | `"a b c"` |
| Near | `a NEAR/n b` |

Every term goes through `ToSqlFtsTerm`, which case-folds it, converts it to UTF-8, and rejects or splits on space, `-`, `*` and tab.

**Scope uses a denormalized ancestor column.** `GrandparentGOIDs` holds all ancestor ids as text. "Everything under this section group" is then a single `LIKE` instead of a recursive query over the hierarchy.

## 4. One search session, several providers

`CSearchSession::StartSearch` starts several **providers** in parallel. Each reports hits into a shared sink:

1. **SQL index provider** (`CSqlIndexerSearchHitSink`): returns candidate pages and paragraphs quickly.
2. **Grep provider** (`CGrepSearchHitSink`, `CTextSearchPlugin`): walks the live in-memory pages and runs the Match tree word by word (`Match::MatchWordSink`, `TransformForWord`). It does three jobs:
   - covers the **delta**, pages changed since they were indexed;
   - confirms candidates and computes exact highlight positions (`GetHighlightCPs`, `CpMatchPrefix`, `CpMatchSubstring`);
   - removes hits that no longer match after an edit (`OnMissingInDelta` → `ReportDeleted`).
3. Others: `CNoteTagSearchPlugin`, `CDateSearchPlugin`, `CAuthorSearchPlugin`, `CQuotedTextSearchPlugin`, `CNotebookEntitySearchPlugin` (section and notebook names), `CRecentSearchResultsSearchPlugin`, linked notes, and cloud (`CGraphSearchPluginBase`).

**Fallback:** when the index provider fails, `MarkAsCompleted` queues a **full grep** and logs `SearchSessionFallingBackToFullGrep`. Search still works without an index, only slower.

**UI streaming:**
- `CSearchController_IndexedSearch` receives `OnHitAdded / Removed / Updated` and sends hits to the UI in batches whose size adapts (`FlushPendingHitsToUI`, `GetAdaptiveBatchSize`).
- Changing the search text cancels and restarts the session (`IndexedSearchManager::OnSearchTermChanged → RestartSearch`).
- A `CQueryScope` sets the scope: page, section, section group, notebook or all notebooks.

**Content beyond typed text:**
- OCR of images: `COcrManager` runs in the background with suspend/resume. It uses either a local engine (`CMicrosoftOCRHelper`, `MSOCR.DLL`) or a cloud service (`AugLoop::Ocr`). The text is stored as a `PageElements` row for the image.
- Handwriting recognition (`ProcessInkAnalysisTree`) and audio (`FParseOneAudioUrl`) in the legacy path.

## 5. What Ultiviewer takes from this

| OneNote design | Ultiviewer version |
|---|---|
| One disposable SQLite index per notebook, outside the notebook | `%LOCALAPPDATA%\Ultiviewer\Index\{notebook-id}.db`. Never stored in the synced folder. Deleted and rebuilt on schema mismatch or corruption. |
| Index rows are **page elements** | Index **Markdown blocks** (heading, paragraph, list item, table, code block) with their start line, from Markdig. A result opens the page **at that line**. |
| Own tokenization with the Windows word breaker | SQLite **FTS5 `unicode61 remove_diacritics 2`**: case- and diacritic-insensitive for every script, with no COM word breakers. Add `prefix='2 3'` so prefix queries are fast. |
| Hebrew prefix letters and CJK break token search; OneNote relies on substring matching in grep | A second FTS5 table with the **`trigram`** tokenizer for substring search. It is used for terms like `מטבח` and for code identifiers. The grep step remains the final check. |
| `RootRevGenCount` per entity, plus a resumable checkpoint | Store `mtime + size + content hash` per page and skip it when unchanged. A `Pending` flag on the index makes rebuilds resumable. |
| Index plus a grep "delta", with full grep as fallback | Query the index, then grep **open editor buffers and files changed since indexing**, and merge. If the index is missing or broken, do a full parallel grep. |
| Grep confirms and highlights | Final match and highlight positions are computed on the actual text, so snippets are exact. |
| `GrandparentGOIDs LIKE` for scope | Store the page path and filter scope with `path LIKE 'Projects/Ultiviewer/%'`. |
| Hashtags and NoteFlags tables | `Tags` (`#tag`, front-matter `tags:`) and `Tasks` (`- [ ]` / `- [x]`) tables. Enables `#tag`, `is:todo` and `is:done` filters and a to-do summary. |
| Query → Match tree → Conditions → SQL compiler | The same layering in `Ultiviewer.Core/Search/`: `QueryParser → QueryNode → SqlCompiler`, with all values bound as parameters. |
| Streaming hit sink, adaptive batches, restart on edit | `IAsyncEnumerable<Hit>` with a `CancellationToken`. The UI adds results in batches; typing debounces and restarts the search. |
| `StartIndexingOnSearch`, `IndexActivePage` | A search first indexes pages that are dirty or not yet indexed, and the active page is always indexed first. |
| Stems added at index time (English only) | Skip stemming in v1. If wanted later, expand at **query** time (OneNote's `SearchStemExpansionV2` direction). |
| Background OCR of images | Later: `Windows.Media.Ocr`, the built-in Windows OCR API. It needs no extra DLLs; requires the `net10.0-windows10.0.19041` target. OCR text is indexed as a block of the page that embeds the image. |

Things to **avoid**, judging from OneNote's history:

- **Relying on Windows Search.** OneNote has dialog strings for an indexer that is not installed or stopped (`DontRemindIndexerNotInstalledProperly`, `ErrIndex_WDSIndexerIsDead`) and needed a watchdog timer. Microsoft moved to its own SQLite index.
- **Duplicating tokens in the FTS column.** Writing alternates and stems into the token stream breaks phrase positions. It led to `PhraseSearchSkipIndexStems`, `QuotedPhraseGroup` and `FixPhraseCompositeChildDispatch`. Keep the FTS column as clean text and handle variants in the query.
