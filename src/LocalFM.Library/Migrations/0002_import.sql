-- Importer (phase 10): jobs that pull music from integrations into the import folder, and the
-- metadata they knew about each file, applied when the scanner first sees it.

CREATE TABLE import_job (
    id           TEXT PRIMARY KEY,
    provider     TEXT NOT NULL,          -- 'ytdlp', 'archive'
    input        TEXT NOT NULL,          -- URL, or the path of a zip/folder/file
    external_id  TEXT,                   -- e.g. the YouTube video id
    title        TEXT NOT NULL,
    artist       TEXT,
    album        TEXT,
    album_artist TEXT,
    track_no     INTEGER,
    date         TEXT,
    thumbnail    TEXT,                   -- URL of a cover candidate
    status       TEXT NOT NULL,          -- queued, running, done, failed, cancelled
    progress     REAL NOT NULL DEFAULT 0,
    error        TEXT,
    file_path    TEXT,                   -- where the file landed
    created_at   INTEGER NOT NULL,
    updated_at   INTEGER NOT NULL
);
CREATE INDEX ix_import_job_status ON import_job(status, created_at);

-- Tags a file should have had. Only fills what the file's own tags leave empty.
CREATE TABLE import_hint (
    path         TEXT PRIMARY KEY,
    provider     TEXT NOT NULL,
    external_id  TEXT,
    url          TEXT,
    title        TEXT,
    artist       TEXT,
    album_artist TEXT,
    album        TEXT,
    date         TEXT,
    track_no     INTEGER,
    created_at   INTEGER NOT NULL
);
