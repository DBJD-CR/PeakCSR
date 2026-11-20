// CORS 响应与 JSON 工具

export function corsHeaders() {
  return {
    "Access-Control-Allow-Origin": "*",
    "Access-Control-Allow-Methods": "GET, POST, OPTIONS",
    "Access-Control-Allow-Headers": "Content-Type, X-Admin-Token"
  };
}

// CORS 头默认随每个响应下发: 客户端 UnityWebRequest 与管理脚本都依赖它,
// handlers 不需要再各自传 corsHeaders
export function jsonResp(obj, status = 200, extraHeaders = {}) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "Content-Type": "application/json; charset=utf-8", ...corsHeaders(), ...extraHeaders }
  });
}

export function clamp01(v) {
  const n = Number(v);
  if (!isFinite(n)) return 0;
  return Math.max(0, Math.min(1, n));
}

export function matchKey(inviteCode, seed) {
  return inviteCode + "|" + seed;
}

export function parseMaybeInt(v, fallback) {
  const n = parseInt(v, 10);
  return isNaN(n) ? fallback : n;
}
