-- LocalFM knowledge base, schema v1.
--
-- Modelled after MusicBrainz so external data maps 1:1:
--   artist ── artist_credit (ordered names + join phrases, e.g. "A feat. B")
--   work (composition) ← recording (a performance) ← track (a recording's appearance on a medium)
--   release_group ("the album") ← release (an edition: remaster, deluxe, country…) ← medium (disc) ← track
--   audio_file (a physical file on disk) → track
-- Every entity id is a ULID (TEXT) so libraries can be exported and merged.
-- Times are unix milliseconds (INTEGER) unless named *_date (partial ISO dates: '1997', '1997-05', '1997-05-21').
-- entity_type values: 'artist','work','recording','release_group','release','medium','track','audio_file','playlist'.

CREATE TABLE source (
    id            TEXT PRIMARY KEY,
    kind          TEXT NOT NULL DEFAULT 'folder',
    path          TEXT NOT NULL UNIQUE,
    enabled       INTEGER NOT NULL DEFAULT 1,
    added_at      INTEGER NOT NULL,
    last_scan_at  INTEGER
);

CREATE TABLE artist (
    id              TEXT PRIMARY KEY,
    name            TEXT NOT NULL,
    sort_name       TEXT,
    type            TEXT,           -- person, group, orchestra, choir, character, other
    disambiguation  TEXT,
    country         TEXT,
    begin_date      TEXT,
    end_date        TEXT,
    name_key        TEXT NOT NULL,  -- normalized name for matching
    created_at      INTEGER NOT NULL,
    updated_at      INTEGER NOT NULL
);
CREATE INDEX ix_artist_name_key ON artist(name_key);

CREATE TABLE artist_credit (
    id            TEXT PRIMARY KEY,
    display_name  TEXT NOT NULL,
    credit_key    TEXT NOT NULL UNIQUE  -- canonical "artist_id|credited|join;..." for dedup
);

CREATE TABLE artist_credit_name (
    credit_id      TEXT NOT NULL REFERENCES artist_credit(id) ON DELETE CASCADE,
    position       INTEGER NOT NULL,
    artist_id      TEXT NOT NULL REFERENCES artist(id),
    credited_name  TEXT NOT NULL,
    join_phrase    TEXT NOT NULL DEFAULT '',
    PRIMARY KEY (credit_id, position)
);
CREATE INDEX ix_acn_artist ON artist_credit_name(artist_id);

CREATE TABLE work (
    id              TEXT PRIMARY KEY,
    title           TEXT NOT NULL,
    disambiguation  TEXT,
    created_at      INTEGER NOT NULL,
    updated_at      INTEGER NOT NULL
);

CREATE TABLE recording (
    id               TEXT PRIMARY KEY,
    title            TEXT NOT NULL,
    artist_credit_id TEXT REFERENCES artist_credit(id),
    length_ms        INTEGER,
    disambiguation   TEXT,
    created_at       INTEGER NOT NULL,
    updated_at       INTEGER NOT NULL
);
CREATE INDEX ix_recording_credit ON recording(artist_credit_id);

CREATE TABLE recording_work (
    recording_id TEXT NOT NULL REFERENCES recording(id) ON DELETE CASCADE,
    work_id      TEXT NOT NULL REFERENCES work(id) ON DELETE CASCADE,
    PRIMARY KEY (recording_id, work_id)
);

CREATE TABLE release_group (
    id                  TEXT PRIMARY KEY,
    title               TEXT NOT NULL,
    artist_credit_id    TEXT REFERENCES artist_credit(id),
    primary_type        TEXT,   -- album, single, ep, broadcast, other
    secondary_types     TEXT,   -- comma separated: compilation, live, soundtrack, remix…
    first_release_date  TEXT,
    title_key           TEXT NOT NULL,
    created_at          INTEGER NOT NULL,
    updated_at          INTEGER NOT NULL
);
CREATE INDEX ix_rg_title_key ON release_group(title_key);

CREATE TABLE release (
    id                TEXT PRIMARY KEY,
    release_group_id  TEXT NOT NULL REFERENCES release_group(id),
    title             TEXT NOT NULL,
    artist_credit_id  TEXT REFERENCES artist_credit(id),
    date              TEXT,
    country           TEXT,
    label             TEXT,
    catalog_number    TEXT,
    barcode           TEXT,
    status            TEXT,   -- official, promotion, bootleg
    edition           TEXT,   -- free text: "2011 Remaster", "Deluxe Edition"
    title_key         TEXT NOT NULL,
    created_at        INTEGER NOT NULL,
    updated_at        INTEGER NOT NULL
);
CREATE INDEX ix_release_rg ON release(release_group_id);
CREATE INDEX ix_release_title_key ON release(title_key);

CREATE TABLE medium (
    id          TEXT PRIMARY KEY,
    release_id  TEXT NOT NULL REFERENCES release(id) ON DELETE CASCADE,
    position    INTEGER NOT NULL,
    format      TEXT,
    title       TEXT,
    track_count INTEGER,
    UNIQUE (release_id, position)
);

CREATE TABLE track (
    id                TEXT PRIMARY KEY,
    medium_id         TEXT NOT NULL REFERENCES medium(id) ON DELETE CASCADE,
    recording_id      TEXT NOT NULL REFERENCES recording(id),
    position          INTEGER NOT NULL,
    number            TEXT,     -- as printed: "A1", "3"
    title             TEXT NOT NULL,
    artist_credit_id  TEXT REFERENCES artist_credit(id),
    length_ms         INTEGER,
    created_at        INTEGER NOT NULL,
    updated_at        INTEGER NOT NULL
);
CREATE INDEX ix_track_medium ON track(medium_id, position);
CREATE INDEX ix_track_recording ON track(recording_id);

CREATE TABLE audio_file (
    id               TEXT PRIMARY KEY,
    source_id        TEXT REFERENCES source(id) ON DELETE SET NULL,
    path             TEXT NOT NULL UNIQUE,
    size             INTEGER NOT NULL,
    mtime            INTEGER NOT NULL,
    audio_hash       TEXT NOT NULL,     -- xxh128 of audio packet payloads: survives tag edits and moves
    tags_hash        TEXT,              -- detects external re-tagging
    codec            TEXT,
    container        TEXT,
    duration_ms      INTEGER,
    bit_rate         INTEGER,
    sample_rate      INTEGER,
    channels         INTEGER,
    bits_per_sample  INTEGER,
    track_id         TEXT REFERENCES track(id) ON DELETE SET NULL,
    rg_track_gain    REAL,
    rg_track_peak    REAL,
    rg_album_gain    REAL,
    rg_album_peak    REAL,
    missing          INTEGER NOT NULL DEFAULT 0,
    added_at         INTEGER NOT NULL,
    updated_at       INTEGER NOT NULL
);
CREATE INDEX ix_file_hash ON audio_file(audio_hash);
CREATE INDEX ix_file_track ON audio_file(track_id);
CREATE INDEX ix_file_source ON audio_file(source_id);

CREATE TABLE genre (
    id    TEXT PRIMARY KEY,
    name  TEXT NOT NULL,
    name_key TEXT NOT NULL UNIQUE
);

CREATE TABLE entity_genre (
    entity_type TEXT NOT NULL,
    entity_id   TEXT NOT NULL,
    genre_id    TEXT NOT NULL REFERENCES genre(id) ON DELETE CASCADE,
    source      TEXT NOT NULL DEFAULT 'tags',
    PRIMARY KEY (entity_type, entity_id, genre_id)
);
CREATE INDEX ix_entity_genre_genre ON entity_genre(genre_id);

-- Content-addressed image store; files live under <data>/images/<hash[0..2]>/<hash>.<ext>
CREATE TABLE image (
    id      TEXT PRIMARY KEY,
    hash    TEXT NOT NULL UNIQUE,
    mime    TEXT NOT NULL,
    width   INTEGER,
    height  INTEGER,
    rel_path TEXT NOT NULL
);

CREATE TABLE entity_image (
    entity_type TEXT NOT NULL,
    entity_id   TEXT NOT NULL,
    image_id    TEXT NOT NULL REFERENCES image(id) ON DELETE CASCADE,
    role        TEXT NOT NULL,     -- front, back, medium, booklet, artist, logo…
    source      TEXT NOT NULL,     -- embedded, folder, coverartarchive, user…
    PRIMARY KEY (entity_type, entity_id, image_id, role)
);

-- Identifiers in other systems (musicbrainz, discogs, itunes, wikidata, isrc, acoustid…)
CREATE TABLE external_id (
    entity_type TEXT NOT NULL,
    entity_id   TEXT NOT NULL,
    provider    TEXT NOT NULL,
    external_id TEXT NOT NULL,
    PRIMARY KEY (entity_type, entity_id, provider, external_id)
);
CREATE INDEX ix_external_lookup ON external_id(provider, external_id, entity_type);

-- Open-ended facts (bpm, key, mood, chords ref, wikipedia url…) without schema changes.
CREATE TABLE attribute (
    entity_type TEXT NOT NULL,
    entity_id   TEXT NOT NULL,
    key         TEXT NOT NULL,
    value       TEXT NOT NULL,
    source      TEXT NOT NULL,
    updated_at  INTEGER NOT NULL,
    PRIMARY KEY (entity_type, entity_id, key, source)
);

-- Where a field's current value came from. locked = 1 means a human set it: automated imports and
-- metadata fetches never overwrite it.
CREATE TABLE field_provenance (
    entity_type TEXT NOT NULL,
    entity_id   TEXT NOT NULL,
    field       TEXT NOT NULL,
    source      TEXT NOT NULL,
    updated_at  INTEGER NOT NULL,
    locked      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (entity_type, entity_id, field)
);

CREATE TABLE lyrics (
    id            TEXT PRIMARY KEY,
    recording_id  TEXT NOT NULL REFERENCES recording(id) ON DELETE CASCADE,
    format        TEXT NOT NULL,   -- plain, lrc (line-synced), elrc (word-synced)
    language      TEXT,
    content       TEXT NOT NULL,
    source        TEXT NOT NULL,
    updated_at    INTEGER NOT NULL
);
CREATE INDEX ix_lyrics_recording ON lyrics(recording_id);

CREATE TABLE playlist (
    id          TEXT PRIMARY KEY,
    name        TEXT NOT NULL,
    description TEXT,
    created_at  INTEGER NOT NULL,
    updated_at  INTEGER NOT NULL
);

-- Entries point at recordings (stable across file moves and re-rips); the file is a preference.
CREATE TABLE playlist_entry (
    id             TEXT PRIMARY KEY,
    playlist_id    TEXT NOT NULL REFERENCES playlist(id) ON DELETE CASCADE,
    position       INTEGER NOT NULL,
    recording_id   TEXT NOT NULL REFERENCES recording(id),
    audio_file_id  TEXT REFERENCES audio_file(id) ON DELETE SET NULL,
    added_at       INTEGER NOT NULL
);
CREATE INDEX ix_playlist_entry ON playlist_entry(playlist_id, position);

-- One row per listen. All statistics are derived from this table.
CREATE TABLE play_event (
    id             TEXT PRIMARY KEY,
    recording_id   TEXT REFERENCES recording(id) ON DELETE SET NULL,
    audio_file_id  TEXT REFERENCES audio_file(id) ON DELETE SET NULL,
    started_at     INTEGER NOT NULL,
    ms_listened    INTEGER NOT NULL,
    completed      INTEGER NOT NULL
);
CREATE INDEX ix_play_event_recording ON play_event(recording_id);
CREATE INDEX ix_play_event_started ON play_event(started_at);

-- Small key/value store: settings, saved queue, resume position.
CREATE TABLE kv (
    key    TEXT PRIMARY KEY,
    value  TEXT NOT NULL
);

-- Raw responses from metadata providers, so we never ask a free service twice for the same thing.
CREATE TABLE http_cache (
    provider    TEXT NOT NULL,
    request_key TEXT NOT NULL,
    status      INTEGER NOT NULL,
    body        BLOB,
    fetched_at  INTEGER NOT NULL,
    expires_at  INTEGER,
    PRIMARY KEY (provider, request_key)
);
