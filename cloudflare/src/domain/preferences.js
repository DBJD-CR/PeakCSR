// 从 settled_matches 现场推导"最常玩难度 / 最常玩模式"
//
// 为什么放在服务端:
//   players 表只有 favorite_ascent / favorite_format 两个"结论列", 没有
//   ascent_games / format_games 这类统计原料
//   settled_matches 里本来就有每一局的 ascent / format, 重算完全是免费的,
//   所以以它为准, 让这项数据天然具备云端可恢复性
//
// 口径与客户端保持一致:取场次最多的那一档; 并列时取较小索引

// 天阶难度合法区间: -1 (菜鸟新人) ~ 8, 与 RoomMagicCodec.MinAscent/MaxAscent 对齐
const MIN_ASCENT = -1;
const MAX_ASCENT = 8;
// 赛制下标: 0 全山标准 / 1 高难天阶 / 2 单群系冲刺
const MIN_FORMAT = 0;
const MAX_FORMAT = 2;

// 可选的国籍白名单与客户端 PcsrCountries.All 保持一致
// 新增国家时两边同步改, 否则客户端选得出来、云端却会被清空
export const SUPPORTED_COUNTRIES = [
  "CN",
  "JP", "KR", "SG", "MY", "TH", "VN", "ID", "PH", "IN", "AU", "NZ",
  "US", "CA", "MX",
  "BR", "AR", "CL",
  "GB", "IE", "DE", "FR", "NL", "BE", "CH", "AT", "IT", "ES", "PT",
  "PL", "CZ", "SE", "NO", "DK", "FI", "IS", "UA", "RU",
  "TR", "AE", "SA", "IL", "ZA", "EG", "NG"
];

// 主权归一: 港澳台折回 CN, 非主权实体直接清空。
// 与客户端 PcsrCountries.Normalize 同口径, 保证两边最终落库的值一致。
const SOVEREIGN_ALIAS = {
  TW: "CN",
  HK: "CN",
  MO: "CN",
  UN: "",
  EU: "",
  XK: "",
  AQ: ""
};

const COUNTRY_SET = new Set(SUPPORTED_COUNTRIES);

// 任意输入 -> 合法国家码或空串 (空串 = 未设置)
export function normalizeCountry(raw) {
  if (typeof raw !== "string") return "";
  const code = raw.trim().toUpperCase();
  if (!/^[A-Z]{2}$/.test(code)) return "";
  if (Object.prototype.hasOwnProperty.call(SOVEREIGN_ALIAS, code)) {
    return SOVEREIGN_ALIAS[code];
  }
  return COUNTRY_SET.has(code) ? code : "";
}

// 推导结果: { favoriteAscent, favoriteFormat, settledGames }
// 没有可用对局时返回 -999 / -1 (与 players 表的哨兵值一致)
export async function derivePreferences(db, steamId) {
  const empty = { favoriteAscent: -999, favoriteFormat: -1, settledGames: 0 };
  if (!steamId) return empty;

  let rows;
  try {
    const res = await db.prepare(
      `SELECT ascent, format, COUNT(*) AS games
         FROM settled_matches
        WHERE is_void = 0
          AND (winner_steam_id = ? OR loser_steam_id = ?)
        GROUP BY ascent, format`
    ).bind(steamId, steamId).all();
    rows = (res && res.results) || [];
  } catch (e) {
    // 推导失败绝不能让档案接口 500: 退化成"暂无数据", 客户端仍可自行显示本地值
    return empty;
  }

  const ascentGames = new Map();
  const formatGames = new Map();
  let total = 0;

  for (const r of rows) {
    const games = Number(r.games) || 0;
    if (games <= 0) continue;

    const ascent = Number(r.ascent);
    if (isFinite(ascent) && ascent >= MIN_ASCENT && ascent <= MAX_ASCENT) {
      ascentGames.set(ascent, (ascentGames.get(ascent) || 0) + games);
    }
    const format = Number(r.format);
    if (isFinite(format) && format >= MIN_FORMAT && format <= MAX_FORMAT) {
      formatGames.set(format, (formatGames.get(format) || 0) + games);
    }
    total += games;
  }

  return {
    favoriteAscent: pickMostFrequent(ascentGames, -999),
    favoriteFormat: pickMostFrequent(formatGames, -1),
    settledGames: total
  };
}

// 场次最多者胜出; 并列取较小键 (与客户端"严格大于才替换"的遍历顺序一致)
function pickMostFrequent(counts, fallback) {
  let bestKey = fallback;
  let bestGames = 0;
  const keys = Array.from(counts.keys()).sort((a, b) => a - b);
  for (const key of keys) {
    const games = counts.get(key);
    if (games > bestGames) {
      bestGames = games;
      bestKey = key;
    }
  }
  return bestGames > 0 ? bestKey : fallback;
}
