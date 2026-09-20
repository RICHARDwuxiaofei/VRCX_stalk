using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text.Json;

namespace VRCX.Insights;

public sealed partial class InsightsCache
{
    private void BeginDerivation(SQLiteConnection db)
    {
        using var transaction = db.BeginTransaction();
        var dirty = MetaLong(db, "dirty_from", long.MaxValue);
        if (dirty == long.MaxValue) Finish(db);
        else
        {
            // Replay from the preceding observer visit, not just the changed row.
            var previous = Rows(db, "SELECT at_ms FROM events WHERE kind='Location' AND at_ms<@dirty ORDER BY at_ms DESC LIMIT 1", "@dirty", dirty).FirstOrDefault();
            var boundary = previous == null ? Number(db, "SELECT MIN(at_ms) FROM events WHERE ord<3") : Int(previous, "at_ms");
            Exec(db, "DELETE FROM sessions WHERE join_ms>=@boundary OR leave_ms>@boundary", "@boundary", boundary);
            Exec(db, "DELETE FROM open_sessions");
            Set(db, "derive_at", boundary - 1); Set(db, "derive_ord", -1);
            Set(db, "derive_user", ""); Set(db, "derive_location", ""); Set(db, "derive_seq", 0);
            Set(db, "observer_location", ""); Set(db, "observer_visit", 0); Set(db, "last_signature", "");
            Set(db, "derive_total", Number(db, "SELECT COUNT(*) FROM events WHERE ord<3 AND at_ms>=@boundary", "@boundary", boundary));
            Set(db, "derived", 0); Set(db, "phase", "deriving");
        }
        transaction.Commit();
    }

