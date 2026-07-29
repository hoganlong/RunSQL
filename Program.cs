using System.Security.Cryptography;
using System.Text;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using Npgsql;

// RunSql — ad-hoc query console for the Keith Long archive Postgres DB.
//
// SAFE BY DEFAULT: queries run inside an explicit READ ONLY transaction, so any
// write (INSERT/UPDATE/DELETE/DDL) is rejected by Postgres. Writes are only
// allowed after an admin login (--admin + the admin password), which runs the
// SQL in normal read-write autocommit mode.
//
// NOTE: this is a guardrail against accidental writes, not a hard security
// boundary — the tool authenticates to the DB with the master credentials from
// Secrets Manager, so anyone who can run it and knows the admin password can
// write. Treat the admin password like the site's admin gate: soft protection.

class Program
{
  static async Task<int> Main(string[] args)
  {
    if (args.Any(a => a is "-h" or "--help" or "-?" or "/?" or "?")) { PrintUsage(); return 0; }

    bool admin = false;
    var positional = new List<string>();
    foreach (var a in args)
    {
      if (a is "--admin" or "-a") admin = true;
      else if (a.StartsWith('-')) { Console.Error.WriteLine($"Unknown option: {a}\n"); PrintUsage(); return 1; }
      else positional.Add(a);
    }

    if (positional.Count == 0) { Console.Error.WriteLine("No SQL provided.\n"); PrintUsage(); return 1; }
    var sql = positional[0];

    var config = new ConfigurationBuilder()
      .SetBasePath(AppContext.BaseDirectory)
      .AddJsonFile("appsettings.json")
      .Build();

    var secretArn = config["PostgreSQL:SecretArn"] ?? throw new Exception("PostgreSQL:SecretArn not configured");
    var host      = config["PostgreSQL:Host"]      ?? throw new Exception("PostgreSQL:Host not configured");
    var database  = config["PostgreSQL:Database"]  ?? throw new Exception("PostgreSQL:Database not configured");
    var port      = config["PostgreSQL:Port"] ?? "5432";

    // Admin login gate. Must pass BEFORE we connect so a failed login can't run anything.
    if (admin)
    {
      var expected = config["Admin:PasswordHash"];
      if (string.IsNullOrWhiteSpace(expected))
      {
        Console.Error.WriteLine("Admin:PasswordHash is not configured — cannot use --admin.");
        return 1;
      }
      var supplied = Environment.GetEnvironmentVariable("RUNSQL_ADMIN_PASS") ?? ReadPassword("Admin password: ");
      if (!string.Equals(Sha256Hex(supplied), expected, StringComparison.OrdinalIgnoreCase))
      {
        Console.Error.WriteLine("Incorrect admin password — refusing to run in write mode.");
        return 1;
      }
    }

    var (user, pass) = await GetCreds(secretArn);
    // Short per-attempt timeout + retry so a paused Aurora cluster wakes up
    // instead of failing on the first connect (see aurora-cold-start behaviour).
    var connStr = $"Host={host};Port={port};Database={database};Username={user};Password={pass};SSL Mode=Require;Trust Server Certificate=true;Timeout=30;Command Timeout=180";

    await using var conn = new NpgsqlConnection(connStr);
    await OpenWithRetryAsync(conn);

    Console.WriteLine(admin
      ? "── RunSql [ADMIN / READ-WRITE] ─────────────────────────────"
      : "── RunSql [READ-ONLY] ──────────────────────────────────────");
    Console.WriteLine();

    NpgsqlTransaction? tx = null;
    if (!admin)
    {
      // Explicit read-only transaction. SET TRANSACTION READ ONLY must run before
      // any query, so a later "SET TRANSACTION READ WRITE" in the user's SQL is
      // rejected — the read-only guard can't be flipped off mid-transaction.
      tx = await conn.BeginTransactionAsync();
      await using var ro = new NpgsqlCommand("SET TRANSACTION READ ONLY", conn, tx);
      await ro.ExecuteNonQueryAsync();
    }

    try
    {
      await using var cmd = new NpgsqlCommand(sql, conn, tx);
      await using var r = await cmd.ExecuteReaderAsync();
      do
      {
        if (r.FieldCount == 0)
        {
          Console.WriteLine($"({r.RecordsAffected} row(s) affected)\n");
          continue;
        }
        for (int i = 0; i < r.FieldCount; i++) Console.Write((i > 0 ? " | " : "") + r.GetName(i));
        Console.WriteLine();
        Console.WriteLine(new string('-', 80));
        int rows = 0;
        while (await r.ReadAsync())
        {
          for (int i = 0; i < r.FieldCount; i++)
            Console.Write((i > 0 ? " | " : "") + (r.IsDBNull(i) ? "<null>" : r.GetValue(i)?.ToString()));
          Console.WriteLine();
          rows++;
        }
        Console.WriteLine($"({rows} rows)\n");
      } while (await r.NextResultAsync());
    }
    catch (PostgresException ex) when (ex.SqlState == "25006")
    {
      // 25006 = read_only_sql_transaction
      Console.Error.WriteLine("Refused: this statement writes to the database and RunSql is read-only.");
      Console.Error.WriteLine("Re-run with --admin (and the admin password) to allow writes.");
      return 2;
    }
    finally
    {
      if (tx != null) await tx.RollbackAsync(); // read-only: nothing to commit
    }

    return 0;
  }

