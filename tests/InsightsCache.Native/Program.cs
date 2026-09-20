using System.Data.SQLite;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using VRCX.Insights;

var root = Path.Combine(Path.GetTempPath(), "insights-native-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var source = Path.Combine(root, "source.sqlite3");
const string observer = "usr_00000000-0000-0000-0000-000000000001";
const string excluded = "usr_1cf3480c-c735-447d-9227-8e1acbb6bf18";
var prefix = observer.Replace("-", "").Replace("_", "");
var users = Enumerable.Range(0, 10).Select(i => "usr_10000000-0000-0000-0000-" + i.ToString("D12")).ToArray();
var visits = args.Contains("--stress") ? 26001 : 6001;
var firstTime = DateTimeOffset.Parse("2020-01-01T00:00:00Z").ToUnixTimeMilliseconds();
var until = DateTimeOffset.Parse("2030-01-01T00:00:00Z").ToUnixTimeMilliseconds();
var results = new List<object>();
var timer = Stopwatch.StartNew();

SQLiteConnection OpenSource()
{
    var db = new SQLiteConnection(new SQLiteConnectionStringBuilder { DataSource = source, Version = 3, Pooling = false }.ConnectionString);
    db.Open(); return db;
}
void Sql(SQLiteConnection db, string sql, params object[] parameters)
{
    using var command = new SQLiteCommand(sql, db);
    for (var i = 0; i < parameters.Length; i += 2) command.Parameters.AddWithValue((string)parameters[i], parameters[i + 1]);
    command.ExecuteNonQuery();
}
string Iso(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("O");
void Check(bool condition, string description)
{
    if (!condition) throw new Exception("FAIL: " + description);
    Console.WriteLine("PASS: " + description);
    results.Add(new { test = description, result = "PASS" });
}
JsonElement Call(InsightsCache cache, string action, object? options = null)
{
    var request = options == null ? new Dictionary<string, object?>() : JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(options))!;
    request["action"] = action;
    using var document = JsonDocument.Parse(cache.Request(JsonSerializer.Serialize(request)));
    return document.RootElement.Clone();
}
JsonElement Complete(InsightsCache cache, JsonElement? initial = null)
{
    var status = initial ?? Call(cache, "start");
    var steps = 0;
    while (status.GetProperty("phase").GetString() != "ready")
    {
        if (++steps > 20000) throw new Exception("Indexer did not converge");
        status = Call(cache, "step", new { jobId = status.GetProperty("jobId").GetString() });
    }
    Console.WriteLine("Completed in " + steps + " bounded steps.");
    return status;
}
string SourceHash() => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));

