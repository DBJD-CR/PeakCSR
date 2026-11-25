// 对局结果上报 (双方签名共识 + 原版后端交叉验证 + ELO 权威结算)
import { jsonResp, clamp01, matchKey } from "../lib/http.js";
import { sha256Hex16, SHARED_SALT } from "../lib/crypto.js";
import { clampSeedElo } from "../domain/rating.js";
import { getOrCreatePlayer } from "../db/players.js";
import { settleEloOnDb, recordVoidSettlement } from "../db/settlement.js";
import { verifyWithVanillaRaceServer } from "../lib/vanilla.js";

// 分段数组钳制: 最多 16 段, 每段只保留必要字段并做类型转换
// 返回 JSON 字符串; 非法输入返回空串 (表示"本次没有分段可存")
function sanitizeSplitsJson(raw) {
  if (!Array.isArray(raw) || raw.length === 0) return "";
  const out = [];
  const limit = Math.min(raw.length, 16);
  for (let i = 0; i < limit; i++) {
    const it = raw[i] || {};
    const key = String(it.key || "").trim().slice(0, 24).toUpperCase();
    if (!key) continue;
    const segMs = parseInt(it.segMs, 10) || 0;
    const cumMs = parseInt(it.cumMs, 10) || 0;
    if (cumMs <= 0) continue;
    out.push({
      key: key,
      segMs: Math.max(0, segMs),
      cumMs: Math.max(0, cumMs),
      est: it.est === true || it.est === 1,
      reached: it.reached !== false
    });
  }
  if (out.length === 0) return "";
  try {
    return JSON.stringify(out);
  } catch (e) {
    return "";
  }
}

function now0() {
  return Date.now();
}

// 后到上报者的分段补写
async function mergeLateSplits(env, key, settledRow, reporterId, splitsJson, now) {
  if (!splitsJson || String(splitsJson).length <= 2) return;
  const iAmWinner = settledRow.winner_steam_id === reporterId;
  const iAmLoser = settledRow.loser_steam_id === reporterId;
  // 既不是胜者也不是败者: 不属于这一局, 不写
  if (!iAmWinner && !iAmLoser) return;

  const winnerHalf = iAmWinner ? splitsJson : "";
  const loserHalf = iAmLoser ? splitsJson : "";
  try {
    await env.DB.prepare(
      `INSERT INTO match_splits (match_key, winner_splits, loser_splits, version, updated_at)
       VALUES (?, ?, ?, 1, ?)
       ON CONFLICT(match_key) DO UPDATE SET
         winner_splits = CASE WHEN length(excluded.winner_splits) > length(match_splits.winner_splits)
                              THEN excluded.winner_splits ELSE match_splits.winner_splits END,
         loser_splits = CASE WHEN length(excluded.loser_splits) > length(match_splits.loser_splits)
                             THEN excluded.loser_splits ELSE match_splits.loser_splits END,
         version = match_splits.version + 1,
         updated_at = excluded.updated_at`
    ).bind(key, winnerHalf || "[]", loserHalf || "[]", now).run();
  } catch (e) {
    // 补写分段失败不影响结算应答
    console.warn(`[report] 补写后到者分段失败 key=${key}: ${e && e.message ? e.message : e}`);
  }
}

