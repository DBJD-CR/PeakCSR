// PeakCSR (PCSR 1v1) Cloudflare Worker 零成本无服务器天梯后端
// 支持: 双方客户端 SHA-256 签名共识校验 + 原版 RankedPeak 后端 /ranked/races/{code} 交叉验证
// 评分口径必须与 src/EloCalculator.cs 完全一致, 修改任何一处都要同步另一处

// 客户端签名盐：必须与 src/CloudLeaderboardService.cs 中的 SharedCloudSalt 保持一致
const SHARED_SALT = "PEAKCSR_CLOUD_V1_2026";

// 上游 RANKED 后端的 API Key 绝不能提交到公开仓库！
// 部署时用 wrangler secret put UPSTREAM_API_KEY 注入，或在控制台
// Settings -> Variables and Secrets 里添加同名加密变量。
const UPSTREAM_API = "https://followme-peak.ddns.net";

// 管理员口令: wrangler secret put ADMIN_TOKEN
// 用于人工处理恶意拔线 / 误判纠正, 未配置时 /api/admin 全部拒绝

// 与 src/EloCalculator.cs 的 AscentMultipliers 完全一致：下标 0 = 标准顶峰(A0)，1~8 = 天阶
const ASCENT_MULTS = [1.00, 1.06, 1.12, 1.18, 1.20, 1.35, 1.50, 1.60, 1.75];
const TENDERFOOT_MULT = 0.75;
const BIOME_SPRINT_FACTOR = 0.22;
const ELO_SCALE_DENOMINATOR = 550.0;
const PLACEMENT_MATCHES = 10;
// 获胜保底基准: 全山/硬核 8 分, 单群系 2 分
const MIN_WIN_FULL_PEAK = 8;
const MIN_WIN_BIOME_SPRINT = 2;
// 1650+ 高端分段把保底钳制到 12, 避免顶端刷分
const HIGH_ELO_FLOOR_THRESHOLD = 1650;
const HIGH_ELO_FLOOR_CAP = 12;

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const corsHeaders = {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type, X-Admin-Token"
    };

    if (request.method === "OPTIONS") {
      return new Response(null, { headers: corsHeaders });
    }

    try {
      if (url.pathname === "/api/leaderboard" && request.method === "GET") {
        return await handleLeaderboard(url, env, corsHeaders);
      }
      if (url.pathname === "/api/profile" && request.method === "GET") {
        return await handleProfile(url, env, corsHeaders);
      }
      if (url.pathname === "/api/profile" && request.method === "POST") {
        const body = await request.json();
        return await handleProfileUpdate(body, env, corsHeaders);
      }
      if (url.pathname === "/api/history" && request.method === "GET") {
        return await handleHistory(url, env, corsHeaders);
      }
      if (url.pathname === "/api/report" && request.method === "POST") {
        const body = await request.json();
        return await handleMatchReport(body, env, corsHeaders);
      }
      if (url.pathname === "/api/admin" && request.method === "POST") {
        const body = await request.json();
        return await handleAdmin(request, body, env, corsHeaders);
      }
      return jsonResp({ ok: false, error: "Not Found" }, 404, corsHeaders);
    } catch (err) {
      return jsonResp({ ok: false, error: err.message || String(err) }, 500, corsHeaders);
    }
  }
};

function jsonResp(obj, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", ...extraHeaders }
  });
}

async function sha256Hex16(raw) {
  const buf = new TextEncoder().encode(raw);
  const hash = await crypto.subtle.digest("SHA-256", buf);
  const bytes = new Uint8Array(hash);
  let hex = "";
  for (let i = 0; i < 8; i++) {
    hex += bytes[i].toString(16).padStart(2, "0");
  }
  return hex;
}

function getAscentMultiplier(ascent) {
  const a = Math.max(-1, Math.min(8, ascent | 0));
  return a < 0 ? TENDERFOOT_MULT : (ASCENT_MULTS[a] || 1.0);
}

// 与客户端 EloCalculator.GetModeMultiplier 严格一致
function getModeMultiplier(format, ascent) {
  const ascentMult = getAscentMultiplier(ascent);
  if (format === 2) {
    return Math.round(BIOME_SPRINT_FACTOR * ascentMult * 100) / 100;
  }
  return ascentMult;
}

// 与客户端 GetMinWinDelta 严格一致: 保底是绝对下限, 不参与赛制倍率
function getMinWinDelta(format, ascent, currentElo) {
  const ascentMult = getAscentMultiplier(ascent);
  const baseMin = format === 2 ? MIN_WIN_BIOME_SPRINT : MIN_WIN_FULL_PEAK;
  const scaled = baseMin * ascentMult;
  let result = format === 2 ? Math.floor(scaled) : Math.round(scaled);
  if (currentElo >= HIGH_ELO_FLOOR_THRESHOLD) {
    result = Math.min(result, HIGH_ELO_FLOOR_CAP);
  }
  return Math.max(1, result);
}

