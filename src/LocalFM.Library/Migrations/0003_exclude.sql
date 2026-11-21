-- Folders inside sources that the scanner skips (e.g. another app's recordings under ~/Music).
CREATE TABLE source_exclude (
    path      TEXT PRIMARY KEY,
    added_at  INTEGER NOT NULL
);
