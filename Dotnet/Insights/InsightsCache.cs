using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;

namespace VRCX.Insights;

/// <summary>Derived, disposable data only. The VRCX source connection is always read-only.</summary>
public sealed partial class InsightsCache
{
    public const int SchemaVersion = 1;
    public const int BatchSize = 2000;
    private const int ReplayTail = 1000;
    private const long Day = 86400000;
    private const string Excluded = "usr_1cf3480c-c735-447d-9227-8e1acbb6bf18";
    private readonly string sourcePath;
    private readonly string account;
    private readonly string prefix;
    public string CachePath { get; }

    public InsightsCache(string sourcePath, string profileRoot, string account)
    {
        if (string.IsNullOrEmpty(account) || !Regex.IsMatch(account, @"^usr_[0-9a-fA-F-]{36}$"))
            throw new ArgumentException("Please sign in before opening the analysis cache.");
        this.sourcePath = Path.GetFullPath(sourcePath);
        this.account = account;
        prefix = account.Replace("-", "").Replace("_", "");
        var key = OperatingSystem.IsWindows() ? this.sourcePath.ToUpperInvariant() : this.sourcePath;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant()[..16];
        CachePath = Path.Combine(Path.GetFullPath(profileRoot), "AnalyticsCache", account, hash, "analysis-v1.db");
        if (string.Equals(this.sourcePath, CachePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The analysis cache must not be the source database.");
    }

    public string Request(string json)
    {
        using var request = JsonDocument.Parse(json);
        var q = request.RootElement;
        if (q.ValueKind != JsonValueKind.Object) throw new ArgumentException("Expected a cache request object.");
        var action = Text(q, "action");
        var mutexKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CachePath)));
        using var mutex = new Mutex(false, "vrcx-insights-" + mutexKey);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException("Another analysis request is still running. Try again shortly.");
            if (action is not ("status" or "create" or "rebuild") && !File.Exists(CachePath))
                throw new InvalidOperationException("Create the local analysis file explicitly before starting analysis.");
            object result = action switch
            {
                "status" => Status(), "create" => Create(false), "rebuild" => Create(true),
                "start" => Start(), "step" => Step(Text(q, "jobId")), "people" => People(q),
                "events" => Events(q), "summary" => Summary(q),
                _ => throw new ArgumentException("Unknown analysis action.")
            };
            return JsonSerializer.Serialize(result);
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static SQLiteConnection Open(string path, bool readOnly)
    {
        var builder = new SQLiteConnectionStringBuilder
        {
            DataSource = path, Version = 3, ReadOnly = readOnly,
            FailIfMissing = readOnly, Pooling = false, DefaultTimeout = 10
        };
        var db = new SQLiteConnection(builder.ConnectionString);
        try { db.Open(); return db; }
        catch { db.Dispose(); throw; }
    }
    private static SQLiteCommand Command(SQLiteConnection db, string sql, params object?[] args)
    {
        var cmd = new SQLiteCommand(sql, db) { CommandTimeout = 60 };
        for (var i = 0; i < args.Length; i += 2) cmd.Parameters.AddWithValue((string)args[i]!, args[i + 1] ?? DBNull.Value);
        return cmd;
    }
    private static int Exec(SQLiteConnection db, string sql, params object?[] args)
    { using var cmd = Command(db, sql, args); return cmd.ExecuteNonQuery(); }
    private static long Number(SQLiteConnection db, string sql, params object?[] args)
    {
        using var cmd = Command(db, sql, args);
        var value = cmd.ExecuteScalar();
        return value == null || value is DBNull ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }
    private static List<Dictionary<string, object?>> Rows(SQLiteConnection db, string sql, params object?[] args)
    {
        using var cmd = Command(db, sql, args);
        using var reader = cmd.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++) row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return rows;
    }
    private static string Get(SQLiteConnection db, string key, string fallback = "")
    {
        using var cmd = Command(db, "SELECT value FROM meta WHERE key=@key", "@key", key);
        return cmd.ExecuteScalar()?.ToString() ?? fallback;
    }
    private static void Set(SQLiteConnection db, string key, object value) =>
        Exec(db, "INSERT INTO meta(key,value) VALUES(@key,@value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            "@key", key, "@value", Convert.ToString(value, CultureInfo.InvariantCulture) ?? "");
    private static long MetaLong(SQLiteConnection db, string key, long fallback = 0) => long.TryParse(Get(db, key), out var value) ? value : fallback;
    private static string Str(Dictionary<string, object?> row, string key) => row.TryGetValue(key, out var value) ? value?.ToString() ?? "" : "";
    private static long Int(Dictionary<string, object?> row, string key) => long.TryParse(Str(row, key), out var n) ? n : 0;
    private static string Text(JsonElement q, string key) => q.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    private static long Long(JsonElement q, string key, long fallback) => q.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : fallback;
    private bool IsExcluded(string id) => string.IsNullOrEmpty(id) || id == account || id == Excluded;
    private static bool Visible(string location) => Regex.IsMatch(location, @"^wrld_[^\s:]+:[^\s]+$");
    private static bool Encounter(string kind) => kind is "Location" or "OnPlayerJoined" or "OnPlayerLeft";

    private object Status()
    {
        if (!File.Exists(CachePath)) return new { exists = false, phase = "missing", path = CachePath, schemaVersion = SchemaVersion };
        using var db = Open(CachePath, true);
        if (Number(db, "PRAGMA user_version") != SchemaVersion) return new { exists = true, phase = "incompatible", path = CachePath, schemaVersion = SchemaVersion };
        return new
        {
            exists = true, path = CachePath, schemaVersion = SchemaVersion,
            phase = Get(db, "phase", "created"), jobId = Get(db, "job_id"), updatedAt = Get(db, "updated_at"), snapshotAt = Get(db, "snapshot_at"),
            imported = MetaLong(db, "imported"), total = MetaLong(db, "total"), derived = MetaLong(db, "derived"), deriveTotal = MetaLong(db, "derive_total"),
            eventCount = MetaLong(db, "event_count"), sessionCount = MetaLong(db, "session_count"), rejected = MetaLong(db, "rejected_count"), warnings = Get(db, "warnings", "[]")
        };
    }
    private object Create(bool rebuild)
    {
        if (File.Exists(CachePath))
        {
            if (!rebuild) return Status();
            using (var old = Open(CachePath, false)) Exec(old, "PRAGMA wal_checkpoint(TRUNCATE)");
            File.Move(CachePath, CachePath + ".previous-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        }
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        using (var db = Open(CachePath, false))
        {
            Exec(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            using var transaction = db.BeginTransaction();
            Exec(db, @"
                CREATE TABLE meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
                CREATE TABLE cursors(name TEXT PRIMARY KEY, cursor INTEGER NOT NULL, high INTEGER NOT NULL,
                    total INTEGER NOT NULL, processed INTEGER NOT NULL, previous_high INTEGER NOT NULL,
                    previous_count INTEGER NOT NULL, schema_hash TEXT NOT NULL);
                CREATE TABLE events(seq INTEGER PRIMARY KEY AUTOINCREMENT, source TEXT NOT NULL, source_id INTEGER NOT NULL,
                    at_ms INTEGER NOT NULL, ord INTEGER NOT NULL, kind TEXT NOT NULL, user_id TEXT NOT NULL,
                    name TEXT NOT NULL, location TEXT NOT NULL, world TEXT NOT NULL, detail TEXT NOT NULL,
                    fingerprint TEXT NOT NULL, UNIQUE(source,source_id));
                CREATE INDEX events_order ON events(at_ms,ord,user_id,location,seq);
                CREATE INDEX events_user ON events(user_id,at_ms DESC,seq DESC);
                CREATE INDEX events_kind_user ON events(kind,user_id,at_ms DESC,seq DESC);
                CREATE INDEX events_location ON events(kind,at_ms);
                CREATE TABLE people(user_id TEXT PRIMARY KEY, name TEXT NOT NULL, last_ms INTEGER NOT NULL, friend INTEGER NOT NULL DEFAULT 0);
                CREATE TABLE rejected(source TEXT, source_id INTEGER, reason TEXT, PRIMARY KEY(source,source_id));
                CREATE TABLE sessions(id INTEGER PRIMARY KEY, user_id TEXT NOT NULL, visit_id INTEGER NOT NULL,
                    location TEXT NOT NULL, join_ms INTEGER NOT NULL, leave_ms INTEGER, complete INTEGER NOT NULL,
                    join_id INTEGER NOT NULL, leave_id INTEGER, reason TEXT NOT NULL);
                CREATE INDEX sessions_user ON sessions(user_id,complete,join_ms,leave_ms);
                CREATE INDEX sessions_visit ON sessions(visit_id,complete,user_id,join_ms,leave_ms);
                CREATE TABLE open_sessions(user_id TEXT PRIMARY KEY, visit_id INTEGER NOT NULL, location TEXT NOT NULL,
                    join_ms INTEGER NOT NULL, join_id INTEGER NOT NULL);
            ");
            Exec(db, "PRAGMA user_version=1");
            Set(db, "phase", "created"); Set(db, "account", account); Set(db, "source", sourcePath); Set(db, "job_id", Guid.NewGuid().ToString("N"));
            transaction.Commit();
        }
        return Status();
    }
    private sealed record Source(string Name, string Kind, bool Friend = false);
    private Source[] Sources() => new[]
    {
        new Source("gamelog_location", "Location"), new Source("gamelog_join_leave", ""),
        new Source(prefix + "_feed_status", "Status"), new Source(prefix + "_feed_bio", "Bio"),
        new Source(prefix + "_feed_gps", "GPS"), new Source(prefix + "_feed_online_offline", ""),
        new Source(prefix + "_feed_avatar", "Avatar"), new Source(prefix + "_friend_log_history", ""),
        new Source(prefix + "_friend_log_current", "Friend", true)
    };
    private static HashSet<string> Columns(SQLiteConnection db, string table) => Rows(db, $"PRAGMA table_info(\"{table}\")").Select(r => Str(r, "name")).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private object Start()
    {
        using (var db = Open(CachePath, false))
        {
            CheckVersion(db);
            if (Get(db, "phase") is "ingesting" or "deriving") return Status();
            if (Get(db, "phase") == "needs-rebuild") throw new InvalidOperationException("Source history changed. Rebuild the analysis cache first.");
            using var source = Open(sourcePath, true);
            var warnings = new List<string>();
            long total = 0;
            using var transaction = db.BeginTransaction();
            foreach (var spec in Sources())
            {
                var columns = Columns(source, spec.Name);
                if (columns.Count == 0)
                {
                    if (Number(db, "SELECT COUNT(*) FROM cursors WHERE name=@name", "@name", spec.Name) > 0) throw new InvalidOperationException("A previously indexed source table is missing. Rebuild the cache.");
                    warnings.Add(spec.Name + ": unavailable (not recorded/imported)"); continue;
                }
                if (!spec.Friend && (!columns.Contains("id") || !columns.Contains("created_at"))) throw new InvalidOperationException("Unsupported source schema: " + spec.Name + ". No source data was modified.");
                var schema = string.Join(",", columns.OrderBy(x => x));
                var prior = Rows(db, "SELECT * FROM cursors WHERE name=@name LIMIT 1", "@name", spec.Name).FirstOrDefault();
                var id = spec.Friend ? "rowid" : "id";
                var high = Number(source, $"SELECT COALESCE(MAX({id}),0) FROM \"{spec.Name}\"");
                var count = Number(source, $"SELECT COUNT(*) FROM \"{spec.Name}\"");
                // The current friend list is a replaceable snapshot, not an append-only log.
                // Removing a friend must not force a full rebuild of all encounter history.
                if (prior != null && (schema != Str(prior, "schema_hash") || (!spec.Friend && (high < Int(prior, "high") || count < Int(prior, "previous_count")))))
                    throw new InvalidOperationException("Source history was replaced, pruned or migrated. Rebuild the cache instead of reusing stale results.");
                var previous = prior == null ? 0 : Int(prior, "high");
                var cursor = spec.Friend ? 0 : Math.Max(0, previous - ReplayTail);
                var work = Number(source, $"SELECT COUNT(*) FROM \"{spec.Name}\" WHERE {id}>@cursor AND {id}<=@high", "@cursor", cursor, "@high", high);
                Exec(db, @"INSERT OR REPLACE INTO cursors(name,cursor,high,total,processed,previous_high,previous_count,schema_hash)
                    VALUES(@name,@cursor,@high,@total,0,@previous,@count,@schema)",
                    "@name", spec.Name, "@cursor", cursor, "@high", high, "@total", work, "@previous", previous, "@count", count, "@schema", schema);
                total += work;
            }
            Exec(db, "UPDATE people SET friend=0 WHERE friend<>0");
            Set(db, "phase", "ingesting"); Set(db, "total", total); Set(db, "imported", 0); Set(db, "dirty_from", long.MaxValue);
            Set(db, "warnings", JsonSerializer.Serialize(warnings)); Set(db, "job_id", Guid.NewGuid().ToString("N")); Set(db, "snapshot_at", DateTimeOffset.UtcNow.ToString("O"));
            transaction.Commit();
        }
        return Status();
    }
    private void CheckVersion(SQLiteConnection db)
    {
        if (Number(db, "PRAGMA user_version") != SchemaVersion || Get(db, "account") != account || Get(db, "source") != sourcePath) throw new InvalidOperationException("The cache format or source identity does not match. Rebuild the cache.");
    }
    private object Step(string jobId)
    {
        using (var db = Open(CachePath, false))
        {
            CheckVersion(db);
            if (string.IsNullOrEmpty(jobId) || Get(db, "job_id") != jobId) throw new InvalidOperationException("The analysis job changed. Reload its status before continuing.");
            if (Get(db, "phase") == "deriving") DeriveBatch(db);
            else if (Get(db, "phase") == "ingesting")
            {
                var cursor = Rows(db, "SELECT * FROM cursors WHERE cursor<high ORDER BY name LIMIT 1").FirstOrDefault();
                if (cursor == null) BeginDerivation(db); else ImportBatch(db, cursor);
            }
        }
        return Status();
    }
    private void ImportBatch(SQLiteConnection db, Dictionary<string, object?> cursor)
    {
        var name = Str(cursor, "name"); var spec = Sources().Single(s => s.Name == name); var idColumn = spec.Friend ? "rowid" : "id";
        using var source = Open(sourcePath, true);
        if (!spec.Friend && Number(source, $"SELECT COALESCE(MAX({idColumn}),0) FROM \"{name}\"") < Int(cursor, "high")) throw new InvalidOperationException("The source was replaced during indexing. Rebuild the cache.");
        var batch = Rows(source, $"SELECT {idColumn} AS source_row_id,* FROM \"{name}\" WHERE {idColumn}>@cursor AND {idColumn}<=@high ORDER BY {idColumn} LIMIT {BatchSize}", "@cursor", Int(cursor, "cursor"), "@high", Int(cursor, "high"));
        using var transaction = db.BeginTransaction();
        var dirty = MetaLong(db, "dirty_from", long.MaxValue);
        foreach (var raw in batch)
        {
            var id = Int(raw, "source_row_id"); var user = Str(raw, "user_id"); var displayName = Str(raw, "display_name");
            if (spec.Friend)
            {
                if (!IsExcluded(user)) UpsertPerson(db, user, displayName, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), true);
                continue;
            }
            var kind = spec.Kind.Length > 0 ? spec.Kind : Str(raw, "type");
            var old = id <= Int(cursor, "previous_high") ? Rows(db, "SELECT at_ms,kind,fingerprint FROM events WHERE source=@source AND source_id=@id LIMIT 1", "@source", name, "@id", id).FirstOrDefault() : null;
            var validTime = TryTimestamp(Str(raw, "created_at"), out var at);
            if (!validTime || (kind != "Location" && IsExcluded(user)))
            {
                if (old != null && Encounter(Str(old, "kind"))) dirty = Math.Min(dirty, Int(old, "at_ms"));
                Exec(db, "DELETE FROM events WHERE source=@source AND source_id=@id", "@source", name, "@id", id);
                if (!validTime) Exec(db, "INSERT OR REPLACE INTO rejected VALUES(@source,@id,'invalid timestamp')", "@source", name, "@id", id);
                else Exec(db, "DELETE FROM rejected WHERE source=@source AND source_id=@id", "@source", name, "@id", id);
                continue;
            }
            var location = Str(raw, "location"); var world = Str(raw, "world_name");
            var detail = new Dictionary<string, string>();
            foreach (var field in new[] { "status", "status_description", "previous_status", "previous_status_description", "bio", "previous_bio", "previous_location", "avatar_name", "previous_display_name", "trust_level", "previous_trust_level" }) if (raw.ContainsKey(field)) detail[field] = Str(raw, field);
            var detailJson = JsonSerializer.Serialize(detail);
            var fingerprint = JsonSerializer.Serialize(new object[] { at, kind, user, displayName, location, world, detailJson });
            if (old != null && Str(old, "fingerprint") == fingerprint) continue;
            if (Encounter(kind)) dirty = Math.Min(dirty, at);
            if (old != null && Encounter(Str(old, "kind"))) dirty = Math.Min(dirty, Int(old, "at_ms"));
            var order = kind == "OnPlayerLeft" ? 0 : kind == "Location" ? 1 : kind == "OnPlayerJoined" ? 2 : 3;
            Exec(db, @"INSERT INTO events(source,source_id,at_ms,ord,kind,user_id,name,location,world,detail,fingerprint)
                VALUES(@source,@id,@at,@ord,@kind,@user,@name,@location,@world,@detail,@fingerprint)
                ON CONFLICT(source,source_id) DO UPDATE SET at_ms=excluded.at_ms,ord=excluded.ord,kind=excluded.kind,
                    user_id=excluded.user_id,name=excluded.name,location=excluded.location,world=excluded.world,detail=excluded.detail,fingerprint=excluded.fingerprint",
                "@source", name, "@id", id, "@at", at, "@ord", order, "@kind", kind, "@user", user, "@name", displayName, "@location", location, "@world", world, "@detail", detailJson, "@fingerprint", fingerprint);
            Exec(db, "DELETE FROM rejected WHERE source=@source AND source_id=@id", "@source", name, "@id", id);
            if (!IsExcluded(user)) UpsertPerson(db, user, displayName, at, false);
        }
        var last = batch.Count == 0 ? Int(cursor, "high") : Int(batch[^1], "source_row_id");
        Exec(db, "UPDATE cursors SET cursor=@last,processed=processed+@count WHERE name=@name", "@last", last, "@count", batch.Count, "@name", name);
        Set(db, "dirty_from", dirty); Set(db, "imported", MetaLong(db, "imported") + batch.Count);
        transaction.Commit();
    }
    private static void UpsertPerson(SQLiteConnection db, string id, string name, long at, bool friend)
    {
        if (string.IsNullOrWhiteSpace(name)) name = id;
        Exec(db, @"INSERT INTO people(user_id,name,last_ms,friend) VALUES(@id,@name,@at,@friend)
            ON CONFLICT(user_id) DO UPDATE SET name=CASE WHEN excluded.last_ms>=people.last_ms THEN excluded.name ELSE people.name END,
            last_ms=MAX(people.last_ms,excluded.last_ms),friend=MAX(people.friend,excluded.friend)", "@id", id, "@name", name, "@at", at, "@friend", friend ? 1 : 0);
    }
    public static bool TryTimestamp(string raw, out long milliseconds)
    {
        milliseconds = 0;
        // Known VRCX legacy SQLite TEXT timestamps without offsets are UTC.
        if (!Regex.IsMatch(raw ?? "", @"^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}")) return false;
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)) return false;
        milliseconds = time.ToUnixTimeMilliseconds(); return true;
    }
}
