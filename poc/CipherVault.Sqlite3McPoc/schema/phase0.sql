PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS documents (
    id INTEGER PRIMARY KEY,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    created_at TEXT NOT NULL
);

CREATE VIRTUAL TABLE IF NOT EXISTS documents_fts USING fts5(
    title,
    content,
    tokenize = 'trigram'
);

CREATE TABLE IF NOT EXISTS crash_probe (
    id INTEGER PRIMARY KEY CHECK (id = 1),
    marker TEXT NOT NULL,
    payload TEXT NOT NULL,
    write_count INTEGER NOT NULL,
    updated_at TEXT NOT NULL
);
