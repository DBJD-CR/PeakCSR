// PCSR 1v1 评分与防刷分领域核心 (Cloudflare Worker 端)
// 口径与 src/Core/Rating/* 完全一致: 参数直接读取 shared/rating-rules.json (单一事实源)
// 构建时 esbuild 会把该 JSON 打进 bundle, 改参数后重新 wrangler deploy 即生效
import rules from "../../../shared/rating-rules.json";

// ===== 倍率表 =====
export function getAscentMultiplier(ascent) {
  const a = Math.max(-1, Math.min(8, ascent | 0));
  return a < 0 ? rules.tenderfootMultiplier : (rules.ascentMultipliers[a] || 1.0);
}

// 与客户端 EloEngine.GetModeMultiplier 严格一致
export function getModeMultiplier(format, ascent) {
  const ascentMult = getAscentMultiplier(ascent);
  if (format === 2) {
    return Math.round(rules.biomeSprintFactor * ascentMult * 100) / 100;
  }
  return ascentMult;
}

// 与客户端 EloEngine.GetMinWinDelta 严格一致: 保底是绝对下限, 不参与赛制倍率
export function getMinWinDelta(format, ascent, currentElo) {
  const ascentMult = getAscentMultiplier(ascent);
  const baseMin = format === 2 ? rules.minWinDelta.biomeSprint : rules.minWinDelta.fullPeak;
  const scaled = baseMin * ascentMult;
  let result = format === 2 ? Math.floor(scaled) : Math.round(scaled);
  if (currentElo >= rules.highEloFloor.threshold) {
    result = Math.min(result, rules.highEloFloor.winCap);
  }
  return Math.max(1, result);
}

// 与客户端 EloEngine.GetMaxLossDelta 严格一致
export function getMaxLossDelta(format) {
  return format === 2 ? rules.maxLossDelta.biomeSprint : rules.maxLossDelta.fullPeak;
}

export function getBaseKFactor(totalGames, currentElo) {
  if (totalGames < rules.placementMatches) return rules.kFactors.placement;
  if (currentElo >= rules.highEloFloor.threshold) return rules.kFactors.highElo;
  return rules.kFactors.regular;
}

// 与客户端 EloEngine.ComputeProgressModifier 严格一致:
// antiBoostWeight 在取整前乘入 bonus, 权重 <= 0.01 时直接返回 0 (不施加补偿)
export function computeProgressModifier(actualScore, selfProgress01, oppProgress01, format, ascent, isPlacement, antiBoostWeight = 1.0) {
  const weight = Number(antiBoostWeight);
  if (!isFinite(weight) || weight <= 0.01) return 0;

  const myP = Math.max(0, Math.min(1, Number(selfProgress01) || 0));
  const oppP = Math.max(0, Math.min(1, Number(oppProgress01) || 0));
  const diff = myP - oppP;

  const pc = rules.progressCompensation;
  let scale = format === 2 ? pc.sprintScale : 1.0;
  if (ascent < 0) scale *= pc.tenderfootScale;

  let bonus = 0;
  if (actualScore > 0.75) {
    const leadMax = isPlacement ? pc.leadBonus.placement : pc.leadBonus.regular;
    bonus = Math.max(0, diff) * leadMax * scale;
  } else if (actualScore < 0.25) {
    const completionReliefMax = isPlacement ? pc.completionRelief.placement : pc.completionRelief.regular;
    const completionFactor = Math.max(0, Math.min(1, (myP - pc.completionStart) / pc.completionSpan));
    const relief = completionFactor * completionReliefMax;
    const gapTerm = diff * (isPlacement ? pc.gapTerm.placement : pc.gapTerm.regular);
    bonus = (relief + gapTerm) * scale;
  }

  const maxCap = isPlacement ? pc.cap.placement : pc.cap.regular;
  return Math.max(-maxCap, Math.min(maxCap, Math.round(bonus * weight)));
}

