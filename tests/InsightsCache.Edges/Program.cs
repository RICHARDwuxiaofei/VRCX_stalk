using System.Data.SQLite;
using System.Text.Json;
using VRCX.Insights;

var root = Path.Combine(Path.GetTempPath(), "insights-edge-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var source = Path.Combine(root, "source.sqlite3");
const string account = "usr_00000000-0000-0000-0000-000000000001";
var prefix = account.Replace("_", "").Replace("-", "");
var a = "usr_10000000-0000-0000-0000-000000000001";
var b = "usr_10000000-0000-0000-0000-000000000002";
var c = "usr_10000000-0000-0000-0000-000000000003";
var start = DateTimeOffset.Parse("2026-09-01T00:00:00Z").ToUnixTimeMilliseconds();
var results = new List<object>();
var cache = new InsightsCache(source, root, account);

void Check(bool value, string name)
{
    if (!value) throw new Exception("FAIL: " + name);
    results.Add(new { test = name, result = "PASS" }); Console.WriteLine("PASS: " + name);
}
JsonElement Call(string action, object? data = null)
{
    var q = data == null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(data))!;
    q["action"] = action;
    using var document = JsonDocument.Parse(cache.Request(JsonSerializer.Serialize(q)));
    return document.RootElement.Clone();
}
void Sync()
{
    var state = Call("start");
    for (var count = 0; state.GetProperty("phase").GetString() != "ready"; count++)
    {
        if (count > 100) throw new Exception("Unexpected unbounded indexing");
        state = Call("step", new { jobId = state.GetProperty("jobId").GetString() });
    }
}
SQLiteConnection Db()
{
    var db = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = source, Version = 3, Pooling = false }.ConnectionString);
    db.Open(); return db;
}
void Sql(SQLiteConnection db, string sql, params object[] args)
{
    using var command = new SQLiteCommand(sql, db);
    for (var i = 0; i < args.Length; i += 2) command.Parameters.AddWithValue((string)args[i], args[i + 1]);
    command.ExecuteNonQuery();
}
string Time(long offset) => DateTimeOffset.FromUnixTimeMilliseconds(start + offset).ToString("O");
void Event(SQLiteConnection db, string user, string type, long offset, string location = "wrld_demo:1") =>
    Sql(db, "INSERT INTO gamelog_join_leave VALUES(NULL,@at,@type,@name,@location,@user)",
        "@at", Time(offset), "@type", type, "@name", user == a ? "Alex" : "Blair", "@location", location, "@user", user);
JsonElement Summary(string[] ids, bool group = false) => Call("summary", new { userIds = ids, groupOnly = group, since = start, until = start + 1000000 });

