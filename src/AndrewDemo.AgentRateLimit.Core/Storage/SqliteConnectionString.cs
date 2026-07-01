namespace AndrewDemo.AgentRateLimit.Core.Storage;

internal static class SqliteConnectionString
{
    public static string GetDataSource(string connectionString)
    {
        foreach (var segment in connectionString.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = segment.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                continue;
            }

            var key = segment[..separator].Trim();
            var value = segment[(separator + 1)..].Trim();

            if (StringComparer.OrdinalIgnoreCase.Equals(key, "Data Source") ||
                StringComparer.OrdinalIgnoreCase.Equals(key, "DataSource"))
            {
                return value;
            }
        }

        return connectionString;
    }
}
