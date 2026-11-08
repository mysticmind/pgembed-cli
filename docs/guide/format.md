# Format and Check

## Format

snippet: sample_format

Formatting uses the PostgreSQL parser and keeps comments. It needs no server. Every statement ends with a
semicolon. `--check` makes it a CI lint: it lists the files that are not formatted and exits 1.

```sql
SELECT id, title
FROM books
WHERE id = 1;
```

## Check

`sql --check` checks the syntax offline and summarizes each statement: its kind, the tables it reads,
writes or defines, the functions it calls, and whether it would ask before running. See
[SQL: check without running](./sql#check-without-running).
