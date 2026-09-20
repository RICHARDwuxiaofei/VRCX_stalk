using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VRCX.Insights;

/// <summary>
/// Read-only compatibility adapter for historical official VRCX SQLite layouts.
/// It never ALTERs or writes the source database. Rows are projected into the
/// current canonical field names before they enter the disposable analysis cache.
/// </summary>
public sealed partial class InsightsCache
{
    private sealed record SourceMap(
        Source Spec,
        string LogicalName,
        string IdSql,
        string Projection,
        string Signature,
        bool Legacy
    );

    private sealed record SourceFamily(string LogicalName, string Kind, bool Friend, string[] Suffixes, string[] Globals);

    private static readonly SourceFamily[] SourceFamilies =
    {
        new("gamelog_location", "Location", false, Array.Empty<string>(),
            new[] { "gamelog_location", "game_log_location", "gamelog_locations" }),
        new("gamelog_join_leave", "", false, Array.Empty<string>(),
            new[] { "gamelog_join_leave", "game_log_join_leave", "gamelog_joinleave" }),
        new("feed_status", "Status", false, new[] { "_feed_status" }, Array.Empty<string>()),
        new("feed_bio", "Bio", false, new[] { "_feed_bio" }, Array.Empty<string>()),
        new("feed_gps", "GPS", false, new[] { "_feed_gps" }, Array.Empty<string>()),
        new("feed_online_offline", "", false, new[] { "_feed_online_offline" }, Array.Empty<string>()),
        new("feed_avatar", "Avatar", false, new[] { "_feed_avatar" }, Array.Empty<string>()),
        new("friend_log_history", "", false, new[] { "_friend_log_history" }, Array.Empty<string>()),
        new("friend_log_current", "Friend", true, new[] { "_friend_log_current" }, Array.Empty<string>())
    };

