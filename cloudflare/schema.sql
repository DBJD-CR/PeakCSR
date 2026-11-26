CREATE TABLE IF NOT EXISTS players (steam_id TEXT PRIMARY KEY, player_name TEXT NOT NULL, nickname TEXT NOT NULL DEFAULT '', nickname_updated_at INTEGER NOT NULL DEFAULT 0, elo INTEGER NOT NULL DEFAULT 1000, peak_elo INTEGER NOT NULL DEFAULT 1000, wins INTEGER NOT NULL DEFAULT 0, losses INTEGER NOT NULL DEFAULT 0, draws INTEGER NOT NULL DEFAULT 0, streak INTEGER NOT NULL DEFAULT 0, best_streak INTEGER NOT NULL DEFAULT 0, wins_all_peak INTEGER NOT NULL DEFAULT 0, losses_all_peak INTEGER NOT NULL DEFAULT 0, wins_hardcore INTEGER NOT NULL DEFAULT 0, losses_hardcore INTEGER NOT NULL DEFAULT 0, wins_sprint INTEGER NOT NULL DEFAULT 0, losses_sprint INTEGER NOT NULL DEFAULT 0, games_all_peak INTEGER NOT NULL DEFAULT 0, games_hardcore INTEGER NOT NULL DEFAULT 0, games_sprint INTEGER NOT NULL DEFAULT 0, favorite_map INTEGER NOT NULL DEFAULT -1, disliked_map INTEGER NOT NULL DEFAULT -1, favorite_ascent INTEGER NOT NULL DEFAULT -999, favorite_format INTEGER NOT NULL DEFAULT -1, updated_at INTEGER NOT NULL);
CREATE TABLE IF NOT EXISTS match_reports (invite_code TEXT NOT NULL, reporter_steam_id TEXT NOT NULL, opponent_steam_id TEXT NOT NULL, reporter_name TEXT NOT NULL, opponent_name TEXT NOT NULL, winner_steam_id TEXT NOT NULL, seed INTEGER NOT NULL, level_index INTEGER NOT NULL, ascent INTEGER NOT NULL, format INTEGER NOT NULL, route INTEGER NOT NULL DEFAULT 0, duration_ms INTEGER NOT NULL, signature TEXT NOT NULL, reporter_progress REAL NOT NULL DEFAULT 0, opponent_progress REAL NOT NULL DEFAULT 0, reporter_splits TEXT NOT NULL DEFAULT '', is_void INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL, PRIMARY KEY (invite_code, reporter_steam_id, seed));
CREATE TABLE IF NOT EXISTS settled_matches (match_key TEXT PRIMARY KEY, invite_code TEXT NOT NULL, seed INTEGER NOT NULL, winner_steam_id TEXT NOT NULL, loser_steam_id TEXT NOT NULL, winner_delta INTEGER NOT NULL, loser_delta INTEGER NOT NULL, win_elo_before INTEGER NOT NULL DEFAULT 0, win_elo_after INTEGER NOT NULL DEFAULT 0, lose_elo_before INTEGER NOT NULL DEFAULT 0, lose_elo_after INTEGER NOT NULL DEFAULT 0, level_index INTEGER NOT NULL DEFAULT 0, ascent INTEGER NOT NULL DEFAULT 0, format INTEGER NOT NULL DEFAULT 0, route INTEGER NOT NULL DEFAULT 0, duration_ms INTEGER NOT NULL DEFAULT 0, winner_progress REAL NOT NULL DEFAULT 0, loser_progress REAL NOT NULL DEFAULT 0, verify_method TEXT NOT NULL, is_void INTEGER NOT NULL DEFAULT 0, settled_at INTEGER NOT NULL, is_private_room INTEGER NOT NULL DEFAULT 0);
CREATE TABLE IF NOT EXISTS admin_audit (id INTEGER PRIMARY KEY AUTOINCREMENT, action TEXT NOT NULL, payload TEXT NOT NULL, note TEXT NOT NULL DEFAULT '', created_at INTEGER NOT NULL);
CREATE INDEX IF NOT EXISTS idx_players_elo ON players(elo DESC);
CREATE UNIQUE INDEX IF NOT EXISTS idx_players_nickname ON players(nickname COLLATE NOCASE) WHERE nickname <> '';
CREATE INDEX IF NOT EXISTS idx_settled_winner ON settled_matches(winner_steam_id, settled_at DESC);
CREATE INDEX IF NOT EXISTS idx_settled_loser ON settled_matches(loser_steam_id, settled_at DESC);
CREATE INDEX IF NOT EXISTS idx_settled_invite ON settled_matches(invite_code);
CREATE INDEX IF NOT EXISTS idx_settled_verify ON settled_matches(verify_method, settled_at DESC);
CREATE INDEX IF NOT EXISTS idx_reports_key ON match_reports(invite_code, seed, created_at);

CREATE TABLE IF NOT EXISTS player_pbs (steam_id TEXT NOT NULL, category INTEGER NOT NULL, sub_key INTEGER NOT NULL, time_ms INTEGER NOT NULL, level_index INTEGER NOT NULL DEFAULT 0, seed INTEGER NOT NULL DEFAULT 0, ascent INTEGER NOT NULL DEFAULT 0, format INTEGER NOT NULL DEFAULT 0, achieved_at INTEGER NOT NULL, PRIMARY KEY (steam_id, category, sub_key));
CREATE INDEX IF NOT EXISTS idx_pbs_steam ON player_pbs(steam_id);

CREATE TABLE IF NOT EXISTS match_splits (match_key TEXT PRIMARY KEY, winner_splits TEXT NOT NULL DEFAULT '[]', loser_splits TEXT NOT NULL DEFAULT '[]', version INTEGER NOT NULL DEFAULT 1, updated_at INTEGER NOT NULL DEFAULT 0);

-- 旧库升级: players 补"国籍"列
-- ALTER TABLE players ADD COLUMN country TEXT NOT NULL DEFAULT '';

-- 旧库升级: players 补"历史最长连胜"列
-- ALTER TABLE players ADD COLUMN best_streak INTEGER NOT NULL DEFAULT 0;
-- 旧库升级: 已存在的 settled_matches 补列 (SQLite 不支持 IF NOT EXISTS 加列, 重复执行会报错, 可忽略)
-- ALTER TABLE settled_matches ADD COLUMN is_private_room INTEGER NOT NULL DEFAULT 0;
-- 旧库升级: players 补"最讨厌群系"列、match_reports 补"上报方分段"列
-- ALTER TABLE players ADD COLUMN disliked_map INTEGER NOT NULL DEFAULT -1;
-- ALTER TABLE match_reports ADD COLUMN reporter_splits TEXT NOT NULL DEFAULT '';