export async function handleMatchReport(body, env) {
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
  const route = Math.max(0, Math.min(11, parseInt(body.route || "0", 10) || 0));
  const durationMs = parseInt(body.durationMs || "0", 10);
  const signature = String(body.signature || "").trim().toLowerCase();
  const isVoid = body.isVoid === true || body.isVoid === 1 || body.isVoid === "1";
  const voidReason = String(body.voidReason || "").trim().slice(0, 64);

  const reporterProgress = clamp01(body.reporterProgress);
  const opponentProgress = clamp01(body.opponentProgress);

  // 分段战报 (可选): 客户端只会上报"我自己"的那半, 对手那半由对手自己上报
  // 服务端只做长度与元素类型钳制, 不参与签名 —— 分段不影响 ELO, 伪造没有收益
  const mySplitsJson = sanitizeSplitsJson(body.mySplits);

  if (!inviteCode || !reporterId || !opponentId || reporterId === opponentId) {
    return jsonResp({ ok: false, error: "Invalid match parameters" }, 400);
  }
  // seed 是 match_key 的组成部分, 缺了它整局指纹就失效, 也会让上游种子校验被跳过
  if (!Number.isFinite(seed) || seed <= 0) {
    return jsonResp({ ok: false, error: "Invalid match parameters" }, 400);
  }
  // 非作废局必须给出胜者, 且胜者只能是这一局的两个参与者
  if (!isVoid && !winnerId) {
    return jsonResp({ ok: false, error: "Invalid match parameters" }, 400);
  }
  if (!isVoid && winnerId !== reporterId && winnerId !== opponentId) {
    return jsonResp({ ok: false, error: "Invalid match parameters" }, 400);
  }

  // 签名校验 (含双方种子分与作废标记, 防止篡改)
  // 字段顺序必须与 src/Ladder/CloudLadderClient.cs 的 rawSigStr 完全一致
  const rawSigStr = `${inviteCode}|${reporterId}|${opponentId}|${winnerId}|${seed}|${ascent}|${format}|${reporterSeedElo}|${opponentSeedElo}|${isVoid ? 1 : 0}|${SHARED_SALT}`;
  const expectedSig = await sha256Hex16(rawSigStr);
  if (signature !== expectedSig) {
    return jsonResp({ ok: false, error: "Signature verification failed" }, 403);
  }

  const key = matchKey(inviteCode, seed);

  // 已结算过 (含 void) 直接回放结果, 避免"再来一局"复用房间号时重复加减分
  const existingSettled = await env.DB.prepare(
    "SELECT * FROM settled_matches WHERE match_key = ?"
  ).bind(key).first();

  if (existingSettled) {
    await mergeLateSplits(env, key, existingSettled, reporterId, mySplitsJson, now0());

    const p = await getOrCreatePlayer(env.DB, reporterId, reporterName, reporterSeedElo);
    const isWinner = reporterId === existingSettled.winner_steam_id;
    return jsonResp({
      ok: true,
      status: "already_settled",
      verifyMethod: existingSettled.verify_method,
      isVoid: !!existingSettled.is_void,
      elo: p.elo,
      delta: existingSettled.is_void ? 0 : (isWinner ? existingSettled.winner_delta : existingSettled.loser_delta)
    }, 200);
  }

  const now = Date.now();

  // 上报方档案先建: 签名已校验, 这一定是本人
  await getOrCreatePlayer(env.DB, reporterId, reporterName, reporterSeedElo);
  if (isVoid) {
    await getOrCreatePlayer(env.DB, opponentId, opponentName, opponentSeedElo);
  }

  await env.DB.prepare(
    `INSERT OR REPLACE INTO match_reports
     (invite_code, reporter_steam_id, opponent_steam_id, reporter_name, opponent_name, winner_steam_id,
      seed, level_index, ascent, format, route, duration_ms, signature,
      reporter_progress, opponent_progress, reporter_splits, is_void, created_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  ).bind(inviteCode, reporterId, opponentId, reporterName, opponentName, winnerId || "",
    seed, levelIndex, ascent, format, route, durationMs, signature,
    reporterProgress, opponentProgress, mySplitsJson || "", isVoid ? 1 : 0, now).run();

  // 作废局优先: 只要有一方申报作废, 本局就按作废落库, 双方都不加减分。
  // 放在胜负验证之前, 是为了让"我这边检测到掉线"能抢在对手误判胜负之前生效。
  if (isVoid) {
    await recordVoidSettlement(env.DB, key, inviteCode, seed, reporterId, opponentId, levelIndex, ascent, format, route,
      durationMs, reporterProgress, opponentProgress, now, "client_void:" + (voidReason || "unknown"));

    // 回读"库里真正的那一行"再应答
    const committed = await env.DB.prepare(
      "SELECT * FROM settled_matches WHERE match_key = ?"
    ).bind(key).first();
    const rowIsVoid = !committed || !!committed.is_void;
    const p = await getOrCreatePlayer(env.DB, reporterId, reporterName, reporterSeedElo);
    const rowIsWinner = !!committed && reporterId === committed.winner_steam_id;
    return jsonResp({
      ok: true,
      status: rowIsVoid ? "settled_void" : "already_settled",
      verifyMethod: committed ? committed.verify_method : "client_void",
      isVoid: rowIsVoid,
      elo: p.elo,
      delta: rowIsVoid ? 0 : (rowIsWinner ? committed.winner_delta : committed.loser_delta)
    }, 200);
  }

  // 验证通道 A: 对手也上报了同一房间同一局, 且 winner 一致
  const oppReport = await env.DB.prepare(
    "SELECT * FROM match_reports WHERE invite_code = ? AND seed = ? AND reporter_steam_id = ?"
  ).bind(inviteCode, seed, opponentId).first();

  let verifiedWinnerId = null;
  let verifyMethod = null;
  let winnerProgress = 0;
  let loserProgress = 0;
  // 上游交叉验证未通过时的具体原因, 会随 pending 响应回给客户端
  let upstreamReason = "";
  // 落库用时必须是"胜者的完赛用时"
  let settledDurationMs = durationMs;
  // 双方分段: 自己的那份来自本次请求, 对手那份来自对手的上报行
  let opponentSplitsJson = "";
  if (oppReport && oppReport.reporter_steam_id === opponentId) {
    // 脏数据不能让这一局永远结算不了: 解析失败就当"对手没存分段"
    let oppSplitsRaw = null;
    if (typeof oppReport.reporter_splits === "string" && oppReport.reporter_splits) {
      try {
        oppSplitsRaw = JSON.parse(oppReport.reporter_splits);
      } catch (e) {
        console.warn(`[report] 对手分段解析失败 key=${key}: ${e && e.message ? e.message : e}`);
        oppSplitsRaw = null;
      }
    }
    opponentSplitsJson = sanitizeSplitsJson(oppSplitsRaw);
  }

  // 共识要求双方报告互指, 且计分相关的赛制参数必须一致
  // seed/route 也必须一致
  const reciprocal = oppReport
    && !oppReport.is_void
    && oppReport.winner_steam_id === winnerId
    && oppReport.opponent_steam_id === reporterId
    && Number(oppReport.format) === format
    && Number(oppReport.ascent) === ascent
    && Number(oppReport.route) === route
    && Number(oppReport.seed) === seed;
  if (reciprocal) {
    verifiedWinnerId = winnerId;
    verifyMethod = "dual_client_consensus";
    if (winnerId === reporterId) {
      winnerProgress = reporterProgress;
      loserProgress = opponentProgress;
      settledDurationMs = durationMs;
    } else {
      winnerProgress = opponentProgress;
      loserProgress = reporterProgress;
      // 本上报方是败者: 用时取对手 (胜者) 那条上报的 durationMs
      settledDurationMs = Number(oppReport.duration_ms) || durationMs;
    }
  } else {
    // 验证通道 B: 向原版 RankedPeak 后端交叉验证
    // 首次没验过时不做"终局判决", 而是留一条可重投的 pending
    //
    // 该通道现在也会返回单方结算结论 (对手 removed / 从赛事中消失且本机已登顶),
    // 依据全部来自上游服务端快照, 客户端无法伪造。method 用来在库里区分来源,
    // 事后可以按 verify_method 筛出所有单方结算的局做人工抽查。
    const upstreamResult = await verifyWithVanillaRaceServer(env, inviteCode, reporterId, opponentId, seed);
    if (upstreamResult.verified && upstreamResult.winnerSteamId === winnerId) {
      verifiedWinnerId = winnerId;
      verifyMethod = upstreamResult.method || "vanilla_raceservice_cross_check";
      // 上报方可能是败方, 进度必须按"谁是胜者"来映射, 不能直接按上报视角塞
      if (winnerId === reporterId) {
        winnerProgress = reporterProgress;
        loserProgress = opponentProgress;
        settledDurationMs = durationMs;
      } else {
        winnerProgress = opponentProgress;
        loserProgress = reporterProgress;
        // 上游交叉验证时不一定能拿到对手上报行, 只能保守沿用本地上报用时
        settledDurationMs = durationMs;
      }
    } else if (upstreamResult && upstreamResult.reason) {
      // 把上游为什么没验过的原因回给客户端, 让线上日志能定位到具体环节
      upstreamReason = upstreamResult.reason;
    }
  }

  // 验证通道 C: 上报方自报失败
  //
  // 这是唯一无需任何外部佐证就能安全采纳的单方结论:
  // winner 不是上报者本人, 意味着上报者主动认输、自愿扣分。
  // 作弊者在自我扣分这件事上没有动机, 所以不需要等对手或上游确认。
  if (!verifiedWinnerId && winnerId && winnerId === opponentId && reporterId !== winnerId) {
    verifiedWinnerId = winnerId;
    verifyMethod = "reporter_self_loss";
    winnerProgress = opponentProgress;
    loserProgress = reporterProgress;
    settledDurationMs = durationMs;
  }

  // 验证通道 D: 上报方自报胜利 (无任何外部佐证)
  //
  // 刻意排在 A/B/C 之后: 能让双端共识或上游核验兜住的, 一律走前面三条,
  // 这样库里 verify_method 能保留最有说服力的那份证据。
  // 只有前面全都拿不到证据时, 才退到这条"信任上报方"的兜底。
  //
  // 取舍说明: 这条放弃了结果真实性校验, 换取"上报必定落地"。
  // 唯一残留的约束来自签名本身 —— inviteCode 与 seed 必须与上报方自述一致,
  // 且 winner 只能是这一局的两个参与者之一。因此它挡不住"自己开房自己打"。
  // 所有走这条的局都会带 verifyMethod = reporter_self_win_unverified,
  // 可用 admin 的 recent 动作按该值筛出来人工抽查, 必要时 void 回滚。
  if (!verifiedWinnerId && winnerId && winnerId === reporterId) {
    verifiedWinnerId = winnerId;
    verifyMethod = "reporter_self_win_unverified";
    winnerProgress = reporterProgress;
    loserProgress = opponentProgress;
    settledDurationMs = durationMs;
  }

  if (!verifiedWinnerId) {
    // retryAfterMs: 客户端补传队列据此安排下次重投
    return jsonResp({
      ok: true,
      status: "pending_opponent_or_upstream",
      retryAfterMs: 45000,
      upstreamReason: upstreamReason || "",
      message: "Report recorded. Waiting for opponent report or upstream confirmation."
    }, 200);
  }

  const loserId = verifiedWinnerId === reporterId ? opponentId : reporterId;
  const winnerName = verifiedWinnerId === reporterId ? reporterName : opponentName;
  const loserName = verifiedWinnerId === reporterId ? opponentName : reporterName;
  const winnerSeedElo = verifiedWinnerId === reporterId ? reporterSeedElo : opponentSeedElo;
  const loserSeedElo = verifiedWinnerId === reporterId ? opponentSeedElo : reporterSeedElo;


  // 上报方是胜者就填 winner_splits, 否则填 loser_splits
  // 另一半来自对手的上报行 (opponentSplitsJson): 这样无论谁先上报, 双方分段都能补齐
  let winnerSplitsJson = verifiedWinnerId === reporterId ? mySplitsJson : opponentSplitsJson;
  let loserSplitsJson = verifiedWinnerId === reporterId ? opponentSplitsJson : mySplitsJson;

  // duration_ms 语义收紧: 只记录"胜者真的登顶完赛"的用时
  const winnerFinished = Number(winnerProgress) >= 0.999;
  if (!winnerFinished) {
    console.log(`[PeakCSR] 胜者未登顶完赛, 丢弃 duration_ms=${settledDurationMs} room=${inviteCode}`);
    settledDurationMs = 0;
  }

  let settlement;
  try {
    settlement = await settleEloOnDb(
      env.DB, key, inviteCode, seed, verifiedWinnerId, winnerName, winnerSeedElo,
      loserId, loserName, loserSeedElo,
      levelIndex, ascent, format, route, settledDurationMs, winnerProgress, loserProgress,
      verifyMethod, now, winnerSplitsJson, loserSplitsJson
    );
  } catch (err) {
    // 并发上报撞在同一 match_key: 唯一键会让整批回滚 (见 settlement.js),
    // 这里回放已落库的那次结算, 保证双方都拿到同一份权威分数
    const settled = await env.DB.prepare(
      "SELECT * FROM settled_matches WHERE match_key = ?"
    ).bind(key).first();
    if (!settled) throw err;
    const p = await getOrCreatePlayer(env.DB, reporterId, reporterName, reporterSeedElo);
    const isWinner = reporterId === settled.winner_steam_id;
    return jsonResp({
      ok: true,
      status: "already_settled",
      verifyMethod: settled.verify_method,
      isVoid: !!settled.is_void,
      elo: p.elo,
      delta: settled.is_void ? 0 : (isWinner ? settled.winner_delta : settled.loser_delta)
    }, 200);
  }

  const myNewElo = reporterId === verifiedWinnerId ? settlement.winnerElo : settlement.loserElo;
  const myDelta = reporterId === verifiedWinnerId ? settlement.winnerDelta : settlement.loserDelta;

  return jsonResp({
    ok: true,
    status: "settled",
    verifyMethod,
    isVoid: false,
    elo: myNewElo,
    delta: myDelta
  }, 200);
}
