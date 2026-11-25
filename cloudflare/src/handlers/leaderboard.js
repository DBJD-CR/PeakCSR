// 全服 ELO 排行榜 (带分赛制战绩, 供天梯表格与档案卡使用)
import { jsonResp } from "../lib/http.js";
import { derivePreferences } from "../domain/preferences.js";
import { PLAYER_COLUMNS } from "../db/players.js";

export async function handleLeaderboard(url, env) {
  const limit = Math.min(100, Math.max(1, parseInt(url.searchParams.get("limit") || "20", 10)));
  const { results } = await env.DB.prepare(
    "SELECT " + PLAYER_COLUMNS + " FROM players ORDER BY elo DESC, wins DESC LIMIT ?"
  ).bind(limit).all();

  const rows = results || [];
  // 榜单里每行也带上推导出的最常玩难度/模式: 看别人的档案时,
  // 对方可能从没客户端推过这两列, 但历史对局是齐的
  const entries = await Promise.all(rows.map(async function (row) {
    const derived = await derivePreferences(env.DB, row.steam_id);
    const merged = Object.assign({}, row);
    if (derived.favoriteAscent > -900) merged.favorite_ascent = derived.favoriteAscent;
    if (derived.favoriteFormat >= 0) merged.favorite_format = derived.favoriteFormat;
    return merged;
  }));

  return jsonResp({
    ok: true,
    refreshedAt: Date.now(),
    entries
  }, 200);
}
