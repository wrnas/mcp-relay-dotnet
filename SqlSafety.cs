using System.Text;

namespace RelayMcp;

/// <summary>
/// Conservative SQL pre-check. The database login must ALSO be read-only;
/// parsing SQL text alone cannot establish database permissions.
/// </summary>
public static class SqlSafety
{
    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT", "UPDATE", "DELETE", "MERGE", "REPLACE", "UPSERT",
        "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "GRANT", "REVOKE", "DENY",
        "EXEC", "EXECUTE", "CALL", "DECLARE", "SET", "USE", "PRAGMA", "ATTACH",
        "DETACH", "VACUUM", "COPY", "LOAD", "LOCK", "UNLOCK", "INTO", "OUTFILE",
        "DUMPFILE", "DO", "ANALYZE", "OPTIMIZE", "REPAIR", "HANDLER"
    };

    public static bool IsReadOnly(string? sql, out string reason)
    {
        reason = "";
        if (string.IsNullOrWhiteSpace(sql))
        {
            reason = "SQL 不能为空";
            return false;
        }

        if (!TryTokenize(sql, out var tokens))
        {
            reason = "SQL 存在未闭合的引号或注释";
            return false;
        }

        if (tokens.Count == 0 || (tokens[0] != "SELECT" && tokens[0] != "WITH"))
        {
            reason = "临时 SQL 只允许 SELECT / WITH ... SELECT 查询";
            return false;
        }

        if (tokens[0] == "WITH" && !tokens.Contains("SELECT"))
        {
            reason = "WITH 必须包含 SELECT 查询";
            return false;
        }

        var semicolons = tokens.FindAll(t => t == ";").Count;
        if (semicolons > 1 || (semicolons == 1 && tokens[^1] != ";"))
        {
            reason = "每次只能执行一条 SQL";
            return false;
        }

        foreach (var token in tokens)
        {
            if (Forbidden.Contains(token))
            {
                reason = $"只读查询不允许使用 {token} 关键字";
                return false;
            }
        }
        return true;
    }

    // Tokenize outside literals, delimited identifiers, and comments.
    // Fail closed on malformed text. This is a second layer, NOT a full SQL parser.
    private static bool TryTokenize(string sql, out List<string> tokens)
    {
        tokens = new List<string>();
        for (var i = 0; i < sql.Length;)
        {
            var c = sql[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i += 2;
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '#')
            {
                while (i < sql.Length && sql[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i += 2;
                var end = sql.IndexOf("*/", i, StringComparison.Ordinal);
                if (end < 0) return false;
                i = end + 2;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`' || c == '[')
            {
                var endChar = c == '[' ? ']' : c;
                var closed = false;
                i++;
                while (i < sql.Length)
                {
                    if (sql[i] == '\\' && c == '\'' && i + 1 < sql.Length) { i += 2; continue; }
                    if (sql[i] == endChar)
                    {
                        if (i + 1 < sql.Length && sql[i + 1] == endChar) { i += 2; continue; }
                        i++;
                        closed = true;
                        break;
                    }
                    i++;
                }
                if (!closed) return false;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i++;
                while (i < sql.Length && (char.IsLetterOrDigit(sql[i]) || sql[i] == '_')) i++;
                tokens.Add(sql[start..i].ToUpperInvariant());
                continue;
            }
            if (c == ';') tokens.Add(";");
            i++;
        }
        return true;
    }
}
