// PeakCSR (PCSR 1v1) Cloudflare Worker 零成本无服务器天梯后端 (模块化路由分发)
// 支持: 双方客户端 SHA-256 签名共识校验 + 原版 RankedPeak 后端 /ranked/races/{code} 交叉验证
// 评分口径由 shared/rating-rules.json 单一事实源驱动 (与客户端 EloEngine 完全一致),
// 结算含与客户端一致的 Anti-Boost 防刷分 (方案 1.2.4 缺陷修复)
import { corsHeaders, jsonResp } from "./lib/http.js";
import { handleLeaderboard } from "./handlers/leaderboard.js";
import { handleProfile, handleProfileUpdate } from "./handlers/profile.js";
import { handleHistory } from "./handlers/history.js";
import { handleMatchReport } from "./handlers/report.js";
import { handleAdmin } from "./handlers/admin.js";

export default {
  async fetch(request, env) {
    const url = new URL(request.url);
    const cors = corsHeaders();

    if (request.method === "OPTIONS") {
      return new Response(null, { headers: cors });
    }

    try {
      if (url.pathname === "/api/leaderboard" && request.method === "GET") {
        return await handleLeaderboard(url, env);
      }
      if (url.pathname === "/api/profile" && request.method === "GET") {
        return await handleProfile(url, env);
      }
      if (url.pathname === "/api/profile" && request.method === "POST") {
        const body = await request.json();
        return await handleProfileUpdate(body, env);
      }
      if (url.pathname === "/api/history" && request.method === "GET") {
        return await handleHistory(url, env);
      }
      if (url.pathname === "/api/report" && request.method === "POST") {
        const body = await request.json();
        return await handleMatchReport(body, env);
      }
      if (url.pathname === "/api/admin" && request.method === "POST") {
        const body = await request.json();
        return await handleAdmin(request, body, env);
      }
      return jsonResp({ ok: false, error: "Not Found" }, 404, cors);
    } catch (err) {
      return jsonResp({ ok: false, error: err.message || String(err) }, 500, cors);
    }
  }
};
