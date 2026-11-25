// 管理员后门: 人工处理恶意拔线 / 误判纠正
// 需要请求头 X-Admin-Token 与 env.ADMIN_TOKEN 一致
import { jsonResp, matchKey } from "../lib/http.js";
import { modeRollbackColumns } from "../db/players.js";
import rules from "../../../shared/rating-rules.json";

// 与 handlers/pbs.js 保持一致的 PB 物理下限, 避免两处漂移
const MIN_ALLPEAK_MS = 5 * 60 * 1000;
const MIN_ASCENT_MS = 2 * 60 * 1000;

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
      ).bind(key),
      // 作废局的分段一并清掉: 否则会出现"已作废但战报还能点开"的鬼数据
      env.DB.prepare("DELETE FROM match_splits WHERE match_key = ?").bind(key)
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
    //
    // verifyMethod 可选过滤: 传 "unverified" 之类可快速筛出无佐证结算的局
    const limit = Math.min(50, Math.max(1, parseInt(body.limit || "20", 10)));
    const verifyMethod = String(body.verifyMethod || "").trim();
    const sql = verifyMethod
      ? `SELECT match_key, invite_code, seed, winner_steam_id, loser_steam_id,
                winner_delta, loser_delta, level_index, ascent, format,
                verify_method, is_void, settled_at
         FROM settled_matches WHERE verify_method = ? ORDER BY settled_at DESC LIMIT ?`
      : `SELECT match_key, invite_code, seed, winner_steam_id, loser_steam_id,
                winner_delta, loser_delta, level_index, ascent, format,
                verify_method, is_void, settled_at
         FROM settled_matches ORDER BY settled_at DESC LIMIT ?`;
    const stmt = env.DB.prepare(sql);
    const { results } = await (verifyMethod
      ? stmt.bind(verifyMethod, limit)
      : stmt.bind(limit)).all();

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
        env.DB.prepare("DELETE FROM match_splits"),
        env.DB.prepare("DELETE FROM player_pbs"),
        env.DB.prepare("DELETE FROM players")
      ]);
    } else {
      // reset = 清空对局与战绩, 与 wipe 保持同一清理范围 (玩家档案本身保留)
      await env.DB.batch([
        env.DB.prepare("DELETE FROM settled_matches"),
        env.DB.prepare("DELETE FROM match_reports"),
        env.DB.prepare("DELETE FROM match_splits"),
        env.DB.prepare("DELETE FROM player_pbs"),
        env.DB.prepare(
          `UPDATE players SET wins = 0, losses = 0, draws = 0, streak = 0,
            peak_elo = elo,
            wins_all_peak = 0, losses_all_peak = 0, wins_hardcore = 0, losses_hardcore = 0,
            wins_sprint = 0, losses_sprint = 0, games_all_peak = 0, games_hardcore = 0, games_sprint = 0,
            updated_at = ?`
        ).bind(now)
      ]);
    }
    await audit(env, action, "{}", note, now);
    return jsonResp({ ok: true, status: action }, 200);
  }

  if (action === "list-pbs") {
    // 查证: 列出某个玩家的 PB, 便于定位要精确删除的 category/subKey
    const steamId = String(body.steamId || "").trim();
    if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400);
    const { results } = await env.DB.prepare(
      `SELECT category, sub_key, time_ms, level_index, seed, ascent, format, achieved_at
       FROM player_pbs WHERE steam_id = ? ORDER BY category, sub_key`
    ).bind(steamId).all();
    return jsonResp({ ok: true, steamId, count: (results || []).length, pbs: results || [] }, 200);
  }

  if (action === "delete-pb") {
    // 精确删除某玩家某一条 PB (category + subKey), 用于清理脏记录根因
    //
    // 与 purge-bad-pbs 的区别: 后者按"物理下限"批量扫, 只能清"明显不可能"的值;
    // steamId 必填, 避免误伤全服; subKey 可选, 不传则删该 category 下的全部。
    const steamId = String(body.steamId || "").trim();
    const category = parseInt(body.category, 10);
    if (!steamId || isNaN(category)) {
      return jsonResp({ ok: false, error: "Missing steamId or category" }, 400);
    }
    let result;
    if (body.subKey === undefined || body.subKey === null || body.subKey === "") {
      result = await env.DB.prepare(
        "DELETE FROM player_pbs WHERE steam_id = ? AND category = ?"
      ).bind(steamId, category).run();
    } else {
      const subKey = parseInt(body.subKey, 10);
      if (isNaN(subKey)) return jsonResp({ ok: false, error: "Invalid subKey" }, 400);
      result = await env.DB.prepare(
        "DELETE FROM player_pbs WHERE steam_id = ? AND category = ? AND sub_key = ?"
      ).bind(steamId, category, subKey).run();
    }
    const deleted = Number(result && result.meta ? result.meta.changes : 0) || 0;
    await audit(env, action,
      JSON.stringify({ steamId, category, subKey: body.subKey === undefined ? "*" : body.subKey, deleted }),
      note, now);
    return jsonResp({ ok: true, status: "deleted", deleted }, 200);
  }

  if (action === "purge-bad-pbs") {
    // 清理历史缺陷遗留的脏 PB。
    //   category 0 = 全山, 5 分钟以内不可能是真成绩
    //   category 1 = 天阶, 2 分钟以内同理
    // 只删"低于物理下限"的记录; 有能力的玩家真打出来的快成绩不会被误删。
    // steamId 可选: 传了就只清这个人, 不传则全服扫描。
    const steamId = String(body.steamId || "").trim();
    const sql = steamId
      ? `DELETE FROM player_pbs
         WHERE steam_id = ?
           AND ((category = 0 AND time_ms < ?) OR (category = 1 AND time_ms < ?))`
      : `DELETE FROM player_pbs
         WHERE (category = 0 AND time_ms < ?) OR (category = 1 AND time_ms < ?)`;
    const stmt = env.DB.prepare(sql);
    const result = await (steamId
      ? stmt.bind(steamId, MIN_ALLPEAK_MS, MIN_ASCENT_MS)
      : stmt.bind(MIN_ALLPEAK_MS, MIN_ASCENT_MS)).run();

    const deleted = Number(result && result.meta ? result.meta.changes : 0) || 0;
    await audit(env, action, JSON.stringify({ steamId: steamId || "*", deleted }), note, now);
    return jsonResp({ ok: true, status: "purged", deleted, steamId: steamId || "*" }, 200);
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
