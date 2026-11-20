// 玩家档案: GET 单人档案 / POST 自选偏好与昵称
import { jsonResp, parseMaybeInt } from "../lib/http.js";
import { sha256Hex16, SHARED_SALT } from "../lib/crypto.js";
import { sanitizeNickname, NICK_COOLDOWN_MS } from "../domain/nickname.js";
import { PLAYER_COLUMNS, getOrCreatePlayer } from "../db/players.js";
import { clampSeedElo } from "../domain/rating.js";

export async function handleProfile(url, env) {
  const steamId = (url.searchParams.get("steamId") || "").trim();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400);

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
  }, 200);
}

// 玩家自选档案 (昵称 / 最爱地图 / 最爱难度 / 最爱模式)
// 只允许玩家修改自己的档案, 用签名校验避免任意篡改他人数据
// 玩家还没有战绩时也会自动建档, 这样"起个 PCSR 昵称"不必先打完一局
export async function handleProfileUpdate(body, env) {
  const steamId = String(body.steamId || "").trim();
  const signature = String(body.signature || "").trim().toLowerCase();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400);

  // favoriteMap 现在是群系索引 0~10, 超出范围一律归 -1 (未设置)
  const favoriteMapRaw = parseMaybeInt(body.favoriteMap, -1);
  const favoriteMap = (favoriteMapRaw >= 0 && favoriteMapRaw <= 10) ? favoriteMapRaw : -1;
  const favoriteAscent = parseMaybeInt(body.favoriteAscent, -999);
  const favoriteFormat = parseMaybeInt(body.favoriteFormat, -1);

  // 昵称走白名单过滤, 空串表示"沿用 Steam 昵称"
  const nickname = sanitizeNickname(body.nickname);
  // keepNickname: 客户端只想改自选偏好, 明确要求不要碰昵称
  const keepNickname = body.keepNickname === true || body.keepNickname === 1 || body.keepNickname === "1";

  // 签名顺序必须与 src/Ladder/CloudLadderClient.cs 的 PushProfile 完全一致
  const rawSigStr = `${steamId}|${favoriteMap}|${favoriteAscent}|${favoriteFormat}|${nickname}|${keepNickname ? 1 : 0}|${SHARED_SALT}`;
  const expectedSig = await sha256Hex16(rawSigStr);
  if (signature !== expectedSig) {
    return jsonResp({ ok: false, error: "Signature verification failed" }, 403);
  }

  // 建档: 不存在时直接创建, 让玩家不用先打一局就能拥有档案
  // 初始分必须用客户端上报的种子分 (从原版 RANKED 软折算而来), 不能默认 1000,
  // 否则新建档会把玩家从 RANKED 继承来的基础分直接抹平
  const displayName = String(body.playerName || "Player").trim().slice(0, 32) || "Player";
  const seedElo = clampSeedElo(body.seedElo);
  const player = await getOrCreatePlayer(env.DB, steamId, displayName, seedElo);
  if (!player) return jsonResp({ ok: false, error: "Cannot create player" }, 500);

  const now = Date.now();
  const updates = [];
  const binds = [];

  updates.push("favorite_map = ?", "favorite_ascent = ?", "favorite_format = ?");
  binds.push(favoriteMap, favoriteAscent, favoriteFormat);

  const currentNick = player.nickname || "";
  let nickChanged = false;
  // keepNickname 时整段跳过昵称处理
  if (!keepNickname && nickname !== currentNick) {
    if (nickname !== "") {
      // 冷却只约束"改成新昵称": 首次命名或改名后 24h 内不能再次改名
      const lastChange = player.nickname_updated_at || 0;
      if (currentNick !== "" && lastChange > 0 && now - lastChange < NICK_COOLDOWN_MS) {
        const remainH = Math.ceil((NICK_COOLDOWN_MS - (now - lastChange)) / 3600000);
        return jsonResp({
          ok: false,
          error: "Nickname cooldown",
          cooldownHours: remainH
        }, 429);
      }
      const taken = await env.DB.prepare(
        "SELECT steam_id FROM players WHERE nickname = ? COLLATE NOCASE AND steam_id <> ?"
      ).bind(nickname, steamId).first();
      if (taken) {
        return jsonResp({ ok: false, error: "Nickname already taken" }, 409);
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
      return jsonResp({ ok: false, error: "Nickname already taken" }, 409);
    }
    throw e;
  }

  return jsonResp({
    ok: true,
    status: "updated",
    nickname,
    nicknameChanged: nickChanged
  }, 200);
}
