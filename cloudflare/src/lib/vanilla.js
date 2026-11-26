// 原版 RANKED 后端交叉验证
// 去上游 followme-peak.ddns.net 查询真实房间状态做二次核验

// 上游 RANKED 后端的 API Key 绝不能提交到公开仓库！
// 部署时用 wrangler secret put UPSTREAM_API_KEY 注入
const UPSTREAM_API = "https://followme-peak.ddns.net";

// 上游房态快照的短暂缓存: 同一房间在同一轮上报里会被双方各查一次,
// 缓存 15 秒能把这类重复请求减半, 又不会让状态过旧到影响判定
const CACHE_TTL_MS = 15000;
const _cache = new Map();

// 每类诊断只打一次, 避免高并发时把 tail 刷满
const _warned = new Set();
function warnOnce(tag, message) {
  if (_warned.has(tag)) return;
  _warned.add(tag);
  console.warn(`[vanilla] ${tag}: ${message}`);
}

// 返回 { data, reason }: reason 用于把失败原因透传到客户端日志
async function fetchRaceState(env, inviteCode) {
  const key = String(inviteCode || "").toUpperCase();
  const now = Date.now();
  const hit = _cache.get(key);
  if (hit && now - hit.at < CACHE_TTL_MS) return { data: hit.data, reason: null };

  const upstreamKey = env && env.UPSTREAM_API_KEY;
  if (!upstreamKey) {
    warnOnce("missing-key", "UPSTREAM_API_KEY 未配置，原版交叉验证通道已禁用（单方结算同样无法生效）");
    return { data: null, reason: "missing-upstream-key" };
  }

  try {
    const resp = await fetch(`${UPSTREAM_API}/ranked/races/${encodeURIComponent(key)}`, {
      method: "GET",
      headers: { "X-API-Key": upstreamKey }
    });
    if (!resp.ok) {
      // 401/403 说明 Key 失效或上游轮换了鉴权, 必须显式暴露
      if (resp.status === 401 || resp.status === 403) {
        warnOnce("auth-failed", `上游拒绝鉴权 (HTTP ${resp.status})，请检查 UPSTREAM_API_KEY 是否仍然有效`);
      }
      return { data: null, reason: `upstream-http-${resp.status}` };
    }

    const data = await resp.json();
    if (!data || !data.race) return { data: null, reason: "upstream-no-race" };

    // 只缓存成功响应: 失败必须让下一次调用真的重试, 不能被缓存放大成"永远查不到"
    _cache.set(key, { at: now, data });
    if (_cache.size > 200) {
      // 极简淘汰: 上限内够用, 不值得为此引入 LRU
      const oldest = _cache.keys().next();
      if (!oldest.done) _cache.delete(oldest.value);
    }
    return { data, reason: null };
  } catch (err) {
    warnOnce("fetch-failed", `上游请求异常: ${err && err.message ? err.message : err}`);
    return { data: null, reason: "upstream-fetch-error" };
  }
}

export async function verifyWithVanillaRaceServer(env, inviteCode, playerA, playerB, expectedSeed) {
  const state = await fetchRaceState(env, inviteCode);
  if (!state || !state.data) {
    return { verified: false, reason: (state && state.reason) || "upstream-unavailable" };
  }
  const data = state.data;

  const race = data.race;
  const participants = data.participants || [];
  if (!race) return { verified: false, reason: "upstream-no-race" };

  // 盲种房: 比对只在可读阶段进行
  const seedReadable = race.status && race.status !== "lobby";
  if (seedReadable && Number(race.seed) !== Number(expectedSeed)) {
    return { verified: false, reason: `seed-mismatch(${race.seed}!=${expectedSeed})` };
  }

  const pA = participants.find(p => p.steamId === playerA);
  const pB = participants.find(p => p.steamId === playerB);

  // 上报方自己必须仍在赛事里, 否则我们连"谁在提问"都无法确认
  if (!pA) return { verified: false, reason: "reporter-not-in-race" };

  // 带 removed 标记的参与者 = 已离开赛事, 不是竞技结果
  if (pA.removed) return { verified: false, reason: "reporter-removed" };

  // 情况1：一方真实登顶完赛且 Rank === 1 (唯一可判胜负的强证据)
  if (pB && pA.state === "finished" && pA.rank === 1 && pB.state !== "finished") {
    return { verified: true, winnerSteamId: playerA };
  }
  if (pB && pB.state === "finished" && pB.rank === 1 && pA.state !== "finished") {
    return { verified: true, winnerSteamId: playerB };
  }

  // 情况2：一方确实死亡 (deaths > 0) 且状态 dnf, 另一方存活
  // 注意: 单纯 dnf 而没有 deaths 记录无法区分"死亡"与"掉线", 必须要求 deaths > 0
  if (pB && pB.state === "dnf" && pB.deaths > 0 && pA.state !== "dnf") {
    return { verified: true, winnerSteamId: playerA };
  }
  if (pA.state === "dnf" && pA.deaths > 0 && pB && pB.state !== "dnf") {
    return { verified: true, winnerSteamId: playerB };
  }

  // 情况3 (单方结算通道): 对手已从赛事中离开, 而本机确实登顶完赛。
  //
  // 这是唯一允许"只凭一方上报就判定胜利"的通道, 依据全部来自上游服务端的快照,
  // 客户端无法伪造: removed 由服务端在对手离开/被踢/掉线超时后写入,
  // participants 里查不到对手同样只能是服务端行为。
  if (pA.state === "finished") {
    const leftRace = !pB || pB.removed === true;
    const notInLobby = race.status && race.status !== "lobby";
    if (leftRace && notInLobby) {
      const method = pB ? "opponent_removed" : "opponent_absent";
      // 单方结算留痕: 这是唯一绕过双端共识的通道, 不打印就无法事后追溯
      console.log(`[vanilla] 单方结算 ${method} room=${inviteCode} winner=${playerA} status=${race.status}`);
      return {
        verified: true,
        winnerSteamId: playerA,
        method
      };
    }
  }

  // 走到这里说明上游快照存在但没有任何一条规则命中。
  // 把 A/B 双方真实状态回传, 客户端日志里就能直接看出卡在哪。
  return {
    verified: false,
    reason: `no-rule-matched(A=${pA.state},B=${pB ? pB.state : "absent"},status=${race.status})`
  };
}
