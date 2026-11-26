// settled_matches 表操作封装 (含 ELO 权威结算与作废落库)
// 评分计算由 domain/rating.js 提供, 这里只做 DB 编排

import {
  getModeMultiplier, getMinWinDelta,
  computeProgressModifier, computeSettlement, evaluateAntiBoostWeight,
  RULES
} from "../domain/rating.js";
import { modeColumns } from "./players.js";

// 零和 ELO 权威结算 (口径与客户端完全一致, 并执行与客户端一致的 Anti-Boost)
// 修复方案 1.2.4 缺陷: 云端结算此前完全没有 Anti-Boost, 本地算完 0 分后 3 秒被权威分覆盖回全额加分
export async function settleEloOnDb(db, key, inviteCode, seed, winnerId, winnerName, winnerSeedElo,
  loserId, loserName, loserSeedElo, levelIndex, ascent, format, route, durationMs,
  winnerProgress, loserProgress, verifyMethod, now, winnerSplitsJson, loserSplitsJson) {
  const winPlayer = await getOrCreate(db, winnerId, winnerName, winnerSeedElo);
  const losePlayer = await getOrCreate(db, loserId, loserName, loserSeedElo);

  const s = computeSettlement(winPlayer, losePlayer, format, ascent, {});
  const mult = s.mult;
  const winExpected = s.winExpected;
  const loseExpected = s.loseExpected;
  const winK = s.winK;
  const loseK = s.loseK;
  const winIsPlacement = s.winIsPlacement;
  const loseIsPlacement = s.loseIsPlacement;

  // ===== Anti-Boost (与客户端 EvaluateAntiBoostFactor 完全一致) =====
  const weights = await evaluateAntiBoostWeight(db, winnerId, loserId, winPlayer.elo, losePlayer.elo);
  const winWeight = weights.winnerWeight;
  const loseWeight = weights.loserWeight;
  const winModeMult = mult;
  const loseModeMult = mult;

  // 保底是绝对下限, 不参与赛制倍率, 且只对胜方生效
  const winMin = getMinWinDelta(format, ascent, winPlayer.elo);

  let winBase = Math.round(winK * (1.0 - winExpected) * (winWeight * winModeMult));
  let loseBase = Math.round(loseK * (0.0 - loseExpected) * (loseWeight * loseModeMult));
  if (winWeight > 0.01) winBase = Math.max(winMin, winBase);
  if (loseWeight > 0.01 && loseBase >= 0) loseBase = -1;

  // 进度补偿 (与客户端 ComputeProgressModifier 一致, 受 Anti-Boost 权重作用)
  const winBonus = winWeight > 0.01
    ? computeProgressModifier(1.0, winnerProgress, loserProgress, format, ascent, winIsPlacement, winWeight)
    : 0;
  const loseBonus = loseWeight > 0.01
    ? computeProgressModifier(0.0, loserProgress, winnerProgress, format, ascent, loseIsPlacement, loseWeight)
    : 0;

  let winDelta = winBase + winBonus;
  let loseDelta = loseBase + loseBonus;

  // 与客户端 ComputeDelta 一致: 败方没有任何扣分兜底, 只禁止"输了反而加分"
  if (winWeight > 0.01) winDelta = Math.max(winMin, winDelta);
  if (loseWeight > 0.01) loseDelta = Math.min(0, loseDelta);

  // 分数钳制必须走规则
  const clampMin = RULES.elo.clampMin;
  const clampMax = RULES.elo.clampMax;
  const clampElo = (v) => Math.max(clampMin, Math.min(clampMax, v));

  const newWinElo = clampElo(winPlayer.elo + winDelta);
  const newWinPeak = Math.max(winPlayer.peak_elo, newWinElo);
  const newWinStreak = winPlayer.streak >= 0 ? winPlayer.streak + 1 : 1;
  // 历史最长连胜: 只增不减, 由胜方这一局的连胜推进决定
  const newWinBestStreak = Math.max(winPlayer.best_streak || 0, newWinStreak);

  const newLoseElo = clampElo(losePlayer.elo + loseDelta);
  const newLosePeak = Math.max(losePlayer.peak_elo, newLoseElo);
  const newLoseStreak = losePlayer.streak <= 0 ? losePlayer.streak - 1 : -1;

  // 落库要用"实际发生的变化"
  const winAppliedDelta = newWinElo - winPlayer.elo;
  const loseAppliedDelta = newLoseElo - losePlayer.elo;

  const winModeCols = modeColumns(format, "wins");
  const loseModeCols = modeColumns(format, "losses");

  // 历史行放在批次最前且不使用 OR IGNORE
  const batch = [
    db.prepare(
      `INSERT INTO settled_matches
       (match_key, invite_code, seed, winner_steam_id, loser_steam_id, winner_delta, loser_delta,
        win_elo_before, win_elo_after, lose_elo_before, lose_elo_after,
        level_index, ascent, format, route, duration_ms, winner_progress, loser_progress,
        verify_method, is_void, settled_at)
       VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 0, ?)`
    ).bind(key, inviteCode, seed, winnerId, loserId, winAppliedDelta, loseAppliedDelta,
      winPlayer.elo, newWinElo, losePlayer.elo, newLoseElo,
      levelIndex, ascent, format, route, durationMs, winnerProgress, loserProgress,
      verifyMethod, now),
    db.prepare(
      `UPDATE players SET player_name = ?, elo = ?, peak_elo = ?, wins = wins + 1, streak = ?, best_streak = ?,
        ${winModeCols}, updated_at = ? WHERE steam_id = ?`
    ).bind(winnerName, newWinElo, newWinPeak, newWinStreak, newWinBestStreak, now, winnerId),
    db.prepare(
      `UPDATE players SET player_name = ?, elo = ?, peak_elo = ?, losses = losses + 1, streak = ?,
        ${loseModeCols}, updated_at = ? WHERE steam_id = ?`
    ).bind(loserName, newLoseElo, newLosePeak, newLoseStreak, now, loserId)
  ];

  // 战报分段: 双方各写各的那半, 合并语义是"更完整的覆盖更短的"
  //
  // 为什么用 length 比较而不是简单"非空即覆盖":
  //   重传/补传可能带来更短的分段 (例如中途断线只记了 3 段),
  //   简单非空覆盖会把先前完整的 8 段冲掉, 回看时就少了一半赛段。
  if (winnerSplitsJson || loserSplitsJson) {
    batch.push(db.prepare(
      `INSERT INTO match_splits (match_key, winner_splits, loser_splits, version, updated_at)
       VALUES (?, ?, ?, 1, ?)
       ON CONFLICT(match_key) DO UPDATE SET
         winner_splits = CASE WHEN length(excluded.winner_splits) > length(match_splits.winner_splits)
                              THEN excluded.winner_splits ELSE match_splits.winner_splits END,
         loser_splits = CASE WHEN length(excluded.loser_splits) > length(match_splits.loser_splits)
                             THEN excluded.loser_splits ELSE match_splits.loser_splits END,
         version = match_splits.version + 1,
         updated_at = excluded.updated_at`
    ).bind(key, winnerSplitsJson || "[]", loserSplitsJson || "[]", now));
  }

  await db.batch(batch);

  return {
    winnerElo: newWinElo, loserElo: newLoseElo,
    winnerDelta: winAppliedDelta, loserDelta: loseAppliedDelta
  };
}

