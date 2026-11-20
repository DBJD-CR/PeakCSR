// 对局结果上报 (双方签名共识 + 原版后端交叉验证 + ELO 权威结算)
import { jsonResp, clamp01, matchKey } from "../lib/http.js";
import { sha256Hex16, SHARED_SALT } from "../lib/crypto.js";
import { clampSeedElo } from "../domain/rating.js";
import { getOrCreatePlayer } from "../db/players.js";
import { settleEloOnDb, recordVoidSettlement } from "../db/settlement.js";
import { verifyWithVanillaRaceServer } from "../lib/vanilla.js";

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

  // 先建双方档案: 作废局的历史行要读双方的"赛前 ELO", 玩家行不存在的话历史会永久记成 0 -> 0
  await getOrCreatePlayer(env.DB, reporterId, reporterName, reporterSeedElo);
  await getOrCreatePlayer(env.DB, opponentId, opponentName, opponentSeedElo);

  await env.DB.prepare(
    `INSERT OR REPLACE INTO match_reports
     (invite_code, reporter_steam_id, opponent_steam_id, reporter_name, opponent_name, winner_steam_id,
      seed, level_index, ascent, format, route, duration_ms, signature,
      reporter_progress, opponent_progress, is_void, created_at)
     VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)`
  ).bind(inviteCode, reporterId, opponentId, reporterName, opponentName, winnerId || "",
    seed, levelIndex, ascent, format, route, durationMs, signature,
    reporterProgress, opponentProgress, isVoid ? 1 : 0, now).run();

  // 作废局优先: 只要有一方申报作废, 本局就按作废落库, 双方都不加减分。
  // 放在胜负验证之前, 是为了让"我这边检测到掉线"能抢在对手误判胜负之前生效。
  if (isVoid) {
    await recordVoidSettlement(env.DB, key, inviteCode, seed, reporterId, opponentId, levelIndex, ascent, format, route,
      durationMs, reporterProgress, opponentProgress, now, "client_void:" + (voidReason || "unknown"));
    const p = await getOrCreatePlayer(env.DB, reporterId, reporterName, reporterSeedElo);
    return jsonResp({
      ok: true,
      status: "settled_void",
      verifyMethod: "client_void",
      isVoid: true,
      elo: p.elo,
      delta: 0
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

  // 共识要求双方报告互指
  const reciprocal = oppReport
    && !oppReport.is_void
    && oppReport.winner_steam_id === winnerId
    && oppReport.opponent_steam_id === reporterId;
  if (reciprocal) {
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
      // 上报方可能是败方, 进度必须按"谁是胜者"来映射, 不能直接按上报视角塞
      if (winnerId === reporterId) {
        winnerProgress = reporterProgress;
        loserProgress = opponentProgress;
      } else {
        winnerProgress = opponentProgress;
        loserProgress = reporterProgress;
      }
    }
  }

  if (!verifiedWinnerId) {
    return jsonResp({
      ok: true,
      status: "pending_opponent_or_upstream",
      message: "Report recorded. Waiting for opponent report or upstream confirmation."
    }, 200);
  }

  const loserId = verifiedWinnerId === reporterId ? opponentId : reporterId;
  const winnerName = verifiedWinnerId === reporterId ? reporterName : opponentName;
  const loserName = verifiedWinnerId === reporterId ? opponentName : reporterName;
  const winnerSeedElo = verifiedWinnerId === reporterId ? reporterSeedElo : opponentSeedElo;
  const loserSeedElo = verifiedWinnerId === reporterId ? opponentSeedElo : reporterSeedElo;

  let settlement;
  try {
    settlement = await settleEloOnDb(
      env.DB, key, inviteCode, seed, verifiedWinnerId, winnerName, winnerSeedElo,
      loserId, loserName, loserSeedElo,
      levelIndex, ascent, format, route, durationMs, winnerProgress, loserProgress,
      verifyMethod, now
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