    private static string QuoteIdentifier(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
    private static string SqlColumn(string value) => value.Equals("rowid", StringComparison.OrdinalIgnoreCase) ? "rowid" : QuoteIdentifier(value);

    private string[] AccountPrefixes()
    {
        var stripped = account.Replace("-", "").Replace("_", "");
        var current = Regex.IsMatch(stripped, @"^\d") ? "_" + stripped : stripped;
        return new[] { current, stripped }.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static Dictionary<string, string> ColumnMap(SQLiteConnection db, string table) =>
        Rows(db, $"PRAGMA table_info({QuoteIdentifier(table)})")
            .Select(r => Str(r, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);

    private static string? Pick(Dictionary<string, string> columns, params string[] candidates)
    {
        foreach (var candidate in candidates)
            if (columns.TryGetValue(candidate, out var actual)) return actual;
        return null;
    }

    private static string Alias(string? actual, string canonical, string fallback = "''") =>
        actual == null ? $"{fallback} AS {QuoteIdentifier(canonical)}" : $"{SqlColumn(actual)} AS {QuoteIdentifier(canonical)}";

    private static string? FindTable(HashSet<string> tables, IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            var match = tables.FirstOrDefault(name => name.Equals(candidate, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    private List<SourceMap> ResolveSourceMaps(SQLiteConnection source, List<string>? warnings = null)
    {
        var tables = Rows(source, "SELECT name FROM sqlite_schema WHERE type='table'")
            .Select(r => Str(r, "name"))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prefixes = AccountPrefixes();
        var result = new List<SourceMap>();

        foreach (var family in SourceFamilies)
        {
            string? table;
            if (family.Globals.Length > 0)
            {
                table = FindTable(tables, family.Globals);
            }
            else
            {
                var candidates = prefixes.SelectMany(prefix => family.Suffixes.Select(suffix => prefix + suffix));
                table = FindTable(tables, candidates);
            }

            if (table == null)
            {
                warnings?.Add(family.LogicalName + ": unavailable in this VRCX database");
                continue;
            }

            var columns = ColumnMap(source, table);
            var map = BuildSourceMap(family, table, columns, warnings);
            if (map != null) result.Add(map);
        }

        return result;
    }

    private SourceMap? ResolveSourceMap(SQLiteConnection source, string table)
    {
        var warnings = new List<string>();
        return ResolveSourceMaps(source, warnings)
            .FirstOrDefault(map => map.Spec.Name.Equals(table, StringComparison.OrdinalIgnoreCase));
    }

    private SourceMap? BuildSourceMap(SourceFamily family, string table, Dictionary<string, string> columns, List<string>? warnings)
    {
        var id = family.Friend ? "rowid" : Pick(columns, "id", "row_id", "rowId") ?? "rowid";
        var created = family.Friend ? null : Pick(columns, "created_at", "createdAt", "created", "timestamp", "datetime", "date_time");
        var type = Pick(columns, "type", "event_type", "eventType", "kind");
        var user = Pick(columns, "user_id", "userId", "userid", "player_id", "playerId");
        var name = Pick(columns, "display_name", "displayName", "user_name", "username", "name");
        var location = Pick(columns, "location", "location_tag", "locationTag");
        var world = Pick(columns, "world_name", "worldName", "world_id", "worldId");

        if (!family.Friend && created == null)
        {
            warnings?.Add($"{table}: unsupported legacy table (no creation timestamp column); skipped");
            return null;
        }
        if (family.LogicalName == "gamelog_location" && location == null)
        {
            warnings?.Add($"{table}: legacy location table has no exact instance location; skipped for encounter reconstruction");
            return null;
        }
        if (family.LogicalName == "gamelog_join_leave" && type == null)
        {
            warnings?.Add($"{table}: legacy join/leave table has no event type column; skipped");
            return null;
        }

        var detailFields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["status"] = Pick(columns, "status"),
            ["status_description"] = Pick(columns, "status_description", "statusDescription"),
            ["previous_status"] = Pick(columns, "previous_status", "previousStatus"),
            ["previous_status_description"] = Pick(columns, "previous_status_description", "previousStatusDescription"),
            ["bio"] = Pick(columns, "bio"),
            ["previous_bio"] = Pick(columns, "previous_bio", "previousBio"),
            ["previous_location"] = Pick(columns, "previous_location", "previousLocation"),
            ["avatar_name"] = Pick(columns, "avatar_name", "avatarName"),
            ["previous_display_name"] = Pick(columns, "previous_display_name", "previousDisplayName"),
            ["trust_level"] = Pick(columns, "trust_level", "trustLevel"),
            ["previous_trust_level"] = Pick(columns, "previous_trust_level", "previousTrustLevel")
        };

        var projection = new List<string>
        {
            $"{SqlColumn(id)} AS source_row_id",
            family.Friend ? "'' AS created_at" : Alias(created, "created_at"),
            Alias(type, "type"),
            Alias(name, "display_name"),
            Alias(location, "location"),
            Alias(user, "user_id"),
            Alias(world, "world_name")
        };
        projection.AddRange(detailFields.Select(field => Alias(field.Value, field.Key)));

        var canonicalTable = family.Globals.FirstOrDefault() ??
            AccountPrefixes().First() + family.Suffixes.FirstOrDefault();
        var canonicalColumns = family.Friend
            ? id.Equals("rowid", StringComparison.OrdinalIgnoreCase) && user?.Equals("user_id", StringComparison.OrdinalIgnoreCase) == true && name?.Equals("display_name", StringComparison.OrdinalIgnoreCase) == true
            : id.Equals("id", StringComparison.OrdinalIgnoreCase) && created?.Equals("created_at", StringComparison.OrdinalIgnoreCase) == true;
        var legacy = !table.Equals(canonicalTable, StringComparison.OrdinalIgnoreCase) || !canonicalColumns ||
            new[] { type, user, name, location, world }.Where(x => x != null).Any(actual =>
                actual is not ("type" or "user_id" or "display_name" or "location" or "world_name"));

        if (legacy)
            warnings?.Add($"{family.LogicalName}: legacy layout detected in {table}; normalized read-only into the analysis cache");

        var signature = JsonSerializer.Serialize(new
        {
            logical = family.LogicalName,
            table,
            id,
            created,
            type,
            user,
            name,
            location,
            world,
            details = detailFields
        });
        return new SourceMap(new Source(table, family.Kind, family.Friend), family.LogicalName, SqlColumn(id),
            string.Join(",", projection), signature, legacy);
    }

    private static readonly Regex LegacyIdentity = new(
        @"^(?<name>.*?)\s*\((?<id>usr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})\)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static bool ValidUserId(string value) =>
        Regex.IsMatch(value ?? "", @"^usr_[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$");

    private static void NormalizeLegacyIdentity(ref string user, ref string displayName)
    {
        var match = LegacyIdentity.Match(displayName ?? "");
        if (!ValidUserId(user) && match.Success) user = match.Groups["id"].Value;
        if (match.Success && (string.IsNullOrEmpty(user) || user.Equals(match.Groups["id"].Value, StringComparison.OrdinalIgnoreCase)))
            displayName = match.Groups["name"].Value.Trim();
    }

    private static string NormalizeLegacyKind(string kind) => kind switch
    {
        "OnPlayerJoin" or "PlayerJoined" or "Joined" or "Join" => "OnPlayerJoined",
        "OnPlayerLeave" or "PlayerLeft" or "Left" or "Leave" => "OnPlayerLeft",
        _ => kind
    };
}