// 与客户端 GetMaxLossDelta 严格一致
function getMaxLossDelta(format) {
  return format === 2 ? -1 : -2;
}

function getBaseKFactor(totalGames, currentElo) {
  if (totalGames < PLACEMENT_MATCHES) return 50;
  if (currentElo >= HIGH_ELO_FLOOR_THRESHOLD) return 32;
  return 40;
}

// 与客户端 ComputeProgressModifier 保持同一设计思路
function computeProgressModifier(actualScore, selfProgress01, oppProgress01, format, ascent, isPlacement) {
  const myP = Math.max(0, Math.min(1, Number(selfProgress01) || 0));
  const oppP = Math.max(0, Math.min(1, Number(oppProgress01) || 0));
  const diff = myP - oppP;

  let scale = format === 2 ? 0.35 : 1.0;
  if (ascent < 0) scale *= 0.75;

  let bonus = 0;
  if (actualScore > 0.75) {
    const leadMax = isPlacement ? 6.0 : 4.0;
    bonus = Math.max(0, diff) * leadMax * scale;
  } else if (actualScore < 0.25) {
    const completionReliefMax = isPlacement ? 12.0 : 6.0;
    const completionFactor = Math.max(0, Math.min(1, (myP - 0.30) / 0.70));
    const relief = completionFactor * completionReliefMax;
    const gapTerm = diff * (isPlacement ? 6.0 : 3.5);
    bonus = (relief + gapTerm) * scale;
  }

  const maxCap = isPlacement ? 14 : 7;
  return Math.max(-maxCap, Math.min(maxCap, Math.round(bonus)));
}

function matchKey(inviteCode, seed) {
  return inviteCode + "|" + seed;
}

function clamp01(v) {
  const n = Number(v);
  if (!isFinite(n)) return 0;
  return Math.max(0, Math.min(1, n));
}

function clampSeedElo(val) {
  const n = parseInt(val || "1000", 10);
  if (isNaN(n)) return 1000;
  return Math.max(850, Math.min(1500, n));
}

const PLAYER_COLUMNS = [
  "steam_id", "player_name", "nickname", "elo", "peak_elo", "wins", "losses", "draws", "streak",
  "wins_all_peak", "losses_all_peak", "wins_hardcore", "losses_hardcore", "wins_sprint", "losses_sprint",
  "games_all_peak", "games_hardcore", "games_sprint",
  "favorite_map", "favorite_ascent", "favorite_format"
].join(", ");

// 1. 全服 ELO 排行榜 (带分赛制战绩, 供天梯表格与档案卡使用)
async function handleLeaderboard(url, env, corsHeaders) {
  const limit = Math.min(100, Math.max(1, parseInt(url.searchParams.get("limit") || "20", 10)));
  const { results } = await env.DB.prepare(
    "SELECT " + PLAYER_COLUMNS + " FROM players ORDER BY elo DESC, wins DESC LIMIT ?"
  ).bind(limit).all();

  return jsonResp({
    ok: true,
    refreshedAt: Date.now(),
    entries: results || []
  }, 200, corsHeaders);
}

// 2. 单名玩家完整档案
async function handleProfile(url, env, corsHeaders) {
  const steamId = (url.searchParams.get("steamId") || "").trim();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400, corsHeaders);

  const row = await env.DB.prepare(
    "SELECT " + PLAYER_COLUMNS + ", updated_at FROM players WHERE steam_id = ?"
  ).bind(steamId).first();

  let rank = null;
  if (row) {
    const higher = await env.DB.prepare(
      "SELECT COUNT(*) AS c FROM players WHERE elo > ?"
    ).bind(row.elo).first();
    rank = (higher && higher.c ? higher.c : 0) + 1;
  }

  return jsonResp({
    ok: true,
    refreshedAt: Date.now(),
    profile: row || null,
    rank
  }, 200, corsHeaders);
}

// 昵称规则: 2~16 字符, 只允许中英文/数字/空格/_-·, 与客户端 PcsrMenuWindow.SanitizeNickname 保持一致
const NICK_MAX_LEN = 16;
const NICK_MIN_LEN = 2;
// 昵称修改冷却: 24 小时, 防止频繁改名骚扰他人
const NICK_COOLDOWN_MS = 24 * 60 * 60 * 1000;

