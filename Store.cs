using Microsoft.Data.Sqlite;
namespace StockSentinel;
public sealed class Store(IConfiguration cfg)
{
    readonly string path = Path.GetFullPath(cfg["Sentinel:DatabasePath"] ?? "data/sentinel.db");
    SqliteConnection Open() { var c = new SqliteConnection($"Data Source={path};Pooling=True"); c.Open(); using var pragma=c.CreateCommand(); pragma.CommandText="PRAGMA busy_timeout=5000;";pragma.ExecuteNonQuery();return c; }
    public void Init()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var c=Open(); using var cmd=c.CreateCommand(); cmd.CommandText="""
        PRAGMA journal_mode=WAL;
        CREATE TABLE IF NOT EXISTS items(id INTEGER PRIMARY KEY, name TEXT NOT NULL, max_price REAL NOT NULL, enabled INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS sources(id INTEGER PRIMARY KEY, item_id INTEGER NOT NULL REFERENCES items(id), name TEXT NOT NULL, url TEXT NOT NULL, interval_mins INTEGER NOT NULL, enabled INTEGER NOT NULL, adapter TEXT NOT NULL DEFAULT 'html', available_selector TEXT, unavailable_selector TEXT, price_selector TEXT, price_pattern TEXT, availability_text TEXT, unavailability_text TEXT, status TEXT NOT NULL DEFAULT 'Unknown', last_detail TEXT, last_checked TEXT, next_check TEXT, fail_count INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY, at_utc TEXT NOT NULL, level TEXT NOT NULL, message TEXT NOT NULL, source_id INTEGER);
        CREATE TABLE IF NOT EXISTS notifications(source_id INTEGER PRIMARY KEY, sent_at TEXT NOT NULL, status TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_events_at ON events(id DESC);
        CREATE TABLE IF NOT EXISTS check_history(id INTEGER PRIMARY KEY, source_id INTEGER NOT NULL, at_utc TEXT NOT NULL, status TEXT NOT NULL, price REAL, duration_ms INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_checks_source_at ON check_history(source_id, at_utc);
        CREATE TABLE IF NOT EXISTS alert_reminders(source_id INTEGER PRIMARY KEY, first_sent_utc TEXT NOT NULL, count INTEGER NOT NULL DEFAULT 1);
        """;cmd.ExecuteNonQuery();
        using var migration=c.CreateCommand();migration.CommandText="PRAGMA table_info(sources)";using var columns=migration.ExecuteReader();bool exists=false;while(columns.Read())if(columns.GetString(1)=="json_config")exists=true;columns.Close();if(!exists)Run(c,"ALTER TABLE sources ADD COLUMN json_config TEXT");
    }
    static object Val(object? value) => value ?? DBNull.Value;
    static void Run(SqliteConnection c,string sql,params (string,object?)[] args){using var cmd=c.CreateCommand();cmd.CommandText=sql;foreach(var (k,v) in args)cmd.Parameters.AddWithValue(k,Val(v));cmd.ExecuteNonQuery();}
    static long LastInsertedId(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
    public List<Item> Items(){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT id,name,max_price,enabled FROM items ORDER BY id";using var r=q.ExecuteReader();var list=new List<Item>();while(r.Read())list.Add(new(r.GetInt64(0),r.GetString(1),Convert.ToDecimal(r.GetDouble(2)),r.GetInt64(3)!=0));return list;}
    public long AddItem(NewItem i){using var c=Open();Run(c,"INSERT INTO items(name,max_price,enabled) VALUES($n,$p,$e)",("$n",i.Name),("$p",i.MaxPriceGbp),("$e",i.Enabled?1:0));return LastInsertedId(c);}
    public bool UpdateItem(long id,NewItem i){using var c=Open();using var q=c.CreateCommand();q.CommandText="UPDATE items SET name=$n,max_price=$p,enabled=$e WHERE id=$id";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$n",i.Name);q.Parameters.AddWithValue("$p",i.MaxPriceGbp);q.Parameters.AddWithValue("$e",i.Enabled?1:0);return q.ExecuteNonQuery()>0;}
    public List<Source> Sources(){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT id,item_id,name,url,interval_mins,enabled,adapter,available_selector,unavailable_selector,price_selector,price_pattern,availability_text,unavailability_text,status,last_detail,last_checked,next_check,fail_count,json_config FROM sources ORDER BY id";using var r=q.ExecuteReader();var list=new List<Source>();while(r.Read()){string? S(int n)=>r.IsDBNull(n)?null:r.GetString(n);list.Add(new(r.GetInt64(0),r.GetInt64(1),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt64(5)!=0,r.GetString(6),S(7),S(8),S(9),S(10),S(11),S(12),r.GetString(13),S(14),S(15),S(16),r.GetInt32(17),S(18)));}return list;}
    public Source? GetSource(long id)=>Sources().FirstOrDefault(x=>x.Id==id);
    public long AddSource(NewSource s){using var c=Open();using var q=c.CreateCommand();q.CommandText="""INSERT INTO sources(item_id,name,url,interval_mins,enabled,adapter,available_selector,unavailable_selector,price_selector,price_pattern,availability_text,unavailability_text,json_config) VALUES($i,$n,$u,$m,$e,$a,$av,$un,$ps,$pp,$at,$ut,$jc)""";BindSource(q,s);q.ExecuteNonQuery();return LastInsertedId(c);}
    public bool UpdateSource(long id,NewSource s){using var c=Open();using var q=c.CreateCommand();q.CommandText="""UPDATE sources SET item_id=$i,name=$n,url=$u,interval_mins=$m,enabled=$e,adapter=$a,available_selector=$av,unavailable_selector=$un,price_selector=$ps,price_pattern=$pp,availability_text=$at,unavailability_text=$ut,json_config=$jc,next_check=NULL,status='Unknown' WHERE id=$id""";BindSource(q,s);q.Parameters.AddWithValue("$id",id);return q.ExecuteNonQuery()>0;}
    static void BindSource(SqliteCommand q,NewSource s){foreach(var (k,v) in new (string,object?)[]{("$i",s.ItemId),("$n",s.Name),("$u",s.Url),("$m",s.IntervalMinutes),("$e",s.Enabled?1:0),("$a",s.Adapter),("$av",s.AvailableSelector),("$un",s.UnavailableSelector),("$ps",s.PriceSelector),("$pp",s.PricePattern),("$at",s.AvailabilityText),("$ut",s.UnavailabilityText),("$jc",s.JsonConfig)})q.Parameters.AddWithValue(k,Val(v));}
    public void DeleteSource(long id){using var c=Open();Run(c,"DELETE FROM sources WHERE id=$id",("$id",id));Run(c,"DELETE FROM notifications WHERE source_id=$id",("$id",id));Run(c,"DELETE FROM alert_reminders WHERE source_id=$id",("$id",id));}
    public List<Event> Events(int limit=150){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT id,at_utc,level,message,source_id FROM events ORDER BY id DESC LIMIT $lim";q.Parameters.AddWithValue("$lim",Math.Clamp(limit,1,500));using var r=q.ExecuteReader();var list=new List<Event>();while(r.Read())list.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetInt64(4)));return list;}
    public void Log(string level,string message,long? sourceId=null){using var c=Open();Run(c,"INSERT INTO events(at_utc,level,message,source_id) VALUES($t,$l,$m,$s)",("$t",DateTimeOffset.UtcNow.ToString("O")),("$l",level),("$m",message),("$s",sourceId));Run(c,"DELETE FROM events WHERE id NOT IN (SELECT id FROM events ORDER BY id DESC LIMIT 5000)");}
    public void RecordCheck(long id,CheckResult result,long elapsedMs)
    {
        using var c=Open();
        Run(c,"INSERT INTO check_history(source_id,at_utc,status,price,duration_ms) VALUES($id,$at,$s,$p,$ms)",("$id",id),("$at",DateTimeOffset.UtcNow.ToString("O")),("$s",result.Status),("$p",result.Price),("$ms",elapsedMs));
        Run(c,"DELETE FROM check_history WHERE at_utc < $cutoff",("$cutoff",DateTimeOffset.UtcNow.AddDays(-90).ToString("O")));
    }
    public object Metrics(int days, long? itemId = null)
    {
        days = Math.Clamp(days, 1, 90);
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        using var c = Open();
        using var cmd = c.CreateCommand();
        // Keep orphaned history in global metrics. A product can only claim history
        // whose source still has a known product association; never infer it from names.
        const string productFilter = " AND ($itemId IS NULL OR source_id IN (SELECT id FROM sources WHERE item_id=$itemId))";
        cmd.CommandText = "SELECT source_id,at_utc,status,duration_ms FROM check_history WHERE at_utc >= $cutoff" + productFilter + " ORDER BY source_id,at_utc,id";
        cmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        cmd.Parameters.AddWithValue("$itemId", Val(itemId));
        var checks = new List<(long SourceId,DateTimeOffset At,string Status,long Duration)>();
        using (var r = cmd.ExecuteReader())
            while (r.Read()) checks.Add((r.GetInt64(0), DateTimeOffset.Parse(r.GetString(1)), r.GetString(2), r.GetInt64(3)));

        // Older installations logged every check in events. Use those observations only
        // before the first detailed check for each source to prevent double counting.
        var firstDetailed = checks.GroupBy(x => x.SourceId)
            .ToDictionary(g => g.Key, g => g.Min(x => x.At));
        var observations = checks.Select(x => (x.SourceId, x.At, x.Status, Detailed: true)).ToList();
        using (var eventCmd = c.CreateCommand())
        {
            eventCmd.CommandText = "SELECT source_id,at_utc,message FROM events WHERE at_utc >= $cutoff AND source_id IS NOT NULL AND level IN ('info','warning')" + productFilter + " ORDER BY source_id,at_utc,id";
            eventCmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
            eventCmd.Parameters.AddWithValue("$itemId", Val(itemId));
            using var r = eventCmd.ExecuteReader();
            while (r.Read())
            {
                var sourceId = r.GetInt64(0);
                var at = DateTimeOffset.Parse(r.GetString(1));
                if (firstDetailed.TryGetValue(sourceId, out var first) && at >= first) continue;
                var message = r.GetString(2);
                // Monitoring logs use "Source name: Available — detail"; other
                // info/warning events must not be treated as availability checks.
                var colon = message.IndexOf(": ", StringComparison.Ordinal);
                if (colon < 0) continue;
                var statusStart = colon + 2;
                string? status = null;
                foreach (var candidate in new[] { "Available", "Unavailable", "Unknown", "Error" })
                {
                    if (message.AsSpan(statusStart).StartsWith(candidate + " —", StringComparison.Ordinal))
                    { status = candidate; break; }
                }
                if (status is not null) observations.Add((sourceId, at, status, false));
            }
        }

        using var alertCmd = c.CreateCommand();
        alertCmd.CommandText = "SELECT COUNT(*) FROM events WHERE at_utc >= $cutoff AND level='alert' AND (message LIKE 'Push accepted%' OR message LIKE 'Reminder push accepted%')" + productFilter;
        alertCmd.Parameters.AddWithValue("$cutoff", cutoff.ToString("O"));
        alertCmd.Parameters.AddWithValue("$itemId", Val(itemId));
        var pushAlerts = Convert.ToInt32(alertCmd.ExecuteScalar());

        var restocks = new List<(long SourceId,DateTimeOffset At,double Minutes,bool Historical)>();
        var hours = new int[24]; var weekdays = new int[7];
        foreach (var group in observations.GroupBy(x => x.SourceId))
        {
            DateTimeOffset? first = null, last = null;
            bool historical = false;
            void Finish()
            {
                if (first.HasValue && last.HasValue)
                    restocks.Add((group.Key, first.Value, Math.Round((last.Value-first.Value).TotalMinutes, 1), historical));
                first = last = null;
                historical = false;
            }
            foreach (var check in group.OrderBy(x => x.At))
            {
                if (check.Status == "Available")
                {
                    if (!first.HasValue)
                    {
                        first = check.At;
                        var local = TimeZoneInfo.ConvertTime(check.At, TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
                        hours[local.Hour]++;
                        weekdays[(int)local.DayOfWeek]++;
                    }
                    last = check.At;
                    historical |= !check.Detailed;
                }
                else if (check.Status == "Unavailable") Finish();
                // Error/Unknown is not evidence that a restock ended.
            }
            Finish();
        }
        return new
        {
            days,
            totalChecks = checks.Count,
            successfulChecks = checks.Count(x => x.Status is "Available" or "Unavailable"),
            failedChecks = checks.Count(x => x.Status is "Error" or "Unknown"),
            historicalObservations = observations.Count(x => !x.Detailed),
            pushAlerts,
            restockCount = restocks.Count,
            restocks = restocks.OrderByDescending(x => x.At).Take(30)
                .Select(x => new { sourceId = x.SourceId, atUtc = x.At, confirmedMinutes = x.Minutes, historical = x.Historical }).ToArray(),
            byHour = hours,
            byWeekday = weekdays,
            averageResponseMs = checks.Count == 0 ? 0 : Math.Round(checks.Average(x => x.Duration)),
            note = "Restocks combine existing event logs and new detailed checks. Durations are minimum observed spans; old events may have been purged."
        };
    }
    public List<(long SourceId,int Count)> DueReminders(DateTimeOffset now)
    {
        using var c=Open();using var q=c.CreateCommand();
        q.CommandText="""SELECT a.source_id,a.count FROM alert_reminders a JOIN sources s ON s.id=a.source_id JOIN items i ON i.id=s.item_id WHERE s.enabled=1 AND i.enabled=1 AND s.status='Available' AND a.count<3 AND ((a.count=1 AND a.first_sent_utc <= $two) OR (a.count=2 AND a.first_sent_utc <= $five)) AND s.last_checked >= $fresh AND (SELECT h.price FROM check_history h WHERE h.source_id=s.id ORDER BY h.id DESC LIMIT 1) <= i.max_price""";
        q.Parameters.AddWithValue("$two",now.AddMinutes(-2).ToString("O"));q.Parameters.AddWithValue("$five",now.AddMinutes(-5).ToString("O"));q.Parameters.AddWithValue("$fresh",now.AddMinutes(-6).ToString("O"));
        using var r=q.ExecuteReader();var list=new List<(long,int)>();while(r.Read())list.Add((r.GetInt64(0),r.GetInt32(1)));return list;
    }
    public bool ClaimReminder(long id,int expectedCount)
    {
        using var c=Open();using var q=c.CreateCommand();q.CommandText="UPDATE alert_reminders SET count=count+1 WHERE source_id=$id AND count=$count AND EXISTS (SELECT 1 FROM sources WHERE id=$id AND status='Available' AND enabled=1)";q.Parameters.AddWithValue("$id",id);q.Parameters.AddWithValue("$count",expectedCount);return q.ExecuteNonQuery()==1;
    }
    public void Record(long id,CheckResult result,int failCount,DateTimeOffset next){using var c=Open();Run(c,"UPDATE sources SET status=$s,last_detail=$d,last_checked=$t,next_check=$n,fail_count=$f WHERE id=$id",("$s",result.Status),("$d",result.Detail),("$t",DateTimeOffset.UtcNow.ToString("O")),("$n",next.ToString("O")),("$f",failCount),("$id",id));}
    public bool HasNotified(long id){using var c=Open();using var q=c.CreateCommand();q.CommandText="SELECT count(*) FROM notifications WHERE source_id=$id";q.Parameters.AddWithValue("$id",id);return Convert.ToInt64(q.ExecuteScalar())>0;}
    public void MarkNotified(long id){using var c=Open();var now=DateTimeOffset.UtcNow.ToString("O");Run(c,"INSERT INTO notifications(source_id,sent_at,status) VALUES($id,$t,'sent') ON CONFLICT(source_id) DO UPDATE SET sent_at=$t,status='sent'",("$id",id),("$t",now));Run(c,"INSERT INTO alert_reminders(source_id,first_sent_utc,count) VALUES($id,$t,1) ON CONFLICT(source_id) DO UPDATE SET first_sent_utc=$t,count=1",("$id",id),("$t",now));}
    public void ClearNotification(long id){using var c=Open();Run(c,"DELETE FROM notifications WHERE source_id=$id",("$id",id));Run(c,"DELETE FROM alert_reminders WHERE source_id=$id",("$id",id));}
}
