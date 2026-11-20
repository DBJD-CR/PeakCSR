// 管理员后门: 人工处理恶意拔线 / 误判纠正
// 需要请求头 X-Admin-Token 与 env.ADMIN_TOKEN 一致
import { jsonResp, matchKey } from "../lib/http.js";
import { modeRollbackColumns } from "../db/players.js";
import rules from "../../../shared/rating-rules.json";

// 回滚时的分数下限直接取规则值, 避免在多处硬编码
function rulesFloor() {
  return Number(rules.elo.clampMin) || 100;
}

export async function handleAdmin(request, body, env) {
  const expected = env && env.ADMIN_TOKEN;
  if (!expected) {
    return jsonResp({ ok: false, error: "Admin API disabled (ADMIN_TOKEN not configured)" }, 403);
  }
  const provided = request.headers.get("X-Admin-Token") || String(body.token || "");
  if (provided !== expected) {
    return jsonResp({ ok: false, error: "Forbidden" }, 403);
  }

  const action = String(body.action || "").trim();
  const note = String(body.note || "").trim().slice(0, 200);
  const now = Date.now();

  if (action === "void") {
    // 作废某场对局并回滚双方分数
    const key = String(body.matchKey || "").trim() ||
      matchKey(String(body.inviteCode || "").trim().toUpperCase(), parseInt(body.seed || "0", 10));
    const row = await env.DB.prepare("SELECT * FROM settled_matches WHERE match_key = ?").bind(key).first();
    if (!row) return jsonResp({ ok: false, error: "Match not found" }, 404);
    if (row.is_void) return jsonResp({ ok: true, status: "already_void" }, 200);

    const winMode = modeRollbackColumns(row.format, "wins");
    const loseMode = modeRollbackColumns(row.format, "losses");
    const winGain = Number(row.winner_delta) || 0;
    const loseGain = Number(row.loser_delta) || 0;

    // 回滚 + 打作废标记同批次
    // 顺序: 先把两条有守卫的回滚做掉, 最后才写标记
    // 守卫条件引用 is_void = 0, 所以重复请求时回滚整段变成 no-op, 天然幂等
    const guardSql = "EXISTS (SELECT 1 FROM settled_matches WHERE match_key = ? AND is_void = 0)";
    const results = await env.DB.batch([
      env.DB.prepare(
        `UPDATE players SET elo = MAX(${rulesFloor()}, elo - ?),
          wins = MAX(0, wins - 1), streak = CASE WHEN streak > 0 THEN streak - 1 ELSE 0 END,
          ${winMode}, updated_at = ? WHERE steam_id = ? AND ${guardSql}`
      ).bind(winGain, now, row.winner_steam_id, key),
      env.DB.prepare(
        `UPDATE players SET elo = MAX(${rulesFloor()}, elo - ?),
          losses = MAX(0, losses - 1), streak = CASE WHEN streak < 0 THEN streak + 1 ELSE 0 END,
          ${loseMode}, updated_at = ? WHERE steam_id = ? AND ${guardSql}`
      ).bind(loseGain, now, row.loser_steam_id, key),
      env.DB.prepare(
        "UPDATE settled_matches SET is_void = 1 WHERE match_key = ? AND is_void = 0"
      ).bind(key)
    ]);

    // 最后一条的 changes 就是"本次是否真的抢到了作废权"
    const marker = results && results[2];
    const claimed = !!(marker && marker.meta && marker.meta.changes > 0);
    if (!claimed) {
      return jsonResp({ ok: true, status: "already_void" }, 200);
    }

    // 历史里保留原始增减 (审计可追溯), 作废通过 is_void 判定, 不再清零 delta

    await audit(env, action, JSON.stringify({ key, inviteCode: row.invite_code, seed: row.seed }), note, now);
    return jsonResp({ ok: true, status: "voided", matchKey: key }, 200);
  }

  if (action === "recent") {
    // 列出最近结算的对局, 便于找到要 void 的 match_key
    const limit = Math.min(50, Math.max(1, parseInt(body.limit || "20", 10)));
    const { results } = await env.DB.prepare(
      `SELECT match_key, invite_code, seed, winner_steam_id, loser_steam_id,
              winner_delta, loser_delta, level_index, ascent, format,
              verify_method, is_void, settled_at
       FROM settled_matches ORDER BY settled_at DESC LIMIT ?`
    ).bind(limit).all();

    const rows = (results || []).map(function (r) {
      return {
        matchKey: r.match_key,
        inviteCode: r.invite_code,
        seed: r.seed,
        winner: r.winner_steam_id,
        loser: r.loser_steam_id,
        winnerDelta: r.winner_delta,
        loserDelta: r.loser_delta,
        levelIndex: r.level_index,
        ascent: r.ascent,
        format: r.format,
        verifyMethod: r.verify_method,
        isVoid: !!r.is_void,
        settledAt: r.settled_at
      };
    });
    return jsonResp({ ok: true, count: rows.length, matches: rows }, 200);
  }

  if (action === "set-elo") {
    const steamId = String(body.steamId || "").trim();
    const elo = parseInt(body.elo, 10);
    if (!steamId || isNaN(elo)) {
      return jsonResp({ ok: false, error: "Missing steamId or elo" }, 400);
    }
    const clamped = Math.max(100, Math.min(5000, elo));
    const p = await env.DB.prepare("SELECT * FROM players WHERE steam_id = ?").bind(steamId).first();
    if (!p) return jsonResp({ ok: false, error: "Player not found" }, 404);

    await env.DB.prepare(
      "UPDATE players SET elo = ?, peak_elo = MAX(peak_elo, ?), updated_at = ? WHERE steam_id = ?"
    ).bind(clamped, clamped, now, steamId).run();

    await audit(env, action, JSON.stringify({ steamId, before: p.elo, after: clamped }), note, now);
    return jsonResp({ ok: true, status: "updated", before: p.elo, after: clamped }, 200);
  }

  if (action === "reset" || action === "wipe") {
    // 测试期清库: reset = 清空对局与战绩, wipe = 连同玩家档案一起清空
    if (action === "wipe") {
      await env.DB.batch([
        env.DB.prepare("DELETE FROM settled_matches"),
        env.DB.prepare("DELETE FROM match_reports"),
        env.DB.prepare("DELETE FROM players")
      ]);
    } else {
      await env.DB.batch([
        env.DB.prepare("DELETE FROM settled_matches"),
        env.DB.prepare("DELETE FROM match_reports"),
        env.DB.prepare(
          `UPDATE players SET wins = 0, losses = 0, draws = 0, streak = 0,
            wins_all_peak = 0, losses_all_peak = 0, wins_hardcore = 0, losses_hardcore = 0,
            wins_sprint = 0, losses_sprint = 0, games_all_peak = 0, games_hardcore = 0, games_sprint = 0,
            updated_at = ?`
        ).bind(now)
      ]);
    }
    await audit(env, action, "{}", note, now);
    return jsonResp({ ok: true, status: action }, 200);
  }

  return jsonResp({ ok: false, error: "Unknown action: " + action }, 400);
}

async function audit(env, action, payload, note, now) {
  try {
    await env.DB.prepare(
      "INSERT INTO admin_audit (action, payload, note, created_at) VALUES (?, ?, ?, ?)"
    ).bind(action, payload, note || "", now).run();
  } catch (e) {
    // 留痕失败不影响主流程
  }
}
