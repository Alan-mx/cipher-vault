# Unsupported Schema 5 Backup Fixture

This directory contains a real encrypted Schema 5 / FormatVersion 1
`.cvbackup` from an unpublished development build. CipherVault v1.3 does not
support restoring backups from older database schemas, so this fixture is used
to prove that unsupported schemas are rejected before a target is published.

## Source

| Field | Value |
|---|---|
| Software version | `2.0.0-rc.1` |
| Git commit | `c83b3cf3b7c085c9601e823a3e6b89c579b6c16d` |
| Database Schema | `5` |
| Backup FormatVersion | `1` |
| Fixture file | `unsupported-schema-5-format-1.cvbackup` |
| Fixture bytes | `193452` |
| Fixture SHA256 | `87c81f63ba68cb4836b87d4c59217b49934a1d3e639900d0fde46e91f8a4e29a` |
| Test password | `correct horse battery staple` |

The fixture contains only synthetic test data. It does not contain user data,
real passwords, MCP tokens, or raw keys. It is retained only as a negative
restore test.

## Test Content

The backup contains one Markdown document:

```text
Title: Compatibility Fixture
Path: fixtures/compatibility-fixture.md
Revision: 1
Tags: compatibility, fixture
```

The exact document content is covered by `contentSha256` in
`unsupported-schema-5-format-1.json`.

## Regeneration Rule

Do not regenerate this fixture from the current working tree. It must remain
frozen so the restore command continues to prove that pre-v1.3 backups are
rejected without leaving a target or partial restore behind.
