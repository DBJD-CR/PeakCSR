// 历史战绩 (按 match_key 去重)
import { jsonResp } from "../lib/http.js";

export async function handleHistory(url, env) {
  const steamId = (url.searchParams.get("steamId") || "").trim();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400);
  const limit = Math.min(50, Math.max(1, parseInt(url.searchParams.get("limit") || "20", 10)));

  const { results } = await env.DB.prepare(
    `SELECT match_key, invite_code, seed, winner_steam_id, loser_steam_id,
            winner_delta, loser_delta, win_elo_before, win_elo_after,
            lose_elo_before, lose_elo_after,
            level_index, ascent, format, route, duration_ms,
            winner_progress, loser_progress, verify_method, is_void, settled_at
     FROM settled_matches
     WHERE winner_steam_id = ? OR loser_steam_id = ?
     ORDER BY settled_at DESC LIMIT ?`
  ).bind(steamId, steamId, limit).all();

  const history = (results || []).map(function (r) {
    const isWinner = r.winner_steam_id === steamId;
    const isVoid = !!r.is_void;
    return {
      matchKey: r.match_key,
      inviteCode: r.invite_code,
      seed: r.seed,
      // 作废局显式 won=false
      won: isVoid ? false : isWinner,
      isVoid: isVoid,
      opponentSteamId: isWinner ? r.loser_steam_id : r.winner_steam_id,
      delta: isWinner ? r.winner_delta : r.loser_delta,
      eloBefore: isWinner ? r.win_elo_before : r.lose_elo_before,
      eloAfter: isWinner ? r.win_elo_after : r.lose_elo_after,
      levelIndex: r.level_index,
      ascent: r.ascent,
      format: r.format,
      route: r.route || 0,
      durationMs: r.duration_ms,
      myProgress: isWinner ? r.winner_progress : r.loser_progress,
      oppProgress: isWinner ? r.loser_progress : r.winner_progress,
      verifyMethod: r.verify_method,
      settledAt: r.settled_at
    };
  });

  return jsonResp({ ok: true, history }, 200);
}