function sanitizeNickname(raw) {
  if (raw === null || raw === undefined) return "";
  let s = String(raw);
  // 去掉 TMP 富文本标签, 否则可注入 <size> 之类影响他人界面
  s = s.replace(/<\/?(?:align|allcaps|alpha|b|br|color|cspace|font|gradient|i|indent|line-height|line-indent|link|lowercase|margin|mark|material|mspace|nobr|noparse|page|pos|quad|rotate|s|size|smallcaps|space|sprite|strikethrough|style|sub|sup|u|uppercase|voffset|width)(?:[=\s][^<>]*)?>/gi, "");
  // 控制字符与竖线(签名分隔符)统一替换为空格
  s = s.replace(/[\u0000-\u001f\u007f|]/g, " ");
  // 白名单过滤: 字母(含中日韩)/数字/空格/_-·.
  let out = "";
  for (const ch of s) {
    if (/[\p{L}\p{N}]/u.test(ch) || ch === " " || ch === "_" || ch === "-" || ch === "." || ch === "·") {
      out += ch;
    }
  }
  out = out.replace(/\s{2,}/g, " ").trim();
  if (out.length > NICK_MAX_LEN) out = out.slice(0, NICK_MAX_LEN).trim();
  if (out.length > 0 && out.length < NICK_MIN_LEN) return "";
  return out;
}

// 3. 玩家自选档案 (昵称 / 最爱地图 / 最爱难度 / 最爱模式)
// 只允许玩家修改自己的档案, 用签名校验避免任意篡改他人数据
// 玩家还没有战绩时也会自动建档, 这样"起个 PCSR 昵称"不必先打完一局
async function handleProfileUpdate(body, env, corsHeaders) {
  const steamId = String(body.steamId || "").trim();
  const signature = String(body.signature || "").trim().toLowerCase();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400, corsHeaders);

  // favoriteMap 现在是群系索引 0~10, 超出范围一律归 -1 (未设置)
  const favoriteMapRaw = parseMaybeInt(body.favoriteMap, -1);
  const favoriteMap = (favoriteMapRaw >= 0 && favoriteMapRaw <= 10) ? favoriteMapRaw : -1;
  const favoriteAscent = parseMaybeInt(body.favoriteAscent, -999);
  const favoriteFormat = parseMaybeInt(body.favoriteFormat, -1);

  // 昵称走白名单过滤, 空串表示"沿用 Steam 昵称"
  const nickname = sanitizeNickname(body.nickname);

  // 签名顺序必须与 src/CloudLeaderboardService.cs 的 PushProfileRoutine 完全一致
  const rawSigStr = `${steamId}|${favoriteMap}|${favoriteAscent}|${favoriteFormat}|${nickname}|${SHARED_SALT}`;
  const expectedSig = await sha256Hex16(rawSigStr);
  if (signature !== expectedSig) {
    return jsonResp({ ok: false, error: "Signature verification failed" }, 403, corsHeaders);
  }

  // 建档: 不存在时直接创建, 让玩家不用先打一局就能拥有档案
  // 初始分必须用客户端上报的种子分 (从原版 RANKED 软折算而来), 不能默认 1000,
  // 否则新建档会把玩家从 RANKED 继承来的基础分直接抹平
  const displayName = String(body.playerName || "Player").trim().slice(0, 32) || "Player";
  const seedElo = clampSeedElo(body.seedElo);
  const player = await getOrCreatePlayer(env, steamId, displayName, seedElo);
  if (!player) return jsonResp({ ok: false, error: "Cannot create player" }, 500, corsHeaders);

  const now = Date.now();
  const updates = [];
  const binds = [];

  updates.push("favorite_map = ?", "favorite_ascent = ?", "favorite_format = ?");
  binds.push(favoriteMap, favoriteAscent, favoriteFormat);

  const currentNick = player.nickname || "";
  let nickChanged = false;
  if (nickname !== currentNick) {
    if (nickname !== "") {
      // 冷却只约束"改成新昵称": 首次命名或改名后 24h 内不能再次改名
      const lastChange = player.nickname_updated_at || 0;
      if (currentNick !== "" && lastChange > 0 && now - lastChange < NICK_COOLDOWN_MS) {
        const remainH = Math.ceil((NICK_COOLDOWN_MS - (now - lastChange)) / 3600000);
        return jsonResp({
          ok: false,
          error: "Nickname cooldown",
          cooldownHours: remainH
        }, 429, corsHeaders);
      }
      const taken = await env.DB.prepare(
        "SELECT steam_id FROM players WHERE nickname = ? COLLATE NOCASE AND steam_id <> ?"
      ).bind(nickname, steamId).first();
      if (taken) {
        return jsonResp({ ok: false, error: "Nickname already taken" }, 409, corsHeaders);
      }
      updates.push("nickname = ?", "nickname_updated_at = ?");
      binds.push(nickname, now);
    } else {
      // 清空昵称随时允许, 且不消耗冷却窗口, 否则玩家手滑清空后要等一整天
      updates.push("nickname = ?");
      binds.push("");
    }
    nickChanged = true;
  }

  updates.push("updated_at = ?");
  binds.push(now);
  binds.push(steamId);

  try {
    await env.DB.prepare(
      "UPDATE players SET " + updates.join(", ") + " WHERE steam_id = ?"
    ).bind(...binds).run();
  } catch (e) {
    // 唯一索引兜底: 并发抢占同名昵称
    const msg = String((e && e.message) || e);
    if (msg.indexOf("UNIQUE") >= 0) {
      return jsonResp({ ok: false, error: "Nickname already taken" }, 409, corsHeaders);
    }
    throw e;
  }

  return jsonResp({
    ok: true,
    status: "updated",
    nickname,
    nicknameChanged: nickChanged
  }, 200, corsHeaders);
}

