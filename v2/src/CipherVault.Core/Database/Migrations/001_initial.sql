CREATE TABLE IF NOT EXISTS vault_meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
) STRICT;

INSERT INTO vault_meta(key, value) VALUES
    ('vault_id', $vaultId),
    ('created_at', $createdAtUtc);
