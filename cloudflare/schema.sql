-- PeakCSR (PCSR 1v1) Cloudflare D1 数据库初始化脚本
-- 在 Cloudflare D1 控制台的 Console 标签页中直接粘贴执行即可

CREATE TABLE IF NOT EXISTS players (
    steam_id TEXT PRIMARY KEY,
    player_name TEXT NOT NULL,
    elo INTEGER NOT NULL DEFAULT 1000,
    peak_elo INTEGER NOT NULL DEFAULT 1000,
    wins INTEGER NOT NULL DEFAULT 0,
    losses INTEGER NOT NULL DEFAULT 0,
    streak INTEGER NOT NULL DEFAULT 0,
    updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS match_reports (
    invite_code TEXT NOT NULL,
    reporter_steam_id TEXT NOT NULL,
    opponent_steam_id TEXT NOT NULL,
    reporter_name TEXT NOT NULL,
    opponent_name TEXT NOT NULL,
    winner_steam_id TEXT NOT NULL,
    seed INTEGER NOT NULL,
    level_index INTEGER NOT NULL,
    ascent INTEGER NOT NULL,
    format INTEGER NOT NULL,
    duration_ms INTEGER NOT NULL,
    signature TEXT NOT NULL,
    created_at INTEGER NOT NULL,
    PRIMARY KEY (invite_code, reporter_steam_id)
);

CREATE TABLE IF NOT EXISTS settled_matches (
    invite_code TEXT PRIMARY KEY,
    winner_steam_id TEXT NOT NULL,
    loser_steam_id TEXT NOT NULL,
    winner_delta INTEGER NOT NULL,
    loser_delta INTEGER NOT NULL,
    verify_method TEXT NOT NULL,
    settled_at INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_players_elo ON players(elo DESC);
