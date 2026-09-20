using System.Data.SQLite;
using System.Security.Cryptography;
using System.Text.Json;
using VRCX.Insights;

var root = Path.Combine(Path.GetTempPath(), "insights-legacy-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
const string account = "usr_00000000-0000-0000-0000-000000000001";
const string alice = "usr_10000000-0000-0000-0000-000000000001";
var prefix = account.Replace("_", "").Replace("-", "");
var results = new List<object>();

void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    results.Add(new { test = name, result = "PASS" });
    Console.WriteLine("PASS: " + name);
}
SQLiteConnection Open(string path)
{
    var db = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = path, Version = 3, Pooling = false }.ConnectionString);
    db.Open(); return db;
}
void Sql(SQLiteConnection db, string sql, params object[] args)
{
    using var command = new SQLiteCommand(sql, db);
    for (var i = 0; i < args.Length; i += 2) command.Parameters.AddWithValue((string)args[i], args[i + 1]);
    command.ExecuteNonQuery();
}
JsonElement Call(InsightsCache cache, string action, object? options = null)
{
    var q = options == null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(options))!;
    q["action"] = action;
    using var document = JsonDocument.Parse(cache.Request(JsonSerializer.Serialize(q)));
    return document.RootElement.Clone();
}
JsonElement Complete(InsightsCache cache)
{
    var state = Call(cache, "start");
    for (var count = 0; state.GetProperty("phase").GetString() != "ready"; count++)
    {
        if (count > 100) throw new Exception("Legacy adapter did not converge.");
        state = Call(cache, "step", new { jobId = state.GetProperty("jobId").GetString() });
    }
    return state;
}
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

try
{
    var legacy = Path.Combine(root, "legacy.sqlite3");
    using (var db = Open(legacy))
    {
        // Historical-style camelCase columns, nonstandard row id and implicit rowid.
        Sql(db, @"CREATE TABLE gamelog_location(rowId INTEGER PRIMARY KEY, createdAt TEXT, location TEXT, worldName TEXT);
            CREATE TABLE gamelog_join_leave(createdAt TEXT, eventType TEXT, displayName TEXT, location TEXT);
        ");
        Sql(db, $"CREATE TABLE {prefix}_feed_status(createdAt TEXT,userId TEXT,displayName TEXT,status TEXT,previousStatus TEXT)");
        Sql(db, $"CREATE TABLE {prefix}_friend_log_current(userId TEXT PRIMARY KEY,displayName TEXT)");
        Sql(db, "INSERT INTO gamelog_location VALUES(1,'2025-01-01 00:00:00','wrld_legacy:1','Legacy Room')");
        Sql(db, "INSERT INTO gamelog_join_leave VALUES('2025-01-01 00:00:10','OnPlayerJoin',@name,'wrld_legacy:1')", "@name", $"Alice ({alice})");
        Sql(db, "INSERT INTO gamelog_join_leave VALUES('2025-01-01 00:01:10','OnPlayerLeave',@name,'wrld_legacy:1')", "@name", $"Alice ({alice})");
        Sql(db, $"INSERT INTO {prefix}_feed_status VALUES('1735689690',@user,'Alice','busy','active')", "@user", alice);
        Sql(db, $"INSERT INTO {prefix}_friend_log_current VALUES(@user,'Alice')", "@user", alice);
    }
    var before = Hash(legacy);
    var cache = new InsightsCache(legacy, root, account);
    Call(cache, "create");
    var ready = Complete(cache);
    Check(ready.GetProperty("sourceProfile").GetString() == "legacy-adapted", "Historical column layout is detected as legacy-adapted");
    Check(ready.GetProperty("legacyAdapters").GetInt64() >= 2, "Legacy adapter count is reported");
    Check(Hash(legacy) == before, "Legacy source database remains byte-for-byte unchanged");
    var people = Call(cache, "people", new { search = "Alice", friendsOnly = false });
    Check(people.GetProperty("rows").GetArrayLength() == 1 && people.GetProperty("rows")[0].GetProperty("id").GetString() == alice,
        "User id embedded in old display-name rows is recovered without editing the source");
    var summary = Call(cache, "summary", new { userIds = new[] { alice }, since = 0L, until = 2000000000000L });
    Check(summary.GetProperty("members")[0].GetProperty("observedMs").GetInt64() == 60000,
        "Legacy join/leave aliases normalize into one complete 60-second session");
    var status = Call(cache, "events", new { userIds = new[] { alice }, since = 0L, until = 2000000000000L, filter = "status" });
    Check(status.GetProperty("total").GetInt64() == 1, "Legacy Unix-second timestamp and camelCase status columns are normalized");

    var current = Path.Combine(root, "current.sqlite3");
    using (var db = Open(current))
    {
        Sql(db, @"CREATE TABLE gamelog_location(id INTEGER PRIMARY KEY,created_at TEXT,location TEXT,world_name TEXT);
            CREATE TABLE gamelog_join_leave(id INTEGER PRIMARY KEY,created_at TEXT,type TEXT,display_name TEXT,location TEXT,user_id TEXT);");
        Sql(db, "INSERT INTO gamelog_location VALUES(1,'2026-01-01T00:00:00Z','wrld_current:1','Current Room')");
        Sql(db, "INSERT INTO gamelog_join_leave VALUES(1,'2026-01-01T00:00:10Z','OnPlayerJoined','Alice','wrld_current:1',@user)", "@user", alice);
        Sql(db, "INSERT INTO gamelog_join_leave VALUES(2,'2026-01-01T00:00:20Z','OnPlayerLeft','Alice','wrld_current:1',@user)", "@user", alice);
    }
    var currentCache = new InsightsCache(current, root, account);
    Call(currentCache, "create");
    var currentReady = Complete(currentCache);
    Check(currentReady.GetProperty("sourceProfile").GetString() == "current", "Current official core schema stays on the current adapter path");

    var tooOld = Path.Combine(root, "unsupported.sqlite3");
    using (var db = Open(tooOld))
    {
        Sql(db, @"CREATE TABLE gamelog_location(id INTEGER PRIMARY KEY,created_at TEXT,location TEXT);
            CREATE TABLE gamelog_join_leave(id INTEGER PRIMARY KEY,created_at TEXT,display_name TEXT,location TEXT);");
    }
    var unsupported = new InsightsCache(tooOld, root, account);
    Call(unsupported, "create");
    var rejected = false;
    try { Call(unsupported, "start"); } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Unknown ancient schema is rejected instead of guessing a destructive migration");
}
finally
{
    Directory.CreateDirectory("test-results");
    File.WriteAllText("test-results/cache-legacy.json", JsonSerializer.Serialize(new { results }, new JsonSerializerOptions { WriteIndented = true }));
    try { Directory.Delete(root, true); } catch { }
}
