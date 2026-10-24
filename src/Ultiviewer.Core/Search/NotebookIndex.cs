using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Ultiviewer.Core.Files;
using Ultiviewer.Core.Notebooks;

namespace Ultiviewer.Core.Search;

public readonly record struct TagCount(string Tag, int Pages);

public sealed record TaskRow(string Path, string Title, int Line, bool Done, string Text);

/// <summary>
/// Disposable full-text index of one notebook, kept outside the notebook
/// (<c>%LOCALAPPDATA%\Ultiviewer\Index\&lt;id&gt;.db</c>). Rows are Markdown blocks, stored case- and accent-folded
/// (<see cref="TextFolding"/>) so the index proposes a superset of what <see cref="TextMatcher"/> accepts.
/// <list type="bullet">
/// <item>Terms of 3+ characters query trigram FTS5 tables (substring match: Hebrew prefixes, code identifiers).</item>
/// <item>Shorter terms query unicode61 tables as word prefixes.</item>
/// <item>Pages are re-indexed only when size/mtime change and the content hash differs.</item>
/// <item>A schema or root mismatch, or a corrupt file, rebuilds the index from scratch.</item>
/// </list>
/// All methods are thread-safe (one connection, serialized).
/// </summary>
public sealed class NotebookIndex : IDisposable
{
    /// <summary>2: attachments (PDF and image text) next to pages; 3: page properties.</summary>
    public const int SchemaVersion = 3;

    private readonly object _gate = new();
    private SqliteConnection _db;

