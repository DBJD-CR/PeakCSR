// 历史战绩 (按 match_key 去重)
import { jsonResp } from "../lib/http.js";

export async function handleHistory(url, env) {
  const steamId = (url.searchParams.get("steamId") || "").trim();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400);
  const limit = Math.min(50, Math.max(1, parseInt(url.searchParams.get("limit") || "20", 10)));

  // LEFT JOIN 双方档案取昵称/Steam 名: 战报回看页必须显示对手是谁。
  // 用 LEFT JOIN 而不是 JOIN 是因为对手可能因为清理而没留下 players 行。
  const { results } = await env.DB.prepare(
    `SELECT s.match_key, s.invite_code, s.seed, s.winner_steam_id, s.loser_steam_id,
            s.winner_delta, s.loser_delta, s.win_elo_before, s.win_elo_after,
            s.lose_elo_before, s.lose_elo_after,
            s.level_index, s.ascent, s.format, s.route, s.duration_ms,
            s.winner_progress, s.loser_progress, s.verify_method, s.is_void, s.settled_at,
            wp.nickname AS winner_nickname, wp.player_name AS winner_name,
            lp.nickname AS loser_nickname, lp.player_name AS loser_name
     FROM settled_matches s
     LEFT JOIN players wp ON wp.steam_id = s.winner_steam_id
     LEFT JOIN players lp ON lp.steam_id = s.loser_steam_id
     WHERE s.winner_steam_id = ? OR s.loser_steam_id = ?
     ORDER BY s.settled_at DESC LIMIT ?`
  ).bind(steamId, steamId, limit).all();

  const history = (results || []).map(function (r) {
    const isWinner = r.winner_steam_id === steamId;
    const isVoid = !!r.is_void;
    // 对手展示名: 优先 PCSR 昵称, 回退 Steam 名
    const oppNick = isWinner ? r.loser_nickname : r.winner_nickname;
    const oppName = isWinner ? r.loser_name : r.winner_name;
    const opponentName = (oppNick && String(oppNick).trim()) || String(oppName || "").trim();
    return {
      matchKey: r.match_key,
      inviteCode: r.invite_code,
      seed: r.seed,
      // 作废局显式 won=false
      won: isVoid ? false : isWinner,
      isVoid: isVoid,
      opponentSteamId: isWinner ? r.loser_steam_id : r.winner_steam_id,
      opponentName: opponentName,
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