function parseMaybeInt(v, fallback) {
  const n = parseInt(v, 10);
  return isNaN(n) ? fallback : n;
}

// 4. 历史战绩 (按 match_key 去重)
async function handleHistory(url, env, corsHeaders) {
  const steamId = (url.searchParams.get("steamId") || "").trim();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400, corsHeaders);
  const limit = Math.min(50, Math.max(1, parseInt(url.searchParams.get("limit") || "20", 10)));

  const { results } = await env.DB.prepare(
    `SELECT match_key, invite_code, seed, winner_steam_id, loser_steam_id,
            winner_delta, loser_delta, win_elo_before, win_elo_after,
            lose_elo_before, lose_elo_after,
            level_index, ascent, format, duration_ms,
            winner_progress, loser_progress, verify_method, is_void, settled_at
     FROM settled_matches
     WHERE winner_steam_id = ? OR loser_steam_id = ?
     ORDER BY settled_at DESC LIMIT ?`
  ).bind(steamId, steamId, limit).all();

  const history = (results || []).map(function (r) {
    const isWinner = r.winner_steam_id === steamId;
    return {
      matchKey: r.match_key,
      inviteCode: r.invite_code,
      seed: r.seed,
      won: isWinner,
      isVoid: !!r.is_void,
      opponentSteamId: isWinner ? r.loser_steam_id : r.winner_steam_id,
      delta: isWinner ? r.winner_delta : r.loser_delta,
      eloBefore: isWinner ? r.win_elo_before : r.lose_elo_before,
      eloAfter: isWinner ? r.win_elo_after : r.lose_elo_after,
      levelIndex: r.level_index,
      ascent: r.ascent,
      format: r.format,
      durationMs: r.duration_ms,
      myProgress: isWinner ? r.winner_progress : r.loser_progress,
      oppProgress: isWinner ? r.loser_progress : r.winner_progress,
      verifyMethod: r.verify_method,
      settledAt: r.settled_at
    };
  });

  return jsonResp({ ok: true, history }, 200, corsHeaders);
}

