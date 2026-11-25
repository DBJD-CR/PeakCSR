// 单场战报详情: 战绩历史点击回看时, 本地归档未命中才走这里
//
// 只返回"是否命中 + 双方分段", 基础的胜负/ELO/地图信息由 history 列表已有,
// 这样详情接口的响应体很小, 也避免重复查询 settled_matches 两次。
import { jsonResp } from "../lib/http.js";

export async function handleReportDetail(url, env) {
  const matchKey = (url.searchParams.get("matchKey") || "").trim();
  const steamId = (url.searchParams.get("steamId") || "").trim();
  if (!matchKey) return jsonResp({ ok: false, error: "Missing matchKey" }, 400);
  // steamId 必须提供: 否则任意人凭 matchKey 就能读到他人分段。
  // matchKey 是 "房间号|种子", 对战双方与旁观者都可能知道, 不能当授权凭据。
  if (!steamId) return jsonResp({ ok: false, error: "Missing steamId" }, 400);

  const row = await env.DB.prepare(
    `SELECT match_key, winner_steam_id, loser_steam_id, winner_splits, loser_splits, version, updated_at
     FROM match_splits WHERE match_key = ?`
  ).bind(matchKey).first();

  if (!row) return jsonResp({ ok: true, found: false, splits: [] }, 200);

  // 请求方必须是这一局的参与者
  if (row.winner_steam_id !== steamId && row.loser_steam_id !== steamId) {
    return jsonResp({ ok: false, error: "Forbidden" }, 403);
  }

  const iAmWinner = row.winner_steam_id === steamId;
  const myRaw = iAmWinner ? row.winner_splits : row.loser_splits;
  const oppRaw = iAmWinner ? row.loser_splits : row.winner_splits;

  return jsonResp({
    ok: true,
    found: !!(myRaw && String(myRaw).length > 2),
    // 双方都返回, 回看页可以并列对比
    mySplits: safeParse(myRaw),
    oppSplits: safeParse(oppRaw),
    updatedAt: row.updated_at || 0
  }, 200);
}

function safeParse(raw) {
  if (!raw || String(raw).length <= 2) return [];
  try {
    const arr = JSON.parse(String(raw));
    return Array.isArray(arr) ? arr : [];
  } catch (e) {
    return [];
  }
}