try
{
    using (var db = OpenSource())
    {
        Sql(db, @"CREATE TABLE gamelog_location(id INTEGER PRIMARY KEY,created_at TEXT,location TEXT,world_name TEXT);
            CREATE TABLE gamelog_join_leave(id INTEGER PRIMARY KEY,created_at TEXT,type TEXT,display_name TEXT,location TEXT,user_id TEXT);");
        Sql(db, $@"CREATE TABLE {prefix}_feed_status(id INTEGER PRIMARY KEY,created_at TEXT,user_id TEXT,display_name TEXT,status TEXT,previous_status TEXT);
            CREATE TABLE {prefix}_feed_bio(id INTEGER PRIMARY KEY,created_at TEXT,user_id TEXT,display_name TEXT,bio TEXT,previous_bio TEXT);
            CREATE TABLE {prefix}_friend_log_current(user_id TEXT PRIMARY KEY,display_name TEXT);");
        using var transaction = db.BeginTransaction();
        for (var visit = 0; visit < visits; visit++)
        {
            var t = firstTime + visit * 600000L;
            var location = "wrld_synthetic:" + visit;
            Sql(db, "INSERT INTO gamelog_location VALUES(@id,@at,@location,'Synthetic room')", "@id", visit + 1, "@at", Iso(t), "@location", location);
            for (var user = 0; user < users.Length; user++)
            {
                Sql(db, "INSERT INTO gamelog_join_leave VALUES(NULL,@at,'OnPlayerJoined',@name,@location,@user)",
                    "@at", Iso(t + 1000 + user * 1000), "@name", "Player " + user, "@location", location, "@user", users[user]);
                Sql(db, "INSERT INTO gamelog_join_leave VALUES(NULL,@at,'OnPlayerLeft',@name,@location,@user)",
                    "@at", Iso(t + 91000 + user * 1000), "@name", "Player " + user, "@location", location, "@user", users[user]);
            }
        }
        Sql(db, $"INSERT INTO {prefix}_feed_status VALUES(1,'2020-01-01 12:00:00',@user,'Player 0','busy','active')", "@user", users[0]);
        Sql(db, $"INSERT INTO {prefix}_feed_bio VALUES(1,'2020-01-01T12:01:00Z',@user,'Player 0','new\nquoted, bio','old bio')", "@user", users[0]);
        Sql(db, $"INSERT INTO {prefix}_feed_status VALUES(2,'2020-01-01T12:00:00Z',@user,'Excluded','busy','active')", "@user", excluded);
        Sql(db, $"INSERT INTO {prefix}_feed_bio VALUES(2,'2020-01-01T12:00:00Z',@user,'Observer','self','previous')", "@user", observer);
        Sql(db, $"INSERT INTO {prefix}_feed_status VALUES(3,'invalid',@user,'Player 0','busy','active')", "@user", users[0]);
        Sql(db, $"INSERT INTO {prefix}_friend_log_current VALUES(@user,'Player 0')", "@user", users[0]);
        transaction.Commit();
    }
    var hashBefore = SourceHash();
    var cache = new InsightsCache(source, root, observer);
    Check(!Call(cache, "status").GetProperty("exists").GetBoolean() && !File.Exists(cache.CachePath), "Opening the page/status does not create or scan a cache");
    Check(Call(cache, "create").GetProperty("phase").GetString() == "created", "Create only initializes a separate analysis file");
    Check(Call(cache, "status").GetProperty("eventCount").GetInt64() == 0, "Creating the file does not silently start analysis");
    var started = Call(cache, "start");
    var partial = Call(cache, "step", new { jobId = started.GetProperty("jobId").GetString() });
    Check(partial.GetProperty("imported").GetInt64() <= InsightsCache.BatchSize, "Native import response is bounded to one batch");
    cache = new InsightsCache(source, root, observer);
    Check(Call(cache, "start").GetProperty("jobId").GetString() == partial.GetProperty("jobId").GetString(), "Pause/reopen/resume keeps the committed job checkpoint");
    var done = Complete(cache, partial);
    Check(done.GetProperty("sessionCount").GetInt64() == visits * 10L, "All historical sessions survive former 5000-location/50000-log limits");
    Check(done.GetProperty("rejected").GetInt64() == 1, "Malformed timestamps are diagnosed instead of silently guessed");
    Check(SourceHash() == hashBefore, "Full analysis leaves the source file byte-for-byte unchanged");
    var directory = Call(cache, "people", new { search = "Player", friendsOnly = true });
    Check(directory.GetProperty("rows").GetArrayLength() == 1, "Friend-only autocomplete reads the cached friend directory");
    var summary = Call(cache, "summary", new { userIds = new[] { users[0] }, since = 0, until });
    Check(summary.GetProperty("members")[0].GetProperty("observedMs").GetInt64() == visits * 90000L, "All-time duration is calculated from persisted complete pairs");
    Check(summary.GetProperty("pairs").GetProperty("total").GetInt64() == 9, "Single-person summary includes every observed peer");
    var group = Call(cache, "summary", new { userIds = new[] { users[0], users[1] }, since = 0, until });
    Check(group.GetProperty("pairs").GetProperty("total").GetInt64() == 1, "Group pairs are unique and restricted to group members");
    Check(group.GetProperty("pairs").GetProperty("rows")[0].GetProperty("observedMs").GetInt64() == visits * 89000L, "Exact same-visit intersections have the expected duration");
    var p6 = Call(cache, "events", new { userIds = new[] { users[0] }, since = 0, until, filter = "encounters", page = 6, size = 30 });
    Check(p6.GetProperty("rows").GetArrayLength() == 30 && p6.GetProperty("page").GetInt64() == 6, "SQL pagination can reach beyond five pages without loading all rows");
    Check(p6.GetProperty("total").GetInt64() == visits * 2L, "Pagination retains the true total count");
    var statusOnly = Call(cache, "events", new { userIds = new[] { users[0] }, since = 0, until, filter = "status" });
    Check(statusOnly.GetProperty("total").GetInt64() == 1, "Status logs exclude invalid and configured-excluded actors");
    var oneDay = Call(cache, "events", new { userIds = new[] { users[0] }, since = firstTime, until = firstTime + 86400000L, filter = "encounters", size = 10 });
    Check(oneDay.GetProperty("total").GetInt64() == 288, "A 24-hour range uses epoch boundaries without string-time comparisons");
    File.Move(source, source + ".temporarily-away");
    Check(Call(cache, "events", new { userIds = new[] { users[0] }, since = 0, until, size = 10 }).GetProperty("rows").GetArrayLength() == 10,
        "Reopened cached reports do not reopen or rescan the source");
    File.Move(source + ".temporarily-away", source);
    var beforeCount = done.GetProperty("eventCount").GetInt64();
    var unchanged = Complete(cache);
    Check(unchanged.GetProperty("eventCount").GetInt64() == beforeCount && unchanged.GetProperty("sessionCount").GetInt64() == visits * 10L,
        "Repeated incremental synchronization is idempotent");
    using (var db = OpenSource())
    {
        Sql(db, "INSERT INTO gamelog_join_leave VALUES(NULL,@at,'OnPlayerJoined','Late import',@location,@user)",
            "@at", Iso(firstTime + 200000), "@location", "wrld_synthetic:0", "@user", users[0]);
        Sql(db, "INSERT INTO gamelog_join_leave VALUES(NULL,@at,'OnPlayerLeft','Late import',@location,@user)",
            "@at", Iso(firstTime + 230000), "@location", "wrld_synthetic:0", "@user", users[0]);
    }
    var late = Complete(cache);
    Check(late.GetProperty("sessionCount").GetInt64() == visits * 10L + 1, "Old-timestamp rows appended with new IDs invalidate and replay their affected visits");
    summary = Call(cache, "summary", new { userIds = new[] { users[0] }, since = 0, until });
    Check(summary.GetProperty("members")[0].GetProperty("observedMs").GetInt64() == visits * 90000L + 30000, "Late import adds only its real duration without double-counting old sessions");
    var other = new InsightsCache(source, root, "usr_00000000-0000-0000-0000-000000000002");
    Check(other.CachePath != cache.CachePath && !Call(other, "status").GetProperty("exists").GetBoolean(), "Different accounts never share analysis caches");
    var badJobRejected = false;
    try { Call(cache, "step", new { jobId = "stale-job" }); } catch (InvalidOperationException) { badJobRejected = true; }
    Check(badJobRejected, "A stale analysis job cannot write after another job or rebuild");
    Check(InsightsCache.TryTimestamp("2020-01-01T08:00:00+08:00", out var zoned) && zoned == firstTime,
        "Offset timestamps normalize to the same UTC millisecond");
    Check(!InsightsCache.TryTimestamp("2020-02-31 00:00:00", out _), "Invalid calendar dates are rejected");
    using (var db = OpenSource()) Sql(db, "DELETE FROM gamelog_join_leave WHERE id=(SELECT MAX(id) FROM gamelog_join_leave)");
    var rewindRejected = false;
    try { Call(cache, "start"); } catch (InvalidOperationException) { rewindRejected = true; }
    Check(rewindRejected, "A truncated/replaced source requires explicit cache rebuilding");
    Console.WriteLine("Native SQLite integration tests passed in " + timer.Elapsed.TotalSeconds.ToString("F1") + " seconds.");
}
finally
{
    Directory.CreateDirectory("test-results");
    File.WriteAllText("test-results/cache-native.json", JsonSerializer.Serialize(new { visits, batchSize = InsightsCache.BatchSize,
        seconds = timer.Elapsed.TotalSeconds, results }, new JsonSerializerOptions { WriteIndented = true }));
    try { Directory.Delete(root, true); } catch { }
}