// 5. 对局结果上报
async function handleMatchReport(body, env, corsHeaders) {
  const inviteCode = String(body.inviteCode || "").trim().toUpperCase();
  const reporterId = String(body.reporterSteamId || "").trim();
  const opponentId = String(body.opponentSteamId || "").trim();
  const reporterName = String(body.reporterName || "Player").trim().slice(0, 32);
  const opponentName = String(body.opponentName || "Opponent").trim().slice(0, 32);
  const reporterSeedElo = clampSeedElo(body.reporterSeedElo);
  const opponentSeedElo = clampSeedElo(body.opponentSeedElo);
  const winnerId = String(body.winnerSteamId || "").trim();
  const seed = parseInt(body.seed || "0", 10);
  const levelIndex = parseInt(body.levelIndex || "0", 10);
  const ascent = Math.max(-1, Math.min(8, parseInt(body.ascent || "0", 10) || 0));
  const format = Math.max(0, Math.min(2, parseInt(body.format || "0", 10) || 0));
  const durationMs = parseInt(body.durationMs || "0", 10);
  const signature = String(body.signature || "").trim().toLowerCase();
  const isVoid = body.isVoid === true || body.isVoid === 1 || body.isVoid === "1";
  const voidReason = String(body.voidReason || "").trim().slice(0, 64);

  const reporterProgress = clamp01(body.reporterProgress);
  const opponentProgress = clamp01(body.opponentProgress);

  if (!inviteCode || !reporterId || !opponentId || reporterId === opponentId) {
    return jsonResp({ ok: false, error: "Invalid match parameters" }, 400, corsHeaders);
  }
  // 非作废局必须给出胜者
  if (!isVoid && !winnerId) {
    return jsonResp({ ok: false, error: "Invalid match parameters" }, 400, corsHeaders);
  }

  // 签名校验 (含双方种子分与作废标记, 防止篡改)
  // 字段顺序必须与 src/CloudLeaderboardService.cs 的 rawSigStr 完全一致
  const rawSigStr = `${inviteCode}|${reporterId}|${opponentId}|${winnerId}|${seed}|${ascent}|${format}|${reporterSeedElo}|${opponentSeedElo}|${isVoid ? 1 : 0}|${SHARED_SALT}`;
  const expectedSig = await sha256Hex16(rawSigStr);
  if (signature !== expectedSig) {
    return jsonResp({ ok: false, error: "Signature verification failed" }, 403, corsHeaders);
  }

  const key = matchKey(inviteCode, seed);

  // 已结算过 (含 void) 直接回放结果, 避免"再来一局"复用房间号时重复加减分
  const existingSettled = await env.DB.prepare(
    "SELECT * FROM settled_matches WHERE match_key = ?"
  ).bind(key).first();

  if (existingSettled) {
    const p = await getOrCreatePlayer(env, reporterId, reporterName, reporterSeedElo);
    const isWinner = reporterId === existingSettled.winner_steam_id;
    return jsonResp({
      ok: true,
      status: "already_settled",
      verifyMethod: existingSettled.verify_method,
      isVoid: !!existingSettled.is_void,
      elo: p.elo,
      delta: existingSettled.is_void ? 0 : (isWinner ? existingSettled.winner_delta : existingSettled.loser_delta)
    }, 200, corsHeaders);
  }

  const now = Date.now();

  await env.DB.prepare(
    `INSERT OR REPLACE INTO match_reports
     (invite_code, reporter_steam_id, opponent_steam_id, reporter_name, opponent_name, winner_steam_id,
      seed, level_index, ascent, format, duration_ms, signature,
      reporter_progress, opponent_progress, is_void, created_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  ).bind(inviteCode, reporterId, opponentId, reporterName, opponentName, winnerId || "",
    seed, levelIndex, ascent, format, durationMs, signature,
    reporterProgress, opponentProgress, isVoid ? 1 : 0, now).run();

  // 作废局优先: 只要有一方申报作废, 本局就按作废落库, 双方都不加减分。
  // 放在胜负验证之前, 是为了让"我这边检测到掉线"能抢在对手误判胜负之前生效。
  if (isVoid) {
    await recordVoidSettlement(env, key, inviteCode, seed, reporterId, opponentId, levelIndex, ascent, format,
      durationMs, reporterProgress, opponentProgress, now, "client_void:" + (voidReason || "unknown"));
    const p = await getOrCreatePlayer(env, reporterId, reporterName, reporterSeedElo);
    await getOrCreatePlayer(env, opponentId, opponentName, opponentSeedElo);
    return jsonResp({
      ok: true,
      status: "settled_void",
      verifyMethod: "client_void",
      isVoid: true,
      elo: p.elo,
      delta: 0
    }, 200, corsHeaders);
  }

  // 验证通道 A: 对手也上报了同一房间同一局, 且 winner 一致
  const oppReport = await env.DB.prepare(
    "SELECT * FROM match_reports WHERE invite_code = ? AND seed = ? AND reporter_steam_id = ?"
  ).bind(inviteCode, seed, opponentId).first();

  let verifiedWinnerId = null;
  let verifyMethod = null;
  let winnerProgress = 0;
  let loserProgress = 0;

  if (oppReport && !oppReport.is_void && oppReport.winner_steam_id === winnerId) {
    verifiedWinnerId = winnerId;
    verifyMethod = "dual_client_consensus";
    if (winnerId === reporterId) {
      winnerProgress = reporterProgress;
      loserProgress = opponentProgress;
    } else {
      winnerProgress = opponentProgress;
      loserProgress = reporterProgress;
    }
  } else {
    // 验证通道 B: 向原版 RankedPeak 后端交叉验证
    const upstreamResult = await verifyWithVanillaRaceServer(env, inviteCode, reporterId, opponentId, seed);
    if (upstreamResult.verified && upstreamResult.winnerSteamId === winnerId) {
      verifiedWinnerId = winnerId;
      verifyMethod = "vanilla_raceservice_cross_check";
      winnerProgress = reporterProgress;
      loserProgress = opponentProgress;
    }
  }

  if (!verifiedWinnerId) {
    return jsonResp({
      ok: true,
      status: "pending_opponent_or_upstream",
      message: "Report recorded. Waiting for opponent report or upstream confirmation."
    }, 200, corsHeaders);
  }

  const loserId = verifiedWinnerId === reporterId ? opponentId : reporterId;
  const winnerName = verifiedWinnerId === reporterId ? reporterName : opponentName;
  const loserName = verifiedWinnerId === reporterId ? opponentName : reporterName;
  const winnerSeedElo = verifiedWinnerId === reporterId ? reporterSeedElo : opponentSeedElo;
  const loserSeedElo = verifiedWinnerId === reporterId ? opponentSeedElo : reporterSeedElo;

  const settlement = await settleEloOnDb(
    env, key, inviteCode, seed, verifiedWinnerId, winnerName, winnerSeedElo,
    loserId, loserName, loserSeedElo,
    levelIndex, ascent, format, durationMs, winnerProgress, loserProgress,
    verifyMethod, now
  );

  const myNewElo = reporterId === verifiedWinnerId ? settlement.winnerElo : settlement.loserElo;
  const myDelta = reporterId === verifiedWinnerId ? settlement.winnerDelta : settlement.loserDelta;

  return jsonResp({
    ok: true,
    status: "settled",
    verifyMethod,
    isVoid: false,
    elo: myNewElo,
    delta: myDelta
  }, 200, corsHeaders);
}

async function recordVoidSettlement(env, key, inviteCode, seed, reporterId, opponentId, levelIndex, ascent, format,
  durationMs, reporterProgress, opponentProgress, now, verifyMethod) {
  await env.DB.prepare(
    `INSERT OR IGNORE INTO settled_matches
     (match_key, invite_code, seed, winner_steam_id, loser_steam_id, winner_delta, loser_delta,
      win_elo_before, win_elo_after, lose_elo_before, lose_elo_after,
      level_index, ascent, format, duration_ms, winner_progress, loser_progress,
      verify_method, is_void, settled_at)
     VALUES (?, ?, ?, ?, ?, 0, 0, 0, 0, 0, 0, ?, ?, ?, ?, ?, ?, ?, 1, ?)`
  ).bind(key, inviteCode, seed, reporterId, opponentId,
    levelIndex, ascent, format, durationMs, reporterProgress, opponentProgress,
    verifyMethod, now).run();
}

// 去原版 followme-peak.ddns.net 查询真实房间状态做交叉核验
async function verifyWithVanillaRaceServer(env, inviteCode, playerA, playerB, expectedSeed) {
  const upstreamKey = env && env.UPSTREAM_API_KEY;
  if (!upstreamKey) return { verified: false };
  try {
    const resp = await fetch(`${UPSTREAM_API}/ranked/races/${encodeURIComponent(inviteCode)}`, {
      method: "GET",
      headers: { "X-API-Key": upstreamKey }
    });
    if (!resp.ok) return { verified: false };

    const data = await resp.json();
    const race = data.race;
    const participants = data.participants || [];
    if (!race || (expectedSeed && race.seed !== expectedSeed)) {
      return { verified: false };
    }

    const pA = participants.find(p => p.steamId === playerA);
    const pB = participants.find(p => p.steamId === playerB);
    // 双方都必须仍在赛事里才谈得上"谁赢"。任何一方从 participants 中消失,
    // 都只可能是掉线 / 被踢 / 断网重连, 这类情况一律不判胜负 (交由 void 通道处理)。
    if (!pA || !pB) return { verified: false };

    // 带 removed 标记的参与者 = 已离开赛事, 不是竞技结果
    if (pA.removed || pB.removed) return { verified: false };

    // 情况1：一方真实登顶完赛且 Rank === 1 (唯一可判胜负的强证据)
    if (pA.state === "finished" && pA.rank === 1 && pB.state !== "finished") return { verified: true, winnerSteamId: playerA };
    if (pB.state === "finished" && pB.rank === 1 && pA.state !== "finished") return { verified: true, winnerSteamId: playerB };

    // 情况2：一方确实死亡 (deaths > 0) 且状态 dnf, 另一方存活
    // 注意: 单纯 dnf 而没有 deaths 记录无法区分"死亡"与"掉线", 必须要求 deaths > 0
    if (pB.state === "dnf" && pB.deaths > 0 && pA.state !== "dnf") return { verified: true, winnerSteamId: playerA };
    if (pA.state === "dnf" && pA.deaths > 0 && pB.state !== "dnf") return { verified: true, winnerSteamId: playerB };

    return { verified: false };
  } catch {
    return { verified: false };
  }
}

async function getOrCreatePlayer(env, steamId, playerName, seedElo = 1000) {
  let row = await env.DB.prepare("SELECT * FROM players WHERE steam_id = ?").bind(steamId).first();
  if (!row) {
    const initElo = clampSeedElo(seedElo);
    const now = Date.now();
    await env.DB.prepare(
      `INSERT OR IGNORE INTO players
       (steam_id, player_name, elo, peak_elo, wins, losses, draws, streak,
        wins_all_peak, losses_all_peak, wins_hardcore, losses_hardcore, wins_sprint, losses_sprint,
        games_all_peak, games_hardcore, games_sprint,
        favorite_map, favorite_ascent, favorite_format, updated_at)
       VALUES (?, ?, ?, ?, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, -999, -1, ?)`
    ).bind(steamId, playerName || "Player", initElo, initElo, now).run();
    row = await env.DB.prepare("SELECT * FROM players WHERE steam_id = ?").bind(steamId).first();
  }
  return row;
}

// 零和 ELO 权威结算 (口径与本地 EloCalculator 完全一致)
async function settleEloOnDb(env, key, inviteCode, seed, winnerId, winnerName, winnerSeedElo,
  loserId, loserName, loserSeedElo, levelIndex, ascent, format, durationMs,
  winnerProgress, loserProgress, verifyMethod, now) {
  const winPlayer = await getOrCreatePlayer(env, winnerId, winnerName, winnerSeedElo);
  const losePlayer = await getOrCreatePlayer(env, loserId, loserName, loserSeedElo);

  const mult = getModeMultiplier(format, ascent);
  const winExpected = 1.0 / (1.0 + Math.pow(10.0, (losePlayer.elo - winPlayer.elo) / ELO_SCALE_DENOMINATOR));
  const loseExpected = 1.0 - winExpected;

  const winTotal = (winPlayer.wins || 0) + (winPlayer.losses || 0) + (winPlayer.draws || 0);
  const loseTotal = (losePlayer.wins || 0) + (losePlayer.losses || 0) + (losePlayer.draws || 0);
  const winK = getBaseKFactor(winTotal, winPlayer.elo);
  const loseK = getBaseKFactor(loseTotal, losePlayer.elo);
  const winIsPlacement = winTotal < PLACEMENT_MATCHES;
  const loseIsPlacement = loseTotal < PLACEMENT_MATCHES;

  // 保底是绝对下限, 不参与赛制倍率
  const winMin = getMinWinDelta(format, ascent, winPlayer.elo);
  const loseMax = getMaxLossDelta(format);

  let winBase = Math.round(winK * (1.0 - winExpected) * mult);
  let loseBase = Math.round(loseK * (0.0 - loseExpected) * mult);
  winBase = Math.max(winMin, winBase);
  if (loseBase >= 0) loseBase = -1;

  const winBonus = computeProgressModifier(1.0, winnerProgress, loserProgress, format, ascent, winIsPlacement);
  const loseBonus = computeProgressModifier(0.0, loserProgress, winnerProgress, format, ascent, loseIsPlacement);

  let winDelta = winBase + winBonus;
  let loseDelta = loseBase + loseBonus;

  // 保底 / 扣分上限绝对钳制
  winDelta = Math.max(winMin, winDelta);
  loseDelta = Math.max(loseMax, Math.min(0, loseDelta));

  const newWinElo = Math.max(100, winPlayer.elo + winDelta);
  const newWinPeak = Math.max(winPlayer.peak_elo, newWinElo);
  const newWinStreak = winPlayer.streak >= 0 ? winPlayer.streak + 1 : 1;

  const newLoseElo = Math.max(100, losePlayer.elo + loseDelta);
  const newLosePeak = Math.max(losePlayer.peak_elo, newLoseElo);
  const newLoseStreak = losePlayer.streak <= 0 ? losePlayer.streak - 1 : -1;

  const winModeCols = modeColumns(format, "wins");
  const loseModeCols = modeColumns(format, "losses");

  await env.DB.batch([
    env.DB.prepare(
      `UPDATE players SET player_name = ?, elo = ?, peak_elo = ?, wins = wins + 1, streak = ?,
        ${winModeCols}, updated_at = ? WHERE steam_id = ?`
    ).bind(winnerName, newWinElo, newWinPeak, newWinStreak, now, winnerId),
    env.DB.prepare(
      `UPDATE players SET player_name = ?, elo = ?, peak_elo = ?, losses = losses + 1, streak = ?,
        ${loseModeCols}, updated_at = ? WHERE steam_id = ?`
    ).bind(loserName, newLoseElo, newLosePeak, newLoseStreak, now, loserId),
    env.DB.prepare(
      `INSERT OR IGNORE INTO settled_matches
       (match_key, invite_code, seed, winner_steam_id, loser_steam_id, winner_delta, loser_delta,
        win_elo_before, win_elo_after, lose_elo_before, lose_elo_after,
        level_index, ascent, format, duration_ms, winner_progress, loser_progress,
        verify_method, is_void, settled_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?)`
    ).bind(key, inviteCode, seed, winnerId, loserId, winDelta, loseDelta,
      winPlayer.elo, newWinElo, losePlayer.elo, newLoseElo,
      levelIndex, ascent, format, durationMs, winnerProgress, loserProgress,
      verifyMethod, now)
  ]);

  return { winnerElo: newWinElo, loserElo: newLoseElo, winnerDelta: winDelta, loserDelta: loseDelta };
}

// 按赛制拼接需要自增的列 (同时累加场次)
function modeColumns(format, side) {
  if (format === 2) {
    return `wins_sprint = wins_sprint + ${side === "wins" ? 1 : 0},
            losses_sprint = losses_sprint + ${side === "losses" ? 1 : 0},
            games_sprint = games_sprint + 1`;
  }
  if (format === 1) {
    return `wins_hardcore = wins_hardcore + ${side === "wins" ? 1 : 0},
            losses_hardcore = losses_hardcore + ${side === "losses" ? 1 : 0},
            games_hardcore = games_hardcore + 1`;
  }
  return `wins_all_peak = wins_all_peak + ${side === "wins" ? 1 : 0},
          losses_all_peak = losses_all_peak + ${side === "losses" ? 1 : 0},
          games_all_peak = games_all_peak + 1`;
}

// 6. 管理员后门: 人工处理恶意拔线 / 误判纠正
// 需要请求头 X-Admin-Token 与 env.ADMIN_TOKEN 一致
async function handleAdmin(request, body, env, corsHeaders) {
  const expected = env && env.ADMIN_TOKEN;
  if (!expected) {
    return jsonResp({ ok: false, error: "Admin API disabled (ADMIN_TOKEN not configured)" }, 403, corsHeaders);
  }
  const provided = request.headers.get("X-Admin-Token") || String(body.token || "");
  if (provided !== expected) {
    return jsonResp({ ok: false, error: "Forbidden" }, 403, corsHeaders);
  }

  const action = String(body.action || "").trim();
  const note = String(body.note || "").trim().slice(0, 200);
  const now = Date.now();

  if (action === "void") {
    // 作废某场对局并回滚双方分数
    const key = String(body.matchKey || "").trim() ||
      matchKey(String(body.inviteCode || "").trim().toUpperCase(), parseInt(body.seed || "0", 10));
    const row = await env.DB.prepare("SELECT * FROM settled_matches WHERE match_key = ?").bind(key).first();
    if (!row) return jsonResp({ ok: false, error: "Match not found" }, 404, corsHeaders);
    if (row.is_void) return jsonResp({ ok: true, status: "already_void" }, 200, corsHeaders);

    const win = await env.DB.prepare("SELECT * FROM players WHERE steam_id = ?").bind(row.winner_steam_id).first();
    const lose = await env.DB.prepare("SELECT * FROM players WHERE steam_id = ?").bind(row.loser_steam_id).first();

    if (win && lose) {
      const winBack = Math.max(100, win.elo - row.winner_delta);
      const loseBack = Math.max(100, lose.elo - row.loser_delta);
      const winMode = modeRollbackColumns(row.format, "wins");
      const loseMode = modeRollbackColumns(row.format, "losses");
      await env.DB.batch([
        env.DB.prepare(
          `UPDATE players SET elo = ?, wins = MAX(0, wins - 1), streak = ?,
            ${winMode}, updated_at = ? WHERE steam_id = ?`
        ).bind(winBack, win.streak > 0 ? win.streak - 1 : 0, now, row.winner_steam_id),
        env.DB.prepare(
          `UPDATE players SET elo = ?, losses = MAX(0, losses - 1), streak = ?,
            ${loseMode}, updated_at = ? WHERE steam_id = ?`
        ).bind(loseBack, lose.streak < 0 ? lose.streak + 1 : 0, now, row.loser_steam_id),
        env.DB.prepare("UPDATE settled_matches SET is_void = 1, winner_delta = 0, loser_delta = 0 WHERE match_key = ?")
          .bind(key)
      ]);
    }

    await audit(env, action, JSON.stringify({ key, inviteCode: row.invite_code, seed: row.seed }), note, now);
    return jsonResp({ ok: true, status: "voided", matchKey: key }, 200, corsHeaders);
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
    return jsonResp({ ok: true, count: rows.length, matches: rows }, 200, corsHeaders);
  }

  if (action === "set-elo") {
    const steamId = String(body.steamId || "").trim();
    const elo = parseInt(body.elo, 10);
    if (!steamId || isNaN(elo)) {
      return jsonResp({ ok: false, error: "Missing steamId or elo" }, 400, corsHeaders);
    }
    const clamped = Math.max(100, Math.min(5000, elo));
    const p = await env.DB.prepare("SELECT * FROM players WHERE steam_id = ?").bind(steamId).first();
    if (!p) return jsonResp({ ok: false, error: "Player not found" }, 404, corsHeaders);

    await env.DB.prepare(
      "UPDATE players SET elo = ?, peak_elo = MAX(peak_elo, ?), updated_at = ? WHERE steam_id = ?"
    ).bind(clamped, clamped, now, steamId).run();

    await audit(env, action, JSON.stringify({ steamId, before: p.elo, after: clamped }), note, now);
    return jsonResp({ ok: true, status: "updated", before: p.elo, after: clamped }, 200, corsHeaders);
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
    return jsonResp({ ok: true, status: action }, 200, corsHeaders);
  }

  return jsonResp({ ok: false, error: "Unknown action: " + action }, 400, corsHeaders);
}

function modeRollbackColumns(format, side) {
  const w = side === "wins" ? "MAX(0, wins_COL - 1)" : "wins_COL";
  const l = side === "losses" ? "MAX(0, losses_COL - 1)" : "losses_COL";
  if (format === 2) {
    return `wins_sprint = ${w.replace("COL", "sprint")},
            losses_sprint = ${l.replace("COL", "sprint")},
            games_sprint = MAX(0, games_sprint - 1)`;
  }
  if (format === 1) {
    return `wins_hardcore = ${w.replace("COL", "hardcore")},
            losses_hardcore = ${l.replace("COL", "hardcore")},
            games_hardcore = MAX(0, games_hardcore - 1)`;
  }
  return `wins_all_peak = ${w.replace("COL", "all_peak")},
          losses_all_peak = ${l.replace("COL", "all_peak")},
          games_all_peak = MAX(0, games_all_peak - 1)`;
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
