using System;
using System.Text.Json;
using VRCX.Insights;

namespace VRCX;

// SQLite.cs becomes partial through the reviewed integration script.
public partial class SQLite
{
    public string InsightsRequest(string json)
    {
        try
        {
            if (json == null || json.Length > 65536) throw new ArgumentException("Invalid cache request.");
            using var request = JsonDocument.Parse(json);
            var account = request.RootElement.GetProperty("accountId").GetString() ?? "";
            var cache = new InsightsCache(m_Connection.DataSource, Program.AppDataDirectory, account);
            return cache.Request(json);
        }
        catch (Exception error)
        {
            // Do not expose connection strings, source rows, credentials or stack traces.
            return JsonSerializer.Serialize(new { error = error.Message });
        }
    }
}
