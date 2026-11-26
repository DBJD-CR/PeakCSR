// 玩家 PB (个人最佳) 云端存取
//
// 全量存放: 全山 1 + 天阶 N + 单群系冲刺 11 + 群系赛段 10 (顶峰不单独成段) 左右,
// 单人量级几十行, 在 D1 免费额度下完全可忽略, 因此不做摘要裁剪。
import { jsonResp } from "../lib/http.js";
import { sha256Hex16, SHARED_SALT } from "../lib/crypto.js";

const MAX_PBS_PER_PLAYER = 64;
const MIN_TIME_MS = 30 * 1000;
const MAX_TIME_MS = 6 * 60 * 60 * 1000;
// 全山 (category 0) 的物理下限: 任何 5 分钟以内的全山成绩都不可能是真的
const MIN_ALLPEAK_MS = 5 * 60 * 1000;
// 天阶 PB (category 1) 同理, 低天阶可以更快, 只拦明显不可能的值
const MIN_ASCENT_MS = 2 * 60 * 1000;

function minTimeFor(category) {
  if (category === 0) return MIN_ALLPEAK_MS;
  if (category === 1) return MIN_ASCENT_MS;
  return MIN_TIME_MS;
}

function clampCategory(v) {
  const n = parseInt(v, 10);
  if (isNaN(n) || n < 0 || n > 3) return -1;
  return n;
}

function clampSubKey(v) {
  const n = parseInt(v, 10);
  if (isNaN(n) || n < 0 || n > 15) return -1;
  return n;
}

// 达成时间以客户端上报为准, 只在缺失/非法/明显超前时退回服务器当前时间
function clampAchievedAt(v) {
  const n = parseInt(v, 10);
  if (!Number.isFinite(n) || n <= 0) return 0;
  // 宽容 1 天时钟漂移, 再往上就是脏数据
  if (n > Date.now() + 24 * 60 * 60 * 1000) return 0;
  return n;
}

// GET /api/pbs?steamId=xxx
export async function handlePbsGet(url, env) {
  const steamId = (url.searchParams.get("steamId") || "").trim();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400);

  const { results } = await env.DB.prepare(
    `SELECT category, sub_key, time_ms, level_index, seed, ascent, format, achieved_at
     FROM player_pbs WHERE steam_id = ?`
  ).bind(steamId).all();

  const pbs = (results || []).map(function (r) {
    return {
      category: r.category,
      subKey: r.sub_key,
      timeMs: r.time_ms,
      levelIndex: r.level_index,
      seed: r.seed,
      ascent: r.ascent,
      format: r.format,
      achievedAt: r.achieved_at
    };
  });

  return jsonResp({ ok: true, refreshedAt: Date.now(), pbs }, 200);
}

// POST /api/pbs  批量上传
// 幂等: 只在"新的更快"时覆盖, 因此重复提交同一批数据是无害的
export async function handlePbsPost(body, env) {
  const steamId = String(body.steamId || "").trim();
  const signature = String(body.signature || "").trim().toLowerCase();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400);

  const raw = Array.isArray(body.pbs) ? body.pbs : [];
  if (raw.length === 0) return jsonResp({ ok: true, status: "empty", applied: 0 }, 200);
  if (raw.length > MAX_PBS_PER_PLAYER) {
    return jsonResp({ ok: false, error: "Too many entries" }, 400);
  }

  // 签名必须基于"服务端最终会采纳的条目集合", 因此先规整再签名
  const entries = [];
  for (let i = 0; i < raw.length; i++) {
    const it = raw[i] || {};
    const category = clampCategory(it.category);
    const subKey = clampSubKey(it.subKey);
    const timeMs = parseInt(it.timeMs, 10);
    if (category < 0 || subKey < 0) continue;
    if (!Number.isFinite(timeMs) || timeMs < minTimeFor(category) || timeMs > MAX_TIME_MS) continue;
    entries.push({
      category,
      subKey,
      timeMs,
      levelIndex: parseInt(it.levelIndex, 10) || 0,
      seed: parseInt(it.seed, 10) || 0,
      ascent: parseInt(it.ascent, 10) || 0,
      format: parseInt(it.format, 10) || 0,
      achievedAt: clampAchievedAt(it.achievedAt)
    });
  }

  // 签名: steamId | 排序后的 category:subKey:timeMs 串 | salt
  // 排序保证同一批数据顺序不同也能得到同一个签名
  // 空集合也要走签名校验
  const normalized = entries
    .map(e => `${e.category}:${e.subKey}:${e.timeMs}`)
    .sort()
    .join(",");
  const expectedSig = await sha256Hex16(`${steamId}|${normalized}|${SHARED_SALT}`);
  if (signature !== expectedSig) {
    return jsonResp({ ok: false, error: "Signature verification failed" }, 403);
  }

  if (entries.length === 0) {
    // 签名通过但没有可采纳的条目: 这是客户端的无效数据, 不是鉴权失败
    return jsonResp({ ok: true, status: "empty", applied: 0 }, 200);
  }

  const now = Date.now();
  const statements = entries.map(function (e) {
    // 客户端带了达成时间就用它, 否则退回服务器当前时间 (仅用于首次插入)
    const achievedAt = e.achievedAt > 0 ? e.achievedAt : now;
    // 只有更快才覆盖; 同毫秒保留先达成的记录
    //
    // 覆盖时的 achieved_at 用 COALESCE(excluded, 原值)
    // 客户端没带时间(0)时保留库里原有的达成时间
    return env.DB.prepare(
      `INSERT INTO player_pbs (steam_id, category, sub_key, time_ms, level_index, seed, ascent, format, achieved_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
       ON CONFLICT(steam_id, category, sub_key) DO UPDATE SET
         time_ms = excluded.time_ms,
         level_index = excluded.level_index,
         seed = excluded.seed,
         ascent = excluded.ascent,
         format = excluded.format,
         achieved_at = CASE WHEN excluded.achieved_at > 0 THEN excluded.achieved_at
                            ELSE player_pbs.achieved_at END
       WHERE excluded.time_ms < player_pbs.time_ms`
    ).bind(steamId, e.category, e.subKey, e.timeMs, e.levelIndex, e.seed, e.ascent, e.format, achievedAt);
  });

  await env.DB.batch(statements);
  return jsonResp({ ok: true, status: "updated", applied: entries.length }, 200);
}
