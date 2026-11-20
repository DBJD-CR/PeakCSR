// 原版 RANKED 后端交叉验证
// 去上游 followme-peak.ddns.net 查询真实房间状态做二次核验

// 上游 RANKED 后端的 API Key 绝不能提交到公开仓库！
// 部署时用 wrangler secret put UPSTREAM_API_KEY 注入
const UPSTREAM_API = "https://followme-peak.ddns.net";

export async function verifyWithVanillaRaceServer(env, inviteCode, playerA, playerB, expectedSeed) {
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
    // 种子无条件比对
    if (!race || Number(race.seed) !== Number(expectedSeed)) {
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