try
{
    var missingRejected = false;
    try { Call("start"); } catch (InvalidOperationException) { missingRejected = true; }
    Check(missingRejected && !File.Exists(cache.CachePath), "Start cannot silently create a cache before explicit user creation");
    using (var db = Db())
    {
        Sql(db, @"CREATE TABLE gamelog_location(id INTEGER PRIMARY KEY,created_at TEXT,location TEXT,world_name TEXT);
            CREATE TABLE gamelog_join_leave(id INTEGER PRIMARY KEY,created_at TEXT,type TEXT,display_name TEXT,location TEXT,user_id TEXT);");
        Sql(db, $"CREATE TABLE {prefix}_friend_log_current(user_id TEXT PRIMARY KEY,display_name TEXT)");
        Sql(db, $"INSERT INTO {prefix}_friend_log_current VALUES(@a,'Alex'),(@b,'Blair')", "@a", a, "@b", b);
        Sql(db, "INSERT INTO gamelog_location VALUES(NULL,@at,'wrld_demo:1','Room')", "@at", Time(0));
        Event(db, a, "OnPlayerJoined", 1000); Event(db, a, "OnPlayerJoined", 1000);
        Event(db, b, "OnPlayerJoined", 11000); Event(db, c, "OnPlayerJoined", 15000);
        Event(db, b, "OnPlayerLeft", 91000); Event(db, a, "OnPlayerLeft", 101000); Event(db, a, "OnPlayerLeft", 101000);
        Sql(db, "INSERT INTO gamelog_location VALUES(NULL,@at,'offline','')", "@at", Time(120000));
        Event(db, a, "OnPlayerJoined", 150000, "private"); Event(db, a, "OnPlayerLeft", 180000, "private");
    }
    Call("create");
    var incompleteRejected = false;
    try { Summary(new[] { a }); } catch (InvalidOperationException) { incompleteRejected = true; }
    Check(incompleteRejected, "Unbuilt caches cannot be presented as complete reports");
    Sync();
    var single = Summary(new[] { a });
    Check(single.GetProperty("members")[0].GetProperty("observedMs").GetInt64() == 100000, "Identical duplicate join and leave rows do not double-count duration");
    Check(single.GetProperty("pairs").GetProperty("total").GetInt64() == 1, "Unclosed and hidden-location evidence cannot invent companions");
    Check(Summary(new[] { a }, true).GetProperty("pairs").GetProperty("total").GetInt64() == 0, "A one-member group does not leak outside-group companion rankings");
    var duo = Summary(new[] { a, b }, true);
    Check(duo.GetProperty("pairs").GetProperty("rows")[0].GetProperty("observedMs").GetInt64() == 80000, "Group intersections retain exact measured overlap");
    Check(Summary(new[] { c }).GetProperty("members")[0].GetProperty("incompleteSessions").GetInt64() == 1, "Observer departure records unknown exits as incomplete");
    using (var db = Db()) Sql(db, $"DELETE FROM {prefix}_friend_log_current WHERE user_id=@b", "@b", b);
    Sync();
    Check(Call("people", new { search = "", friendsOnly = true }).GetProperty("rows").GetArrayLength() == 1, "Removing a friend refreshes the snapshot without rebuilding historical logs");
    Check(Summary(new[] { a }).GetProperty("members")[0].GetProperty("observedMs").GetInt64() == 100000, "Friend snapshot changes preserve already measured history");
    using (var db = Db())
    {
        // A newly imported old Location splits both previously measured join/leave pairs.
        Sql(db, "INSERT INTO gamelog_location VALUES(NULL,@at,'wrld_demo:1','Room')", "@at", Time(50000));
        Sql(db, "INSERT INTO gamelog_location VALUES(NULL,@at,'wrld_demo:1','Room')", "@at", Time(50000));
    }
    Sync();
    Check(Summary(new[] { a }).GetProperty("members")[0].GetProperty("observedMs").GetInt64() == 0, "Late observer boundaries invalidate previous complete sessions instead of joining across visits");
    using (var db = Db())
    {
        Event(db, a, "OnPlayerJoined", 70000); Event(db, b, "OnPlayerJoined", 80000); Event(db, a, "OnPlayerJoined", 90000);
    }
    Sync();
    Check(Summary(new[] { a }).GetProperty("members")[0].GetProperty("observedMs").GetInt64() == 11000, "Repeated joins discard the uncertain start and pair only the newest evidence");
    Check(Summary(new[] { a, b }, true).GetProperty("pairs").GetProperty("rows")[0].GetProperty("observedMs").GetInt64() == 1000,
        "Late joins are rebuilt within their exact observer visit and do not count unknown intervals");
    var before = Summary(new[] { a }).GetRawText(); Sync();
    Check(Summary(new[] { a }).GetRawText() == before, "Repeated replay remains deterministic after duplicate and late-boundary corrections");
    var oldJob = Call("status").GetProperty("jobId").GetString(); Call("rebuild");
    var staleRejected = false;
    try { Call("step", new { jobId = oldJob }); } catch (InvalidOperationException) { staleRejected = true; }
    Check(staleRejected && Call("status").GetProperty("phase").GetString() == "created", "Rebuild replaces the checkpoint and still requires an explicit fresh Start");
    Check(Directory.GetFiles(Path.GetDirectoryName(cache.CachePath)!, "*.previous-*").Length == 1, "Rebuild preserves the old generated analysis file as a backup");
}
finally
{
    Directory.CreateDirectory("test-results");
    File.WriteAllText("test-results/cache-edges.json", JsonSerializer.Serialize(new { results }, new JsonSerializerOptions { WriteIndented = true }));
    try { Directory.Delete(root, true); } catch { }
}