  static async Task OpenWithRetryAsync(NpgsqlConnection conn, int maxAttempts = 8, int delaySeconds = 15)
  {
    for (int attempt = 1; ; attempt++)
    {
      try { await conn.OpenAsync(); if (attempt > 1) Console.Error.WriteLine("  ✓ database is awake"); return; }
      catch (Exception ex) when (ex is NpgsqlException or TimeoutException && attempt < maxAttempts)
      {
        Console.Error.WriteLine($"  DB not responding yet (attempt {attempt}/{maxAttempts}) — waking it up, retrying in {delaySeconds}s...");
        await Task.Delay(TimeSpan.FromSeconds(delaySeconds));
      }
    }
  }

  static async Task<(string user, string pass)> GetCreds(string arn)
  {
    using var c = new AmazonSecretsManagerClient(Amazon.RegionEndpoint.USEast1);
    var resp = await c.GetSecretValueAsync(new GetSecretValueRequest { SecretId = arn });
    var j = JObject.Parse(resp.SecretString);
    return (j["username"]?.ToString() ?? throw new Exception("username not in secret"),
            j["password"]?.ToString() ?? throw new Exception("password not in secret"));
  }

  static string Sha256Hex(string s)
  {
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(s));
    var sb = new StringBuilder(bytes.Length * 2);
    foreach (var b in bytes) sb.Append(b.ToString("x2"));
    return sb.ToString();
  }

  // Masked console prompt; falls back to a plain read when input is redirected.
  static string ReadPassword(string prompt)
  {
    Console.Error.Write(prompt);
    var sb = new StringBuilder();
    try
    {
      while (true)
      {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) { Console.Error.WriteLine(); break; }
        if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) { sb.Length--; Console.Error.Write("\b \b"); } }
        else if (!char.IsControl(key.KeyChar)) { sb.Append(key.KeyChar); Console.Error.Write('*'); }
      }
    }
    catch (InvalidOperationException)
    {
      // stdin redirected (non-interactive) — read a line instead.
      return Console.ReadLine() ?? "";
    }
    return sb.ToString();
  }

  static void PrintUsage()
  {
    Console.WriteLine("RunSql — ad-hoc query console for the archive Postgres DB.");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  dotnet run -- \"SELECT ...\"            Read-only (writes are rejected)");
    Console.WriteLine("  dotnet run -- --admin \"UPDATE ...\"    Read-write after admin password");
    Console.WriteLine();
    Console.WriteLine("Multiple statements separated by ';' are supported; each result set is printed.");
    Console.WriteLine("Admin password may be supplied via the RUNSQL_ADMIN_PASS env var (else prompted).");
    Console.WriteLine("Config (appsettings.json): PostgreSQL:SecretArn/Host/Port/Database, Admin:PasswordHash");
  }
}
