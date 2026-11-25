// 玩家档案: GET 单人档案 / POST 自选偏好与昵称
import { jsonResp, parseMaybeInt } from "../lib/http.js";
import { sha256Hex16, SHARED_SALT } from "../lib/crypto.js";
import { sanitizeNickname, NICK_COOLDOWN_MS } from "../domain/nickname.js";
import { derivePreferences, normalizeCountry } from "../domain/preferences.js";
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

  // 最常玩难度/模式以 settled_matches 现场推导为准
  const derived = row ? await derivePreferences(env.DB, steamId) : null;
  const profile = row ? withDerivedPreferences(row, derived) : null;

  return jsonResp({
    ok: true,
    refreshedAt: Date.now(),
    profile,
    rank
  }, 200);
}

// 用推导值覆盖 favorite_ascent / favorite_format, 推导不出来时保留原列
function withDerivedPreferences(row, derived) {
  if (!row || !derived) return row;
  const merged = Object.assign({}, row);
  if (derived.favoriteAscent > -900) merged.favorite_ascent = derived.favoriteAscent;
  if (derived.favoriteFormat >= 0) merged.favorite_format = derived.favoriteFormat;
  merged.settled_games = derived.settledGames;
  return merged;
}

// 玩家自选档案 (昵称 / 最爱地图 / 最爱难度 / 最爱模式)
// 只允许玩家修改自己的档案, 用签名校验避免任意篡改他人数据
// 玩家还没有战绩时也会自动建档, 这样"起个 PCSR 昵称"不必先打完一局
export async function handleProfileUpdate(body, env) {
  const steamId = String(body.steamId || "").trim();
  const signature = String(body.signature || "").trim().toLowerCase();
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400);

  // favoriteMap / dislikedMap 都是群系索引 0~10, 超出范围一律归 -1 (未设置)
  const favoriteMapRaw = parseMaybeInt(body.favoriteMap, -1);
  const favoriteMap = (favoriteMapRaw >= 0 && favoriteMapRaw <= 10) ? favoriteMapRaw : -1;
  const dislikedMapRaw = parseMaybeInt(body.dislikedMap, -1);
  let dislikedMap = (dislikedMapRaw >= 0 && dislikedMapRaw <= 10) ? dislikedMapRaw : -1;
  // 最爱与最讨厌不允许是同一个群系
  if (dislikedMap >= 0 && dislikedMap === favoriteMap) dislikedMap = -1;
  const favoriteAscent = parseMaybeInt(body.favoriteAscent, -999);
  const favoriteFormat = parseMaybeInt(body.favoriteFormat, -1);
  // 国籍: 两位大写字母, 走主权归一
  const country = normalizeCountry(body.country);

  // 昵称走白名单过滤, 空串表示"沿用 Steam 昵称"
  const nickname = sanitizeNickname(body.nickname);
  // keepNickname: 客户端只想改自选偏好, 明确要求不要碰昵称
  const keepNickname = body.keepNickname === true || body.keepNickname === 1 || body.keepNickname === "1";

  // 签名顺序必须与 src/Ladder/CloudLadderClient.cs 的 PushProfile 完全一致
  // 四轨并存, 从新到旧依次尝试:
  //   含 country 的最新版 > 含 dislikedMap 不含 country > 不含 dislikedMap > 最早版本
  // 老客户端不会传 country, 落到旧轨时本次不改动该列, 保留云端现值
  const sigStr = `${steamId}|${favoriteMap}|${dislikedMap}|${favoriteAscent}|${favoriteFormat}|${nickname}|${keepNickname ? 1 : 0}|${country}|${SHARED_SALT}`;
  const expectedSig = await sha256Hex16(sigStr);
  const noCountrySigStr = `${steamId}|${favoriteMap}|${dislikedMap}|${favoriteAscent}|${favoriteFormat}|${nickname}|${keepNickname ? 1 : 0}|${SHARED_SALT}`;
  const noCountrySig = await sha256Hex16(noCountrySigStr);
  const midSigStr = `${steamId}|${favoriteMap}|${favoriteAscent}|${favoriteFormat}|${nickname}|${keepNickname ? 1 : 0}|${SHARED_SALT}`;
  const midSig = await sha256Hex16(midSigStr);
  const legacySigStr = `${steamId}|${favoriteMap}|${favoriteAscent}|${favoriteFormat}|${nickname}|${SHARED_SALT}`;
  const legacySig = await sha256Hex16(legacySigStr);

  // legacyClient: 客户端没带 dislikedMap, 本次不写该列
  // legacyCountry: 客户端没带 country, 本次不写该列
  let legacyClient = false;
  let legacyCountry = false;
  if (signature !== expectedSig) {
    if (signature === noCountrySig) {
      legacyCountry = true;
    } else if (signature === midSig) {
      // 旧客户端不带 dislikedMap, 也不带 country
      legacyClient = true;
      legacyCountry = true;
    } else if (signature === legacySig) {
      legacyClient = true;
      legacyCountry = true;
    } else {
      return jsonResp({ ok: false, error: "Signature verification failed" }, 403);
    }
  }
  const effectiveKeepNickname = keepNickname || (legacyClient && nickname === "");

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
  // 旧客户端不传 dislikedMap: 不写该列, 免得把玩家已选的偏好抹成 -1
  if (!legacyClient) {
    updates.push("disliked_map = ?");
    binds.push(dislikedMap);
  }
  // 同理国籍: 老客户端不带该字段时保留云端现值
  if (!legacyCountry) {
    updates.push("country = ?");
    binds.push(country);
  }

  const currentNick = player.nickname || "";
  // 昵称等价性按大小写不敏感判定: 只改大小写不算改名, 不消耗冷却也不必回写
  const nickEquivalent = currentNick.toLowerCase() === nickname.toLowerCase();
  let nickChanged = false;
  // 抢占新昵称: 用单条 UPDATE 的条件子句做原子判定, 避免两次请求同时通过查询
  let claimNickname = false;
  if (!effectiveKeepNickname && !nickEquivalent) {
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
      claimNickname = true;
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

  // 抢占昵称时把占用检查内联进 WHERE: SQLite 单条语句原子执行,
  // "查是否被占"与"写入"不会像先 SELECT 再 UPDATE 那样被并发穿过
  let updateSql = "UPDATE players SET " + updates.join(", ") + " WHERE steam_id = ?";
  let updateBinds = binds;
  if (claimNickname) {
    updateSql += " AND NOT EXISTS (SELECT 1 FROM players p2"
      + " WHERE p2.nickname = ? COLLATE NOCASE AND p2.steam_id <> players.steam_id)";
    updateBinds = binds.concat([nickname]);
  }

  let changed = 0;
  try {
    const res = await env.DB.prepare(updateSql).bind(...updateBinds).run();
    changed = (res && res.meta && res.meta.changes) || 0;
  } catch (e) {
    // 唯一索引兜底: 并发抢占同名昵称
    const msg = String((e && e.message) || e);
    if (msg.indexOf("UNIQUE") >= 0) {
      return jsonResp({ ok: false, error: "Nickname already taken" }, 409);
    }
    throw e;
  }
  // 守卫子句命中: 说明昵称已被别人占用
  if (claimNickname && changed === 0) {
    return jsonResp({ ok: false, error: "Nickname already taken" }, 409);
  }

  return jsonResp({
    ok: true,
    status: "updated",
    nickname,
    nicknameChanged: nickChanged
  }, 200);
}