    private NotebookIndex(string root, string databasePath)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        DatabasePath = databasePath;
        _db = Connect();
    }

    public string Root { get; }

    public string DatabasePath { get; }

    public static string IdFor(string root) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant())))[..16].ToLowerInvariant();

    /// <summary>Opens (or creates, or rebuilds) the index for <paramref name="root"/> in <paramref name="folder"/>.</summary>
    public static NotebookIndex Open(string root, string folder)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, IdFor(root) + ".db");
        try
        {
            return new NotebookIndex(root, path);
        }
        catch (SqliteException)
        {
            SqliteConnection.ClearAllPools();
            DeleteDatabaseFiles(path);
            return new NotebookIndex(root, path);
        }
    }

    private SqliteConnection Connect()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA temp_store=MEMORY; PRAGMA foreign_keys=ON;");

            var hasMeta = Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='Meta'") is long n && n > 0;
            if (!hasMeta)
            {
                CreateSchema(connection);
                return connection;
            }

            var version = Scalar(connection, "SELECT value FROM Meta WHERE key='schema'") as string;
            var root = Scalar(connection, "SELECT value FROM Meta WHERE key='root'") as string;
            Execute(connection, "SELECT count(*) FROM Pages"); // throws on a damaged file
            if (version == SchemaVersion.ToString() && string.Equals(root, Root, StringComparison.OrdinalIgnoreCase))
            {
                return connection;
            }
        }
        catch
        {
            connection.Dispose(); // release the file so the caller can delete and rebuild it
            throw;
        }

        // Another schema version or another root: start over.
        connection.Dispose();
        SqliteConnection.ClearAllPools();
        DeleteDatabaseFiles(DatabasePath);
        return Connect();
    }

    private void CreateSchema(SqliteConnection connection)
    {
        using var transaction = connection.BeginTransaction();
        Execute(connection, """
            CREATE TABLE Meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE Pages(
                id INTEGER PRIMARY KEY,
                path TEXT NOT NULL UNIQUE COLLATE NOCASE,
                title TEXT NOT NULL,
                title_folded TEXT NOT NULL,
                mtime INTEGER NOT NULL,
                size INTEGER NOT NULL,
                hash TEXT NOT NULL,
                kind INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE AttachmentPages(page_id INTEGER NOT NULL REFERENCES Pages(id) ON DELETE CASCADE, page INTEGER NOT NULL, text TEXT NOT NULL);
            CREATE INDEX AttachmentPages_page ON AttachmentPages(page_id);
            CREATE TABLE Blocks(id INTEGER PRIMARY KEY, page_id INTEGER NOT NULL REFERENCES Pages(id) ON DELETE CASCADE, line INTEGER NOT NULL, text TEXT NOT NULL);
            CREATE INDEX Blocks_page ON Blocks(page_id);
            CREATE TABLE Tags(page_id INTEGER NOT NULL REFERENCES Pages(id) ON DELETE CASCADE, line INTEGER NOT NULL, tag TEXT NOT NULL COLLATE NOCASE);
            CREATE INDEX Tags_tag ON Tags(tag);
            CREATE INDEX Tags_page ON Tags(page_id);
            CREATE TABLE Tasks(page_id INTEGER NOT NULL REFERENCES Pages(id) ON DELETE CASCADE, line INTEGER NOT NULL, done INTEGER NOT NULL, text TEXT NOT NULL);
            CREATE INDEX Tasks_page ON Tasks(page_id);
            CREATE TABLE Properties(page_id INTEGER NOT NULL REFERENCES Pages(id) ON DELETE CASCADE, line INTEGER NOT NULL, key TEXT NOT NULL COLLATE NOCASE, value_folded TEXT NOT NULL, num REAL);
            CREATE INDEX Properties_key ON Properties(key, value_folded);
            CREATE INDEX Properties_page ON Properties(page_id);

            CREATE VIRTUAL TABLE BlocksTri USING fts5(text, content='Blocks', content_rowid='id', tokenize='trigram');
            CREATE VIRTUAL TABLE BlocksWords USING fts5(text, content='Blocks', content_rowid='id', tokenize='unicode61', prefix='1 2');
            CREATE VIRTUAL TABLE TitlesTri USING fts5(title_folded, content='Pages', content_rowid='id', tokenize='trigram');
            CREATE VIRTUAL TABLE TitlesWords USING fts5(title_folded, content='Pages', content_rowid='id', tokenize='unicode61', prefix='1 2');

            CREATE TRIGGER Blocks_ai AFTER INSERT ON Blocks BEGIN
                INSERT INTO BlocksTri(rowid, text) VALUES (new.id, new.text);
                INSERT INTO BlocksWords(rowid, text) VALUES (new.id, new.text);
            END;
            CREATE TRIGGER Blocks_ad AFTER DELETE ON Blocks BEGIN
                INSERT INTO BlocksTri(BlocksTri, rowid, text) VALUES ('delete', old.id, old.text);
                INSERT INTO BlocksWords(BlocksWords, rowid, text) VALUES ('delete', old.id, old.text);
            END;
            CREATE TRIGGER Pages_ai AFTER INSERT ON Pages BEGIN
                INSERT INTO TitlesTri(rowid, title_folded) VALUES (new.id, new.title_folded);
                INSERT INTO TitlesWords(rowid, title_folded) VALUES (new.id, new.title_folded);
            END;
            CREATE TRIGGER Pages_ad AFTER DELETE ON Pages BEGIN
                INSERT INTO TitlesTri(TitlesTri, rowid, title_folded) VALUES ('delete', old.id, old.title_folded);
                INSERT INTO TitlesWords(TitlesWords, rowid, title_folded) VALUES ('delete', old.id, old.title_folded);
            END;
            CREATE TRIGGER Pages_au AFTER UPDATE OF title_folded ON Pages BEGIN
                INSERT INTO TitlesTri(TitlesTri, rowid, title_folded) VALUES ('delete', old.id, old.title_folded);
                INSERT INTO TitlesWords(TitlesWords, rowid, title_folded) VALUES ('delete', old.id, old.title_folded);
                INSERT INTO TitlesTri(rowid, title_folded) VALUES (new.id, new.title_folded);
                INSERT INTO TitlesWords(rowid, title_folded) VALUES (new.id, new.title_folded);
            END;
            """);
        Execute(connection, "INSERT INTO Meta(key, value) VALUES ('schema', $v), ('root', $r)", ("$v", SchemaVersion.ToString()), ("$r", Root));
        transaction.Commit();
    }

    // ---- Updating ----

    /// <summary>Brings the index in line with the folder: (re)indexes changed pages, drops deleted ones.</summary>
    public (int Indexed, int Removed) Update(CancellationToken cancellationToken = default)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in NotebookScanner.EnumeratePageFiles(Root))
        {
            files[Relative(file)] = file;
        }

        lock (_gate)
        {
            var known = new Dictionary<string, (long Mtime, long Size)>(StringComparer.OrdinalIgnoreCase);
            using (var command = Command(_db, "SELECT path, mtime, size FROM Pages WHERE kind = 0"))
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    known[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2));
                }
            }

            var indexed = 0;
            var transaction = _db.BeginTransaction();
            try
            {
                var pendingInBatch = 0;
                foreach (var (relative, full) in files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (FileStamp.Read(full) is not { } stamp)
                    {
                        continue;
                    }

                    if (known.TryGetValue(relative, out var row) && row.Mtime == stamp.LastWriteUtc.Ticks && row.Size == stamp.Length)
                    {
                        continue;
                    }

                    if (IndexFileCore(full, relative, stamp))
                    {
                        indexed++;
                    }

                    if (++pendingInBatch >= 200)
                    {
                        transaction.Commit();
                        transaction.Dispose();
                        transaction = _db.BeginTransaction();
                        pendingInBatch = 0;
                    }
                }

                var removed = 0;
                foreach (var relative in known.Keys.Where(k => !files.ContainsKey(k)))
                {
                    RemoveCore(relative);
                    removed++;
                }

                transaction.Commit();
                return (indexed, removed);
            }
            finally
            {
                transaction.Dispose();
            }
        }
    }

    /// <summary>Indexes (or removes, if gone) one file after a save or a change on disk.</summary>
    public void Refresh(string fullPath)
    {
        if (!IsInside(fullPath) || !NotebookScanner.IsPageFile(fullPath))
        {
            return;
        }

        lock (_gate)
        {
            using var transaction = _db.BeginTransaction();
            var relative = Relative(fullPath);
            if (FileStamp.Read(fullPath) is { } stamp)
            {
                IndexFileCore(fullPath, relative, stamp);
            }
            else
            {
                RemoveCore(relative);
            }

            transaction.Commit();
        }
    }

    private bool IndexFileCore(string full, string relative, FileStamp stamp)
    {
        byte[] bytes;
        try
        {
            bytes = TextDocumentFile.ReadAllBytesShared(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        var hash = TextDocumentFile.Hash(bytes);
        long? id = null;
        string? oldHash = null;
        using (var find = Command(_db, "SELECT id, hash FROM Pages WHERE path = $p", ("$p", relative)))
        using (var reader = find.ExecuteReader())
        {
            if (reader.Read())
            {
                id = reader.GetInt64(0);
                oldHash = reader.GetString(1);
            }
        }

        if (id is { } existing && oldHash == hash)
        {
            Execute(_db, "UPDATE Pages SET mtime = $m, size = $s WHERE id = $id", ("$m", stamp.LastWriteUtc.Ticks), ("$s", stamp.Length), ("$id", existing));
            return false;
        }

        var text = TextFileFormat.Decode(bytes, out _);
        var title = Path.GetFileNameWithoutExtension(full);
        if (id is { } pageId)
        {
            DeletePageRows(pageId);
            Execute(_db, "UPDATE Pages SET title = $t, title_folded = $tf, mtime = $m, size = $s, hash = $h WHERE id = $id",
                ("$t", title), ("$tf", TextFolding.Fold(title)), ("$m", stamp.LastWriteUtc.Ticks), ("$s", stamp.Length), ("$h", hash), ("$id", pageId));
        }
        else
        {
            using var insert = Command(_db, "INSERT INTO Pages(path, title, title_folded, mtime, size, hash) VALUES ($p, $t, $tf, $m, $s, $h) RETURNING id",
                ("$p", relative), ("$t", title), ("$tf", TextFolding.Fold(title)), ("$m", stamp.LastWriteUtc.Ticks), ("$s", stamp.Length), ("$h", hash));
            id = (long)insert.ExecuteScalar()!;
        }

        using (var block = Command(_db, "INSERT INTO Blocks(page_id, line, text) VALUES ($id, $l, $t)", ("$id", id), ("$l", 0), ("$t", "")))
        {
            foreach (var item in MarkdownBlocks.Split(text))
            {
                block.Parameters["$l"].Value = item.Line;
                block.Parameters["$t"].Value = TextFolding.Fold(item.Text);
                block.ExecuteNonQuery();
            }
        }

        var facts = PageFacts.Extract(text);
        using (var tag = Command(_db, "INSERT INTO Tags(page_id, line, tag) VALUES ($id, $l, $t)", ("$id", id), ("$l", 0), ("$t", "")))
        {
            foreach (var item in facts.Tags)
            {
                tag.Parameters["$l"].Value = item.Line;
                tag.Parameters["$t"].Value = item.Tag;
                tag.ExecuteNonQuery();
            }
        }

        using (var task = Command(_db, "INSERT INTO Tasks(page_id, line, done, text) VALUES ($id, $l, $d, $t)", ("$id", id), ("$l", 0), ("$d", 0), ("$t", "")))
        {
            foreach (var item in facts.Tasks)
            {
                task.Parameters["$l"].Value = item.Line;
                task.Parameters["$d"].Value = item.Done ? 1 : 0;
                task.Parameters["$t"].Value = item.Text;
                task.ExecuteNonQuery();
            }
        }

        using (var property = Command(_db, "INSERT INTO Properties(page_id, line, key, value_folded, num) VALUES ($id, $l, $k, $v, $n)",
                   ("$id", id), ("$l", 0), ("$k", ""), ("$v", ""), ("$n", null)))
        {
            foreach (var item in facts.Properties)
            {
                property.Parameters["$l"].Value = item.Line;
                property.Parameters["$k"].Value = item.Key;
                property.Parameters["$v"].Value = PropertyValues.Fold(item.Value);
                property.Parameters["$n"].Value = PropertyValues.TryNumber(item.Value, out var number) ? number : DBNull.Value;
                property.ExecuteNonQuery();
            }
        }

        return true;
    }

    private void RemoveCore(string relative)
    {
        if (Scalar(_db, "SELECT id FROM Pages WHERE path = $p", ("$p", relative)) is long id)
        {
            DeletePageRows(id);
            Execute(_db, "DELETE FROM Pages WHERE id = $id", ("$id", id));
        }
    }

    private void DeletePageRows(long id)
    {
        Execute(_db, "DELETE FROM Blocks WHERE page_id = $id; DELETE FROM Tags WHERE page_id = $id; DELETE FROM Tasks WHERE page_id = $id; DELETE FROM Properties WHERE page_id = $id; DELETE FROM AttachmentPages WHERE page_id = $id;", ("$id", id));
    }

    // ---- Attachments ----

    /// <summary>
    /// Reads new or changed attachments (at most <paramref name="maxFiles"/> per call) and forgets deleted ones.
    /// The slow part (PDF parsing, OCR) runs outside the lock, so searches don't wait for it. An unreadable file
    /// is stored without text and only retried after it changes. Returns how many were read and how many wait.
    /// </summary>
    public (int Read, int Remaining) UpdateAttachments(IAttachmentReader reader, CancellationToken cancellationToken = default, int maxFiles = 10)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in AttachmentFiles.Enumerate(Root).Where(reader.CanRead))
        {
            files[Relative(file)] = file;
        }

        var known = new Dictionary<string, (long Mtime, long Size)>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            using (var command = Command(_db, "SELECT path, mtime, size FROM Pages WHERE kind = 1"))
            using (var rows = command.ExecuteReader())
            {
                while (rows.Read())
                {
                    known[rows.GetString(0)] = (rows.GetInt64(1), rows.GetInt64(2));
                }
            }

            using var transaction = _db.BeginTransaction();
            foreach (var gone in known.Keys.Where(k => !files.ContainsKey(k)).ToList())
            {
                RemoveCore(gone);
            }

            transaction.Commit();
        }

        var pending = files
            .Select(f => (Relative: f.Key, Full: f.Value, Stamp: FileStamp.Read(f.Value)))
            .Where(f => f.Stamp is { } stamp && (!known.TryGetValue(f.Relative, out var row) || row.Mtime != stamp.LastWriteUtc.Ticks || row.Size != stamp.Length))
            .ToList();
        var read = 0;
        foreach (var (relative, full, stamp) in pending.Take(maxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<string> pages;
            try
            {
                pages = reader.Read(full, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                pages = []; // damaged or locked: remember it, retry when the file changes
            }

            lock (_gate)
            {
                using var transaction = _db.BeginTransaction();
                StoreAttachment(relative, full, stamp!.Value, pages);
                transaction.Commit();
            }

            read++;
        }

        return (read, pending.Count - read);
    }

    /// <summary>The stored text of an attachment, one entry per page; null when the path isn't an indexed attachment.</summary>
    public IReadOnlyList<string>? AttachmentText(string fullPath)
    {
        if (!IsInside(fullPath))
        {
            return null;
        }

        lock (_gate)
        {
            if (Scalar(_db, "SELECT id FROM Pages WHERE path = $p AND kind = 1", ("$p", Relative(fullPath))) is not long id)
            {
                return null;
            }

            var count = Convert.ToInt32(Scalar(_db, "SELECT coalesce(max(page), 0) FROM AttachmentPages WHERE page_id = $id", ("$id", id)));
            var pages = Enumerable.Repeat("", count).ToArray();
            using var command = Command(_db, "SELECT page, text FROM AttachmentPages WHERE page_id = $id", ("$id", id));
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                pages[reader.GetInt32(0) - 1] = reader.GetString(1);
            }

            return pages;
        }
    }

    /// <summary>Forgets every attachment (searching attachments was turned off).</summary>
    public void RemoveAttachments()
    {
        lock (_gate)
        {
            Execute(_db, "DELETE FROM Blocks WHERE page_id IN (SELECT id FROM Pages WHERE kind = 1); DELETE FROM AttachmentPages; DELETE FROM Pages WHERE kind = 1;");
        }
    }

    private void StoreAttachment(string relative, string full, FileStamp stamp, IReadOnlyList<string> pages)
    {
        RemoveCore(relative);
        var title = Path.GetFileName(full);
        using var insert = Command(_db, "INSERT INTO Pages(path, title, title_folded, mtime, size, hash, kind) VALUES ($p, $t, $tf, $m, $s, '', 1) RETURNING id",
            ("$p", relative), ("$t", title), ("$tf", TextFolding.Fold(title)), ("$m", stamp.LastWriteUtc.Ticks), ("$s", stamp.Length));
        var id = (long)insert.ExecuteScalar()!;
        using var block = Command(_db, "INSERT INTO Blocks(page_id, line, text) VALUES ($id, $l, $t)", ("$id", id), ("$l", 0), ("$t", ""));
        using var page = Command(_db, "INSERT INTO AttachmentPages(page_id, page, text) VALUES ($id, $n, $t)", ("$id", id), ("$n", 0), ("$t", ""));
        for (var i = 0; i < pages.Count; i++)
        {
            var text = Flatten(pages[i]);
            if (text.Length == 0)
            {
                continue;
            }

            block.Parameters["$l"].Value = i + 1;
            block.Parameters["$t"].Value = TextFolding.Fold(text);
            block.ExecuteNonQuery();
            page.Parameters["$n"].Value = i + 1;
            page.Parameters["$t"].Value = text;
            page.ExecuteNonQuery();
        }
    }

    /// <summary>A page's text on one line (search hits are reported per line, which is per page for attachments).</summary>
    private static string Flatten(string text) => string.Join(' ', text.Split((char[])['\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries));

    // ---- Querying ----

    /// <summary>
    /// Pages that may match <paramref name="query"/> (full paths, most recently modified first), limited to
    /// <paramref name="scope"/>: null, a folder (everything below it) or a single page file.
    /// </summary>
    public IReadOnlyList<string> Candidates(AndNode query, string? scope = null, int limit = 5000, bool includeAttachments = true)
    {
        if (query.Items.Count == 0)
        {
            return [];
        }

        var parameters = new List<(string, object)>();
        var sql = new StringBuilder("SELECT path FROM Pages WHERE ").Append(Compile(query, parameters));
        if (!includeAttachments)
        {
            sql.Append(" AND kind = 0");
        }

        if (scope is not null && IsInside(scope) && !FileSystemMoves.IsSamePath(scope, Root))
        {
            if (NotebookScanner.IsPageFile(scope) && !Directory.Exists(scope))
            {
                sql.Append(" AND path = $scope");
                parameters.Add(("$scope", Relative(scope)));
            }
            else
            {
                sql.Append(" AND path LIKE $scope ESCAPE '\\'");
                parameters.Add(("$scope", EscapeLike(Relative(scope)) + "/%"));
            }
        }

        sql.Append(" ORDER BY mtime DESC LIMIT ").Append(limit);
        lock (_gate)
        {
            using var command = Command(_db, sql.ToString(), [.. parameters]);
            using var reader = command.ExecuteReader();
            var result = new List<string>();
            while (reader.Read())
            {
                result.Add(Path.Combine(Root, reader.GetString(0).Replace('/', Path.DirectorySeparatorChar)));
            }

            return result;
        }
    }

    public IReadOnlyList<TagCount> Tags()
    {
        lock (_gate)
        {
            using var command = Command(_db, "SELECT lower(tag), count(DISTINCT page_id) FROM Tags GROUP BY lower(tag) ORDER BY lower(tag)");
            using var reader = command.ExecuteReader();
            var result = new List<TagCount>();
            while (reader.Read())
            {
                result.Add(new TagCount(reader.GetString(0), reader.GetInt32(1)));
            }

            return result;
        }
    }

    public IReadOnlyList<TaskRow> Tasks(bool? done = false)
    {
        lock (_gate)
        {
            using var command = Command(_db,
                "SELECT p.path, p.title, t.line, t.done, t.text FROM Tasks t JOIN Pages p ON p.id = t.page_id"
                + (done is null ? "" : " WHERE t.done = $d") + " ORDER BY p.mtime DESC, t.line",
                ("$d", done == true ? 1 : 0));
            using var reader = command.ExecuteReader();
            var result = new List<TaskRow>();
            while (reader.Read())
            {
                result.Add(new TaskRow(
                    Path.Combine(Root, reader.GetString(0).Replace('/', Path.DirectorySeparatorChar)),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt32(3) != 0,
                    reader.GetString(4)));
            }

            return result;
        }
    }

    public int PageCount()
    {
        lock (_gate)
        {
            return Convert.ToInt32(Scalar(_db, "SELECT count(*) FROM Pages WHERE kind = 0"));
        }
    }

    private static string Compile(QueryNode node, List<(string, object)> parameters)
    {
        switch (node)
        {
            case AndNode and:
                return and.Items.Count == 0 ? "1" : "(" + string.Join(" AND ", and.Items.Select(i => Compile(i, parameters))) + ")";
            case OrNode or:
                return "(" + string.Join(" OR ", or.Items.Select(i => Compile(i, parameters))) + ")";
            case NotNode not:
                return "NOT (" + Compile(not.Inner, parameters) + ")";
            case TagNode tag:
            {
                var exact = Add(parameters, tag.Tag);
                var children = Add(parameters, EscapeLike(tag.Tag) + "/%");
                return $"id IN (SELECT page_id FROM Tags WHERE tag = {exact} OR tag LIKE {children} ESCAPE '\\')";
            }

            case TaskNode task:
                return task.Done is null
                    ? "id IN (SELECT page_id FROM Tasks)"
                    : $"id IN (SELECT page_id FROM Tasks WHERE done = {Add(parameters, task.Done.Value ? 1 : 0)})";
            case PropertyNode property:
                return "id IN (SELECT page_id FROM Properties WHERE key = " + Add(parameters, property.Key) + PropertyCondition(property, parameters) + ")";
            case TermNode term:
            {
                var folded = TextFolding.Fold(term.Text);
                if (folded.Length == 0)
                {
                    return "1";
                }

                var quoted = "\"" + folded.Replace("\"", "\"\"") + "\"";
                return folded.Length >= 3
                    ? $"id IN (SELECT page_id FROM Blocks WHERE id IN (SELECT rowid FROM BlocksTri WHERE BlocksTri MATCH {Add(parameters, quoted)}) UNION SELECT rowid FROM TitlesTri WHERE TitlesTri MATCH {Add(parameters, quoted)})"
                    : $"id IN (SELECT page_id FROM Blocks WHERE id IN (SELECT rowid FROM BlocksWords WHERE BlocksWords MATCH {Add(parameters, quoted + "*")}) UNION SELECT rowid FROM TitlesWords WHERE TitlesWords MATCH {Add(parameters, quoted + "*")})";
            }

            default:
                return "0";
        }
    }

    /// <summary>The SQL side of <see cref="PropertyValues.Matches"/> (a superset is fine: matches are confirmed on the text).</summary>
    private static string PropertyCondition(PropertyNode property, List<(string, object)> parameters)
    {
        var folded = PropertyValues.Fold(property.Value);
        var numeric = PropertyValues.TryNumber(property.Value, out var number);
        var op = property.Op switch
        {
            PropertyOp.Less => "<",
            PropertyOp.LessOrEqual => "<=",
            PropertyOp.Greater => ">",
            PropertyOp.GreaterOrEqual => ">=",
            _ => "=",
        };
        return property.Op switch
        {
            PropertyOp.Exists => "",
            PropertyOp.Equals when folded.Contains('*') =>
                $" AND value_folded LIKE {Add(parameters, string.Join("%", folded.Split('*').Select(EscapeLike)))} ESCAPE '\\'",
            _ when numeric =>
                $" AND (num {op} {Add(parameters, number)} OR (num IS NULL AND value_folded {op} {Add(parameters, folded)}))",
            _ => $" AND value_folded {op} {Add(parameters, folded)}",
        };
    }

    private static string Add(List<(string, object)> parameters, object value)
    {
        var name = "$p" + parameters.Count;
        parameters.Add((name, value));
        return name;
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private bool IsInside(string path) => FileSystemMoves.IsSameOrInside(path, Root);

    private string Relative(string full) => Path.GetRelativePath(Root, full).Replace(Path.DirectorySeparatorChar, '/');

    private static SqliteCommand Command(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    private static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(connection, sql, parameters);
        return command.ExecuteScalar();
    }

    private static void DeleteDatabaseFiles(string path)
    {
        foreach (var file in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _db.Dispose();
        }
    }
}
