// PeakCSR (PCSR 1v1) Cloudflare Worker 零成本无服务器天梯后端
// 同时支持：【机制1：双方客户端 SHA-256 签名共识校验】 + 【机制2：原版 RankedPeak 后端 /ranked/races/{code} 交叉验证】

// 客户端签名盐：必须与 src/CloudLeaderboardService.cs 中的 SharedCloudSalt 保持一致，
// 因此只能内嵌（它本身不是机密，只是防止随手伪造请求的弱校验）
const SHARED_SALT = "PEAKCSR_CLOUD_V1_2026";

// 上游 RANKED 后端的 API Key 绝不能提交到公开仓库！
// 部署时用 wrangler secret put UPSTREAM_API_KEY 注入，或在控制台
// Settings -> Variables and Secrets 里添加同名加密变量。
const UPSTREAM_API = "https://followme-peak.ddns.net";
const ASCENT_MULTS = [1.00, 1.06, 1.12, 1.18, 1.20, 1.35, 1.50, 1.60, 1.75];

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const corsHeaders = {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
      "Access-Control-Allow-Headers": "Content-Type"
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
      if (url.pathname === "/api/report" && request.method === "POST") {
        const body = await request.json();
        return await handleMatchReport(body, env, corsHeaders);
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

function getModeMultiplier(format, ascent) {
  const a = Math.max(0, Math.min(8, ascent | 0));
  const mult = ASCENT_MULTS[a] || 1.0;
  if (format === 2) {
    return Math.round(0.22 * mult * 100) / 100;
  }
  return mult;
}

// 1. 获取全服 ELO 排行榜
async function handleLeaderboard(url, env, corsHeaders) {
  const limit = Math.min(100, Math.max(1, parseInt(url.searchParams.get("limit") || "20", 10)));
  const { results } = await env.DB.prepare(
    "SELECT steam_id, player_name, elo, peak_elo, wins, losses, streak FROM players ORDER BY elo DESC LIMIT ?"
  ).bind(limit).all();

  return jsonResp({ ok: true, entries: results || [] }, 200, corsHeaders);
}

// 2. 获取单名玩家云端档案
async function handleProfile(url, env, corsHeaders) {
  const steamId = (url.searchParams.get("steamId") || "").trim();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400, corsHeaders);

  const row = await env.DB.prepare(
    "SELECT steam_id, player_name, elo, peak_elo, wins, losses, streak FROM players WHERE steam_id = ?"
  ).bind(steamId).first();

  return jsonResp({ ok: true, profile: row || null }, 200, corsHeaders);
}

// 3. 接收对局结果上报：双端签名共识 + 原版 RaceService 后端交叉验证
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
  const ascent = parseInt(body.ascent || "0", 10);
  const format = parseInt(body.format || "0", 10);
  const durationMs = parseInt(body.durationMs || "0", 10);
  const signature = String(body.signature || "").trim().toLowerCase();

  if (!inviteCode || !reporterId || !opponentId || !winnerId || reporterId === opponentId) {
    return jsonResp({ ok: false, error: "Invalid match parameters" }, 400, corsHeaders);
  }

  // 校验客户端 SHA-256 签名
  const rawSigStr = `${inviteCode}|${reporterId}|${opponentId}|${winnerId}|${seed}|${ascent}|${format}|${SHARED_SALT}`;
  const expectedSig = await sha256Hex16(rawSigStr);
  if (signature !== expectedSig) {
    return jsonResp({ ok: false, error: "Signature verification failed" }, 403, corsHeaders);
  }

  // 检查该房间是否已经结算过
  const existingSettled = await env.DB.prepare(
    "SELECT * FROM settled_matches WHERE invite_code = ?"
  ).bind(inviteCode).first();

  if (existingSettled) {
    const p = await getOrCreatePlayer(env, reporterId, reporterName, reporterSeedElo);
    return jsonResp({
      ok: true,
      status: "already_settled",
      verifyMethod: existingSettled.verify_method,
      elo: p.elo,
      delta: reporterId === existingSettled.winner_steam_id ? existingSettled.winner_delta : existingSettled.loser_delta
    }, 200, corsHeaders);
  }

  const now = Date.now();

  // 写入本次上报记录
  await env.DB.prepare(
    `INSERT OR REPLACE INTO match_reports
     (invite_code, reporter_steam_id, opponent_steam_id, reporter_name, opponent_name, winner_steam_id, seed, level_index, ascent, format, duration_ms, signature, created_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  ).bind(inviteCode, reporterId, opponentId, reporterName, opponentName, winnerId, seed, levelIndex, ascent, format, durationMs, signature, now).run();

  // 【验证通道 A】：检查对手是否也提交了该房间的战报，且双方申报的 winner_steam_id 完全一致
  const oppReport = await env.DB.prepare(
    "SELECT * FROM match_reports WHERE invite_code = ? AND reporter_steam_id = ?"
  ).bind(inviteCode, opponentId).first();

  let verifiedWinnerId = null;
  let verifyMethod = null;

  if (oppReport && oppReport.winner_steam_id === winnerId) {
    verifiedWinnerId = winnerId;
    verifyMethod = "dual_client_consensus";
  } else {
    // 【验证通道 B】：向原版 RankedPeak 后端 /ranked/races/{inviteCode} 发起交叉验证
    // 即使败方破防强退游戏不发包，只要原版服务器记录了完赛名次(rank=1)、对手DNF/死亡或离开，照样权威判胜！
    const upstreamResult = await verifyWithVanillaRaceServer(env, inviteCode, reporterId, opponentId, seed);
    if (upstreamResult.verified && upstreamResult.winnerSteamId === winnerId) {
      verifiedWinnerId = winnerId;
      verifyMethod = "vanilla_raceservice_cross_check";
    }
  }

  if (!verifiedWinnerId) {
    return jsonResp({
      ok: true,
      status: "pending_opponent_or_upstream",
      message: "Report recorded. Waiting for opponent report or upstream confirmation."
    }, 200, corsHeaders);
  }

  // 执行零和 ELO 权威结算
  const loserId = verifiedWinnerId === reporterId ? opponentId : reporterId;
  const winnerName = verifiedWinnerId === reporterId ? reporterName : opponentName;
  const loserName = verifiedWinnerId === reporterId ? opponentName : reporterName;
  const winnerSeedElo = verifiedWinnerId === reporterId ? reporterSeedElo : opponentSeedElo;
  const loserSeedElo = verifiedWinnerId === reporterId ? opponentSeedElo : reporterSeedElo;

  const settlement = await settleEloOnDb(env, inviteCode, verifiedWinnerId, winnerName, winnerSeedElo, loserId, loserName, loserSeedElo, format, ascent, verifyMethod, now);
  const myNewElo = reporterId === verifiedWinnerId ? settlement.winnerElo : settlement.loserElo;
  const myDelta = reporterId === verifiedWinnerId ? settlement.winnerDelta : settlement.loserDelta;

  return jsonResp({
    ok: true,
    status: "settled",
    verifyMethod,
    elo: myNewElo,
    delta: myDelta
  }, 200, corsHeaders);
}

// 去原版 followme-peak.ddns.net 查询真实房间状态做交叉核验
async function verifyWithVanillaRaceServer(env, inviteCode, playerA, playerB, expectedSeed) {
  // 缺少密钥时直接跳过通道 B，只用双端签名共识判定，避免把密钥写进代码
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
    if (!pA && !pB) return { verified: false };

    // 情况1：一方完赛且 Rank === 1
    if (pA && pA.state === "finished" && pA.rank === 1) return { verified: true, winnerSteamId: playerA };
    if (pB && pB.state === "finished" && pB.rank === 1) return { verified: true, winnerSteamId: playerB };

    // 情况2：一方 DNF / 死亡，另一方存活
    if (pB && (pB.state === "dnf" || pB.deaths > 0 || pB.removed) && pA && pA.state !== "dnf") {
      return { verified: true, winnerSteamId: playerA };
    }
    if (pA && (pA.state === "dnf" || pA.deaths > 0 || pA.removed) && pB && pB.state !== "dnf") {
      return { verified: true, winnerSteamId: playerB };
    }

    // 情况3：比赛进行中对手直接退房消失
    if (pA && !pB && race.status !== "lobby") return { verified: true, winnerSteamId: playerA };
    if (pB && !pA && race.status !== "lobby") return { verified: true, winnerSteamId: playerB };

    return { verified: false };
  } catch {
    return { verified: false };
  }
}

function clampSeedElo(val) {
  const n = parseInt(val || "1000", 10);
  if (isNaN(n)) return 1000;
  return Math.max(850, Math.min(1500, n));
}

async function getOrCreatePlayer(env, steamId, playerName, seedElo = 1000) {
  let row = await env.DB.prepare("SELECT * FROM players WHERE steam_id = ?").bind(steamId).first();
  if (!row) {
    const initElo = clampSeedElo(seedElo);
    const now = Date.now();
    await env.DB.prepare(
      "INSERT INTO players (steam_id, player_name, elo, peak_elo, wins, losses, streak, updated_at) VALUES (?, ?, ?, ?, 0, 0, 0, ?)"
    ).bind(steamId, playerName || "Player", initElo, initElo, now).run();
    row = { steam_id: steamId, player_name: playerName || "Player", elo: initElo, peak_elo: initElo, wins: 0, losses: 0, streak: 0 };
  }
  return row;
}

async function settleEloOnDb(env, inviteCode, winnerId, winnerName, winnerSeedElo, loserId, loserName, loserSeedElo, format, ascent, verifyMethod, now) {
  const winPlayer = await getOrCreatePlayer(env, winnerId, winnerName, winnerSeedElo);
  const losePlayer = await getOrCreatePlayer(env, loserId, loserName, loserSeedElo);

  const mult = getModeMultiplier(format, ascent);
  const winExpected = 1.0 / (1.0 + Math.pow(10.0, (losePlayer.elo - winPlayer.elo) / 400.0));
  const loseExpected = 1.0 - winExpected;

  const winK = (winPlayer.wins + winPlayer.losses) < 10 ? 40 : (winPlayer.elo >= 1650 ? 28 : 36);
  const loseK = (losePlayer.wins + losePlayer.losses) < 10 ? 40 : (losePlayer.elo >= 1650 ? 28 : 36);

  let winDelta = Math.max(1, Math.round(winK * (1.0 - winExpected) * mult));
  let loseDelta = Math.min(-1, Math.round(loseK * (0.0 - loseExpected) * mult));

  const newWinElo = Math.max(100, winPlayer.elo + winDelta);
  const newWinPeak = Math.max(winPlayer.peak_elo, newWinElo);
  const newWinStreak = winPlayer.streak >= 0 ? winPlayer.streak + 1 : 1;

  const newLoseElo = Math.max(100, losePlayer.elo + loseDelta);
  const newLosePeak = Math.max(losePlayer.peak_elo, newLoseElo);
  const newLoseStreak = losePlayer.streak <= 0 ? losePlayer.streak - 1 : -1;

  await env.DB.batch([
    env.DB.prepare(
      "UPDATE players SET player_name = ?, elo = ?, peak_elo = ?, wins = wins + 1, streak = ?, updated_at = ? WHERE steam_id = ?"
    ).bind(winnerName, newWinElo, newWinPeak, newWinStreak, now, winnerId),
    env.DB.prepare(
      "UPDATE players SET player_name = ?, elo = ?, peak_elo = ?, losses = losses + 1, streak = ?, updated_at = ? WHERE steam_id = ?"
    ).bind(loserName, newLoseElo, newLosePeak, newLoseStreak, now, loserId),
    env.DB.prepare(
      "INSERT OR IGNORE INTO settled_matches (invite_code, winner_steam_id, loser_steam_id, winner_delta, loser_delta, verify_method, settled_at) VALUES (?, ?, ?, ?, ?, ?, ?)"
    ).bind(inviteCode, winnerId, loserId, winDelta, loseDelta, verifyMethod, now)
  ]);

  return { winnerElo: newWinElo, loserElo: newLoseElo, winnerDelta: winDelta, loserDelta: loseDelta };
}
