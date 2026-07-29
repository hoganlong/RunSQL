# RunSql

Ad-hoc query console for the Keith Long archive PostgreSQL database. Connects with
the RDS master credentials from AWS Secrets Manager and prints result sets for any
SQL you pass on the command line.

## Safety model

- **Read-only by default.** Queries run inside an explicit `READ ONLY` transaction,
  so any `INSERT`/`UPDATE`/`DELETE`/DDL is rejected by Postgres (error `25006`).
  The read-only guard is set before your SQL runs and can't be flipped off
  mid-transaction.
- **Writes require admin login.** Pass `--admin` and the admin password; the SQL
  then runs in normal read-write autocommit mode.

This is a guardrail against *accidental* writes, not a hard security boundary — the
tool holds the master DB credentials, so anyone who can run it and knows the admin
password can write. Treat the admin password like the site's admin gate.

## Usage

```
dotnet run -- "SELECT * FROM show ORDER BY id_field DESC LIMIT 10"
dotnet run -- --admin "UPDATE artlist_item SET item_type='ART_IN_SHOW' WHERE id_field=123"
```

- Multiple statements separated by `;` are supported; each result set prints in turn.
- The admin password can be supplied non-interactively via the `RUNSQL_ADMIN_PASS`
  environment variable; otherwise you're prompted (masked).
- Handles Aurora cold-start: retries the first connect while a paused cluster wakes.

## Config

Copy `appsettings.template.json` to `appsettings.json` and fill in:

- `PostgreSQL:SecretArn` / `Host` / `Port` / `Database`
- `Admin:PasswordHash` — SHA-256 (lowercase hex) of the admin password

`appsettings.json` is gitignored.