    private void DeriveBatch(SQLiteConnection db)
    {
        var batch = Rows(db, $@"SELECT seq,at_ms,ord,kind,user_id,location FROM events
            WHERE ord<3 AND (at_ms,ord,user_id,location,seq)>(@at,@ord,@user,@location,@seq)
            ORDER BY at_ms,ord,user_id,location,seq LIMIT {BatchSize}",
            "@at", MetaLong(db, "derive_at"), "@ord", MetaLong(db, "derive_ord", -1),
            "@user", Get(db, "derive_user"), "@location", Get(db, "derive_location"), "@seq", MetaLong(db, "derive_seq"));
        using var transaction = db.BeginTransaction();
        var observerLocation = Get(db, "observer_location");
        var visit = MetaLong(db, "observer_visit");
        var lastSignature = Get(db, "last_signature");
        foreach (var row in batch)
        {
            var at = Int(row, "at_ms"); var seq = Int(row, "seq");
            var kind = Str(row, "kind"); var user = Str(row, "user_id"); var location = Str(row, "location");
            var signature = JsonSerializer.Serialize(new object[] { at, kind, user, location });
            if (signature == lastSignature) continue;
            lastSignature = signature;
            if (kind == "Location")
            {
                Exec(db, @"INSERT INTO sessions(user_id,visit_id,location,join_ms,leave_ms,complete,join_id,leave_id,reason)
                    SELECT user_id,visit_id,location,join_ms,NULL,0,join_id,NULL,'observer visit ended without an exit' FROM open_sessions");
                Exec(db, "DELETE FROM open_sessions");
                observerLocation = Visible(location) ? location : "";
                visit = seq;
                continue;
            }
            if (!Visible(location) || location != observerLocation) continue;
            var open = Rows(db, "SELECT * FROM open_sessions WHERE user_id=@user LIMIT 1", "@user", user).FirstOrDefault();
            if (kind == "OnPlayerJoined")
            {
                if (open != null) Close(db, open, null, null, "repeated join without an exit");
                Exec(db, "INSERT OR REPLACE INTO open_sessions VALUES(@user,@visit,@location,@at,@seq)",
                    "@user", user, "@visit", visit, "@location", location, "@at", at, "@seq", seq);
            }
            else if (open != null)
            {
                var duration = at - Int(open, "join_ms");
                Close(db, open, duration > 0 && duration <= Day ? at : null, seq,
                    duration > 0 && duration <= Day ? "" : "invalid or over-24-hour pair");
            }
        }
        if (batch.Count > 0)
        {
            var last = batch[^1];
            Set(db, "derive_at", Int(last, "at_ms")); Set(db, "derive_ord", Int(last, "ord"));
            Set(db, "derive_user", Str(last, "user_id")); Set(db, "derive_location", Str(last, "location"));
            Set(db, "derive_seq", Int(last, "seq")); Set(db, "last_signature", lastSignature);
            Set(db, "observer_location", observerLocation); Set(db, "observer_visit", visit);
            Set(db, "derived", MetaLong(db, "derived") + batch.Count);
        }
        else Finish(db);
        transaction.Commit();
    }

    private static void Close(SQLiteConnection db, Dictionary<string, object?> row, long? end, long? leaveId, string reason)
    {
        Exec(db, @"INSERT INTO sessions(user_id,visit_id,location,join_ms,leave_ms,complete,join_id,leave_id,reason)
            VALUES(@user,@visit,@location,@start,@end,@complete,@join,@leave,@reason)",
            "@user", Str(row, "user_id"), "@visit", Int(row, "visit_id"), "@location", Str(row, "location"),
            "@start", Int(row, "join_ms"), "@end", end, "@complete", end.HasValue ? 1 : 0,
            "@join", Int(row, "join_id"), "@leave", leaveId, "@reason", reason);
        Exec(db, "DELETE FROM open_sessions WHERE user_id=@user", "@user", Str(row, "user_id"));
    }

    private static void Finish(SQLiteConnection db)
    {
        Set(db, "event_count", Number(db, "SELECT COUNT(*) FROM events"));
        Set(db, "session_count", Number(db, "SELECT COUNT(*) FROM sessions WHERE complete=1"));
        Set(db, "rejected_count", Number(db, "SELECT COUNT(*) FROM rejected"));
        Set(db, "phase", "ready"); Set(db, "dirty_from", long.MaxValue);
        Set(db, "updated_at", DateTimeOffset.UtcNow.ToString("O"));
    }

    private SQLiteConnection Ready()
    {
        if (!System.IO.File.Exists(CachePath)) throw new InvalidOperationException("Create and build the local analysis file first.");
        var db = Open(CachePath, true);
        try
        {
            CheckVersion(db);
            if (Get(db, "phase") != "ready") throw new InvalidOperationException("This cache is incomplete. Resume analysis before viewing results.");
            return db;
        }
        catch { db.Dispose(); throw; }
    }

    private object People(JsonElement q)
    {
        using var db = Ready();
        var search = Text(q, "search").Trim();
        if (search.Length > 128) search = search[..128];
        search = search.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
        var friends = q.TryGetProperty("friendsOnly", out var friend) && friend.ValueKind == JsonValueKind.True;
        return new { rows = Rows(db, @"SELECT user_id AS id,name,friend FROM people
            WHERE (@friends=0 OR friend=1) AND (name LIKE @search ESCAPE '\' OR user_id LIKE @search ESCAPE '\')
            ORDER BY name,user_id LIMIT 30", "@friends", friends ? 1 : 0, "@search", "%" + search + "%") };
    }

    private sealed record Scope(string[] Ids, string Placeholders, List<object?> Args, long From, long Until);
    private Scope GetScope(JsonElement q)
    {
        var ids = q.TryGetProperty("userIds", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).Where(id => !IsExcluded(id)).Distinct().ToArray()
            : Array.Empty<string>();
        if (ids.Length == 0 || ids.Length > 200) throw new ArgumentException("Choose between 1 and 200 people for a review.");
        var from = Long(q, "since", 0);
        var until = Long(q, "until", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        if (from >= until) throw new ArgumentException("The start time must be earlier than the end time.");
        var args = new List<object?> { "@from", from, "@until", until };
        var placeholders = new List<string>();
        for (var i = 0; i < ids.Length; i++) { placeholders.Add("@u" + i); args.Add("@u" + i); args.Add(ids[i]); }
        return new Scope(ids, string.Join(",", placeholders), args, from, until);
    }

    private static (long Page, int Size, long Offset) Page(JsonElement q, long count)
    {
        var size = (int)Math.Clamp(Long(q, "size", 30), 1, 100);
        var pages = Math.Max(1, (count + size - 1) / size);
        var page = Math.Clamp(Long(q, "page", 1), 1, pages);
        return (page, size, (page - 1) * size);
    }

    private object Events(JsonElement q)
    {
        using var db = Ready();
        var scope = GetScope(q);
        var filter = Text(q, "filter");
        var category = filter switch
        {
            "encounters" => " AND kind IN ('OnPlayerJoined','OnPlayerLeft')",
            "join" => " AND kind='OnPlayerJoined'",
            "left" => " AND kind='OnPlayerLeft'",
            "status" => " AND kind='Status'",
            "bio" => " AND kind='Bio'",
            "all" or "" => "",
            _ => throw new ArgumentException("Unknown log filter.")
        };
        var where = $"user_id IN ({scope.Placeholders}) AND at_ms>=@from AND at_ms<@until{category}";
        var total = Number(db, "SELECT COUNT(*) FROM events WHERE " + where, scope.Args.ToArray());
        var page = Page(q, total);
        scope.Args.AddRange(new object?[] { "@size", page.Size, "@offset", page.Offset });
        var rows = Rows(db, @"SELECT seq AS id,source,source_id AS sourceId,at_ms AS at,kind AS type,
            user_id AS userId,name AS displayName,location,world AS worldName,detail FROM events WHERE " + where +
            " ORDER BY at_ms DESC,seq DESC LIMIT @size OFFSET @offset", scope.Args.ToArray());
        foreach (var row in rows)
        {
            row["details"] = JsonSerializer.Deserialize<JsonElement>(Str(row, "detail"));
            row.Remove("detail");
        }
        return new { rows, total, page = page.Page, size = page.Size };
    }

    private object Summary(JsonElement q)
    {
        using var db = Ready();
        var scope = GetScope(q);
        var members = new List<object>();
        foreach (var id in scope.Ids)
        {
            var name = Rows(db, "SELECT name FROM people WHERE user_id=@user LIMIT 1", "@user", id).FirstOrDefault();
            var stats = Rows(db, @"SELECT COALESCE(SUM(MIN(leave_ms,@until)-MAX(join_ms,@from)),0) AS observedMs,
                COUNT(*) AS completeSessions FROM sessions WHERE user_id=@user AND complete=1 AND join_ms<@until AND leave_ms>@from",
                "@user", id, "@from", scope.From, "@until", scope.Until)[0];
            var incomplete = Number(db, @"SELECT
                (SELECT COUNT(*) FROM sessions WHERE user_id=@user AND complete=0 AND join_ms>=@from AND join_ms<@until) +
                (SELECT COUNT(*) FROM open_sessions WHERE user_id=@user AND join_ms<@until AND join_ms>=@from)",
                "@user", id, "@from", scope.From, "@until", scope.Until);
            members.Add(new { id, name = name == null ? id : Str(name, "name"), observedMs = Int(stats, "observedMs"),
                completeSessions = Int(stats, "completeSessions"), incompleteSessions = incomplete });
        }
        var groupOnly = scope.Ids.Length > 1 || (q.TryGetProperty("groupOnly", out var groupValue) && groupValue.ValueKind == JsonValueKind.True);
        var groupFilter = groupOnly ? $" AND b.user_id IN ({scope.Placeholders}) AND a.user_id<b.user_id" : "";
        var pairsSql = $@"SELECT a.user_id AS leftId,b.user_id AS rightId,
            SUM(MIN(a.leave_ms,b.leave_ms,@until)-MAX(a.join_ms,b.join_ms,@from)) AS observedMs,COUNT(*) AS segments
            FROM sessions a JOIN sessions b ON a.visit_id=b.visit_id AND a.location=b.location
                AND a.user_id<>b.user_id AND a.join_ms<b.leave_ms AND b.join_ms<a.leave_ms
            WHERE a.complete=1 AND b.complete=1 AND a.user_id IN ({scope.Placeholders})
                AND a.join_ms<@until AND a.leave_ms>@from AND b.join_ms<@until AND b.leave_ms>@from{groupFilter}
            GROUP BY a.user_id,b.user_id";
        var total = Number(db, "SELECT COUNT(*) FROM (" + pairsSql + ")", scope.Args.ToArray());
        var page = Page(q, total);
        scope.Args.AddRange(new object?[] { "@size", page.Size, "@offset", page.Offset });
        var rows = Rows(db, "SELECT p.*,COALESCE(l.name,p.leftId) AS leftName,COALESCE(r.name,p.rightId) AS rightName FROM (" + pairsSql + @") p
            LEFT JOIN people l ON l.user_id=p.leftId LEFT JOIN people r ON r.user_id=p.rightId
            ORDER BY p.observedMs DESC,p.leftId,p.rightId LIMIT @size OFFSET @offset", scope.Args.ToArray());
        return new { members, pairs = new { rows, total, page = page.Page, size = page.Size }, since = scope.From, until = scope.Until };
    }
}