// ===== Anti-Boost (方案 1.2.4 修复: 云端结算补齐与客户端一致的防刷分) =====
// 判定顺序与客户端 AntiBoostPolicy.Evaluate 严格一致:
//   未计分 → 自战 → 分差 → 频次
// freqSource: 查询 settled_matches 中该玩家近 30 分钟与同一对手的交手次数
export async function evaluateAntiBoostWeight(db, winnerId, loserId, winnerElo, loserElo) {
  const ab = rules.antiBoost;

  // 分差减半: 双方分差超过阈值时只给一半权重 (双方各自的判定基于各自视角)
  // 与客户端一致: 谁的视角分差超阈值, 谁就减半
  const winGap = Math.abs(winnerElo - loserElo);
  let winnerWeight = 1.0;
  let loserWeight = 1.0;
  if (winGap > ab.gapThreshold) {
    winnerWeight = ab.gapWeight;
    loserWeight = ab.gapWeight;
  }

  // 同对手频次衰减: 各自查询自己近 30 分钟内与对手的交手次数 (不含本局)
  const [winCount, loseCount] = await Promise.all([
    countRecentEncounters(db, winnerId, loserId, ab.windowMinutes),
    countRecentEncounters(db, loserId, winnerId, ab.windowMinutes)
  ]);

  if (winCount >= ab.maxRecentAgainstSameOpponent) winnerWeight = 0.0;
  else if (winCount === 2) winnerWeight = ab.repeatWeight;
  if (loseCount >= ab.maxRecentAgainstSameOpponent) loserWeight = 0.0;
  else if (loseCount === 2) loserWeight = ab.repeatWeight;

  return { winnerWeight, loserWeight };
}

// 查询 settled_matches: 该玩家作为胜方或败方、对手为 opponentId 的近期结算次数
async function countRecentEncounters(db, selfId, opponentId, windowMinutes) {
  const since = Date.now() - windowMinutes * 60 * 1000;
  const row = await db.prepare(
    `SELECT COUNT(*) AS c FROM settled_matches
     WHERE settled_at >= ?
       AND ((winner_steam_id = ? AND loser_steam_id = ?) OR (winner_steam_id = ? AND loser_steam_id = ?))
       AND is_void = 0`
  ).bind(since, selfId, opponentId, opponentId, selfId).first();
  return (row && row.c) || 0;
}

// ===== ELO 结算 (原 worker.js settleEloOnDb 核心计算抽离, 补齐 Anti-Boost) =====
// 与客户端 EloEngine.ComputeDelta/ComputeProgressModifier 语义一致:
//   weightFactor 只作用于基础分; antiBoostWeight <= 0.01 时不施加保底/上限
export function computeSettlement(winPlayer, losePlayer, format, ascent, progress) {
  const mult = getModeMultiplier(format, ascent);
  const winExpected = 1.0 / (1.0 + Math.pow(10.0, (losePlayer.elo - winPlayer.elo) / rules.elo.scaleDenominator));
  const loseExpected = 1.0 - winExpected;

  const winTotal = (winPlayer.wins || 0) + (winPlayer.losses || 0) + (winPlayer.draws || 0);
  const loseTotal = (losePlayer.wins || 0) + (losePlayer.losses || 0) + (losePlayer.draws || 0);
  const winK = getBaseKFactor(winTotal, winPlayer.elo);
  const loseK = getBaseKFactor(loseTotal, losePlayer.elo);
  const winIsPlacement = winTotal < rules.placementMatches;
  const loseIsPlacement = loseTotal < rules.placementMatches;

  return { mult, winExpected, loseExpected, winTotal, loseTotal, winK, loseK, winIsPlacement, loseIsPlacement };
}

export function clampSeedElo(val) {
  const n = parseInt(val || "1000", 10);
  if (isNaN(n)) return rules.seedElo.vanillaBase;
  return Math.max(rules.seedElo.min, Math.min(rules.seedElo.max, n));
}

export const RULES = rules;