export async function recordVoidSettlement(db, key, inviteCode, seed, reporterId, opponentId, levelIndex, ascent, format, route,
  durationMs, reporterProgress, opponentProgress, now, verifyMethod) {
  // 作废局也要记录双方当时的 ELO, 否则历史面板会显示 0->0 这种无意义的分数
  const before = [];
  for (const id of [reporterId, opponentId]) {
    const p = await db.prepare("SELECT elo FROM players WHERE steam_id = ?").bind(id).first();
    before.push(p && p.elo ? p.elo : 0);
  }
  // 这里保持 OR IGNORE: 作废是"只记一笔、不加分"的语义, 重复申报不需要报错
  await db.prepare(
    `INSERT OR IGNORE INTO settled_matches
     (match_key, invite_code, seed, winner_steam_id, loser_steam_id, winner_delta, loser_delta,
      win_elo_before, win_elo_after, lose_elo_before, lose_elo_after,
      level_index, ascent, format, route, duration_ms, winner_progress, loser_progress,
      verify_method, is_void, settled_at)
     VALUES (?, ?, ?, ?, ?, 0, 0, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, 1, ?)`
  ).bind(key, inviteCode, seed, reporterId, opponentId,
    before[0], before[0], before[1], before[1],
    levelIndex, ascent, format, route, durationMs, reporterProgress, opponentProgress,
    verifyMethod, now).run();
}

import { getOrCreatePlayer } from "./players.js";
function getOrCreate(db, id, name, seed) {
  return getOrCreatePlayer(db, id, name, seed);
}
