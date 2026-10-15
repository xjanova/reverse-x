using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ReverseX.Core.Analysis.Local;

/// <summary>
/// Opens a SQLite file strictly read-only (immutable, untrusted schema) and lists tables, columns, row counts and sample rows.
/// When a -wal file sits next to it, both are copied to a private temp folder so uncommitted pages are included
/// without touching the original.
/// </summary>
public static partial class SqliteReader
{
    public const int SampleRows = 50;
    public const int CellCap = 200;
    private const int TableCap = 500;

    [GeneratedRegex(@"(pass(word|wd)?|pwd|secret|token|api_?key|auth|session|cookie|credit|card_?(no|num)|cvv|ssn|otp|pin_?code|private_?key|salt|hash)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveRx();

    [GeneratedRegex(@"(e_?mail|phone|mobile|address|birth|dob|lat(itude)?$|lon(gitude)?$|national_?id|passport)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PersonalRx();

    public static DbModel Read(string path, IProgress<AnalysisProgress>? progress, CancellationToken ct)
    {
        var header = new byte[100];
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            if (fs.Read(header, 0, 100) < 100) throw new InvalidDataException("file too small for SQLite");
        if (Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3") throw new InvalidDataException("not a SQLite 3 file");

        var pageSize = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(16)) is var ps && ps == 1 ? 65536 : ps;
        var walMode = header[18] == 2 || header[19] == 2;
        var pageCount = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(28));
        var freelist = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(36));
        var encoding = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(56)) switch { 1 => "UTF-8", 2 => "UTF-16le", 3 => "UTF-16be", _ => "?" };
        var userVersion = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(60));
        var appId = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(68));
        var writer = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(96));
        var writerText = writer == 0 ? "?" : $"{writer / 1_000_000}.{writer / 1000 % 1000}.{writer % 1000}";

        var wal = path + "-wal";
        var walBytes = File.Exists(wal) ? new FileInfo(wal).Length : 0;
        string? tmp = null;
        string connStr;
        if (walBytes > 0)
        {
            tmp = Path.Combine(Path.GetTempPath(), "ReverseX-db-" + Guid.NewGuid().ToString("N")[..12]);
            Directory.CreateDirectory(tmp);
            var copy = Path.Combine(tmp, "copy.db");
            File.Copy(path, copy);
            File.Copy(wal, copy + "-wal");
            connStr = new SqliteConnectionStringBuilder { DataSource = copy, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString();
        }
        else
        {
            var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?immutable=1";
            connStr = new SqliteConnectionStringBuilder { DataSource = uri, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        }

        try
        {
            using var con = new SqliteConnection(connStr);
            con.Open();
            Exec(con, "PRAGMA trusted_schema=OFF");
            Exec(con, "PRAGMA query_only=ON");
            var engine = Scalar(con, "select sqlite_version()") ?? "";

            var master = new List<(string Type, string Name, string TblName, string Sql)>();
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "select type, name, tbl_name, coalesce(sql,'') from sqlite_master order by type, name";
                using var r = cmd.ExecuteReader();
                while (r.Read()) master.Add((r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
            }

            var tables = new List<DbTable>();
            var tableRows = master.Where(m => m.Type is "table" or "view").Take(TableCap).ToList();
            var i = 0;
            foreach (var t in tableRows)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new AnalysisProgress("db.table", ++i, tableRows.Count));
                tables.Add(ReadTable(con, t.Type, t.Name, t.Sql, master.Where(m => m.Type == "index" && m.TblName == t.Name).Select(m => m.Name).ToList()));
            }

            var triggers = master.Where(m => m.Type == "trigger").ToList();
            var model = new DbModel
            {
                PageSize = pageSize,
                PageCount = pageCount,
                FreelistPages = freelist,
                TextEncoding = encoding,
                UserVersion = userVersion,
                ApplicationId = appId,
                WalMode = walMode,
                WalBytes = walBytes,
                WalIncluded = tmp is not null,
                WriterVersion = writerText,
                EngineVersion = engine,
                Origins = Origins(tables.Select(t => t.Name).ToList(), appId),
                Tables = tables.OrderBy(t => t.Name.StartsWith("sqlite_", StringComparison.Ordinal)).ThenByDescending(t => t.RowCount ?? -1).ThenBy(t => t.Name, StringComparer.Ordinal).ToList(),
                Triggers = triggers.Select(t => new NamedCount(t.Name + " → " + t.TblName, 0)).ToList(),
                TriggerSql = triggers.Select(t => t.Sql).ToList(),
            };
            return model with { Insights = Insights(model) };
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (tmp is not null)
                try { Directory.Delete(tmp, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Q(string ident) => "\"" + ident.Replace("\"", "\"\"") + "\"";

    private static void Exec(SqliteConnection con, string sql)
    {
        using var c = con.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection con, string sql)
    {
        using var c = con.CreateCommand();
        c.CommandText = sql;
        return Convert.ToString(c.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static DbTable ReadTable(SqliteConnection con, string type, string name, string sql, List<string> indexes)
    {
        var isVirtual = sql.StartsWith("CREATE VIRTUAL", StringComparison.OrdinalIgnoreCase);
        var cols = new List<DbColumn>();
        var fks = new List<string>();
        try
        {
            using var c = con.CreateCommand();
            c.CommandText = $"PRAGMA table_info({Q(name)})";
            using var r = c.ExecuteReader();
            while (r.Read())
                cols.Add(new DbColumn(r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), r.GetInt64(3) != 0, r.GetInt64(5) != 0, r.IsDBNull(4) ? null : r.GetString(4)));
        }
        catch (SqliteException) { }
        try
        {
            using var c = con.CreateCommand();
            c.CommandText = $"PRAGMA foreign_key_list({Q(name)})";
            using var r = c.ExecuteReader();
            while (r.Read()) fks.Add($"{r.GetString(3)} → {r.GetString(2)}.{(r.IsDBNull(4) ? "?" : r.GetString(4))}");
        }
        catch (SqliteException) { }

        long? count = null;
        var sampleCols = new List<string>();
        var sample = new List<List<string?>>();
        try
        {
            using var c = con.CreateCommand();
            c.CommandText = $"select count(*) from {Q(name)}";
            count = Convert.ToInt64(c.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        catch (SqliteException) { }
        try
        {
            using var c = con.CreateCommand();
            c.CommandText = $"select * from {Q(name)} limit {SampleRows}";
            using var r = c.ExecuteReader();
            for (var k = 0; k < r.FieldCount; k++) sampleCols.Add(r.GetName(k));
            while (r.Read())
            {
                var row = new List<string?>(r.FieldCount);
                for (var k = 0; k < r.FieldCount; k++) row.Add(Cell(r, k));
                sample.Add(row);
            }
        }
        catch (SqliteException) { }

        var sensitive = cols.Select(x => x.Name).Where(n => SensitiveRx().IsMatch(n) || PersonalRx().IsMatch(n)).ToList();
        return new DbTable
        {
            Name = name,
            Type = isVirtual ? "virtual" : type,
            RowCount = count,
            Sql = sql,
            Columns = cols,
            Indexes = indexes,
            ForeignKeys = fks,
            SampleColumns = sampleCols,
            Sample = sample,
            SensitiveColumns = sensitive,
        };
    }

    private static string? Cell(SqliteDataReader r, int k)
    {
        if (r.IsDBNull(k)) return null;
        var v = r.GetValue(k);
        if (v is byte[] b) return DescribeBlob(b);
        var s = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
        s = s.Replace('\r', ' ').Replace('\n', ' ');
        return s.Length > CellCap ? s[..CellCap] + "…" : s;
    }

    /// <summary>Short label for a BLOB: recognised file type or hex preview, plus size.</summary>
    public static string DescribeBlob(byte[] b)
    {
        string? kind = b switch
        {
            [0x89, 0x50, 0x4E, 0x47, ..] => "PNG",
            [0xFF, 0xD8, 0xFF, ..] => "JPEG",
            [0x47, 0x49, 0x46, 0x38, ..] => "GIF",
            [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => "WebP",
            [0x1F, 0x8B, ..] => "gzip",
            [0x50, 0x4B, 0x03, 0x04, ..] => "ZIP",
            [0x62, 0x70, 0x6C, 0x69, 0x73, 0x74, ..] => "bplist",
            [0x25, 0x50, 0x44, 0x46, ..] => "PDF",
            [0x53, 0x51, 0x4C, 0x69, 0x74, 0x65, ..] => "SQLite",
            [0x7B, ..] or [0x5B, ..] => "JSON?",
            _ => null,
        };
        var hex = Convert.ToHexString(b.AsSpan(0, Math.Min(b.Length, 12)));
        return $"BLOB {(kind is null ? "" : kind + " ")}{b.Length:N0} B · {hex}{(b.Length > 12 ? "…" : "")}";
    }

    private static List<string> Origins(List<string> names, uint appId)
    {
        var set = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var o = new List<string>();
        if (set.Contains("android_metadata")) o.Add("Android app database");
        if (set.Contains("room_master_table")) o.Add("Android Room");
        if (set.Contains("__EFMigrationsHistory")) o.Add("Entity Framework Core");
        if (set.Contains("Z_PRIMARYKEY") && set.Contains("Z_METADATA")) o.Add("Apple Core Data");
        if (set.Contains("moz_places")) o.Add("Firefox history");
        if (set.Contains("moz_cookies")) o.Add("Firefox cookies");
        if (set.Contains("urls") && set.Contains("visits")) o.Add("Chromium history");
        if (set.Contains("cookies") && set.Contains("meta") && names.Count < 6) o.Add("Chromium cookies");
        if (set.Contains("logins") && set.Contains("meta")) o.Add("Chromium saved logins (encrypted)");
        if (set.Contains("message") && set.Contains("chat")) o.Add("Messaging app (chat/message)");
        if (set.Contains("schema_migrations")) o.Add("Rails / migration-based app");
        if (set.Contains("django_migrations")) o.Add("Django");
        if (set.Contains("knex_migrations")) o.Add("Knex.js");
        if (set.Contains("_prisma_migrations")) o.Add("Prisma");
        if (set.Contains("ItemTable") && names.Count <= 2) o.Add("VS Code / Electron state");
        if (set.Contains("gpkg_contents")) o.Add("GeoPackage");
        if (set.Contains("mbtiles") || set.Contains("tiles") && set.Contains("metadata")) o.Add("MBTiles map");
        if (appId == 0x47504B47 || appId == 0x47503130) o.Add("GeoPackage (application_id)");
        if (appId == 0x0F055112 || appId == 0x0F055113) o.Add("Fossil SCM");
        return o;
    }

    private static List<Insight> Insights(DbModel m)
    {
        var list = new List<Insight>();
        foreach (var o in m.Origins) list.Add(Insight.Of("DbOrigin", InsightTone.Good, EvidenceLevel.Inferred, o));
        var sens = m.Tables.Where(t => t.SensitiveColumns.Count > 0).ToList();
        if (sens.Count > 0)
            list.Add(Insight.Of("DbSensitive", InsightTone.Warn, EvidenceLevel.Inferred, sens.Sum(t => t.SensitiveColumns.Count),
                string.Join(", ", sens.SelectMany(t => t.SensitiveColumns.Select(c => t.Name + "." + c)).Take(4))));
        if (m.FreelistPages > 0)
            list.Add(Insight.Of("DbFreelist", InsightTone.Info, EvidenceLevel.Seen, m.FreelistPages, ByteSizeText(m.FreelistPages * m.PageSize)));
        if (m.WalBytes > 0)
            list.Add(Insight.Of("DbWal", InsightTone.Info, EvidenceLevel.Seen, ByteSizeText(m.WalBytes)));
        if (m.Triggers.Count > 0) list.Add(Insight.Of("DbTriggers", InsightTone.Info, EvidenceLevel.Seen, m.Triggers.Count));
        var unreadable = m.Tables.Count(t => t.RowCount is null);
        if (unreadable > 0) list.Add(Insight.Of("DbUnreadable", InsightTone.Warn, EvidenceLevel.Unknown, unreadable));
        var big = m.Tables.Where(t => t.RowCount > 0).OrderByDescending(t => t.RowCount).FirstOrDefault();
        if (big is not null) list.Add(Insight.Of("DbBiggest", InsightTone.Info, EvidenceLevel.Seen, big.Name, big.RowCount!.Value.ToString("N0", CultureInfo.InvariantCulture)));
        return list;
    }

    private static string ByteSizeText(long b) => ReverseX.Core.Files.ByteSize.Format(b);
}
