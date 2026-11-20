// D1 players 表操作封装与表驱动列映射 (取代 worker.js 里的 if(format) 分支树)

export const PLAYER_COLUMNS = [
  "steam_id", "player_name", "nickname", "elo", "peak_elo", "wins", "losses", "draws", "streak",
  "wins_all_peak", "losses_all_peak", "wins_hardcore", "losses_hardcore", "wins_sprint", "losses_sprint",
  "games_all_peak", "games_hardcore", "games_sprint",
  "favorite_map", "favorite_ascent", "favorite_format"
].join(", ");

// 分赛制战绩列的表驱动映射 (format: 0=全山 1=高难 2=单群系)
const MODE_TABLE = {
  0: { suffix: "all_peak" },
  1: { suffix: "hardcore" },
  2: { suffix: "sprint" }
};

export function modeColumns(format, side) {
  const t = MODE_TABLE[format] || MODE_TABLE[0];
  const s = t.suffix;
  const w = side === "wins" ? 1 : 0;
  const l = side === "losses" ? 1 : 0;
  return `wins_${s} = wins_${s} + ${w},
          losses_${s} = losses_${s} + ${l},
          games_${s} = games_${s} + 1`;
}

// 管理员回滚用的逆操作列
export function modeRollbackColumns(format, side) {
  const t = MODE_TABLE[format] || MODE_TABLE[0];
  const s = t.suffix;
  const w = side === "wins" ? "MAX(0, wins_COL - 1)" : "wins_COL";
  const l = side === "losses" ? "MAX(0, losses_COL - 1)" : "losses_COL";
  return `wins_${s} = ${w.replace("COL", s)},
          losses_${s} = ${l.replace("COL", s)},
          games_${s} = MAX(0, games_${s} - 1)`;
}

export async function getOrCreatePlayer(db, steamId, playerName, seedElo = 1000) {
  let row = await db.prepare("SELECT * FROM players WHERE steam_id = ?").bind(steamId).first();
  if (!row) {
    const initElo = seedElo;
    const now = Date.now();
    await db.prepare(
      `INSERT OR IGNORE INTO players
       (steam_id, player_name, elo, peak_elo, wins, losses, draws, streak,
        wins_all_peak, losses_all_peak, wins_hardcore, losses_hardcore, wins_sprint, losses_sprint,
        games_all_peak, games_hardcore, games_sprint,
        favorite_map, favorite_ascent, favorite_format, updated_at)
       VALUES (?, ?, ?, ?, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, -999, -1, ?)`
    ).bind(steamId, playerName || "Player", initElo, initElo, now).run();
    row = await db.prepare("SELECT * FROM players WHERE steam_id = ?").bind(steamId).first();
  }
  return row;
}
