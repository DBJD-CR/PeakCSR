// 全服 ELO 排行榜 (带分赛制战绩, 供天梯表格与档案卡使用)
import { jsonResp } from "../lib/http.js";
import { PLAYER_COLUMNS } from "../db/players.js";

export async function handleLeaderboard(url, env) {
  const limit = Math.min(100, Math.max(1, parseInt(url.searchParams.get("limit") || "20", 10)));
  const { results } = await env.DB.prepare(
    "SELECT " + PLAYER_COLUMNS + " FROM players ORDER BY elo DESC, wins DESC LIMIT ?"
  ).bind(limit).all();

  return jsonResp({
    ok: true,
    refreshedAt: Date.now(),
    entries: results || []
  }, 200);
}
