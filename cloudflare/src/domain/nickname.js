// PCSR 1v1 昵称清洗 (Cloudflare Worker 端)
// 与客户端 src/Core/Profile/NicknamePolicy.cs 保持同一规则:
// 剥 TMP 富文本 → 控制字符/竖线转空格 → 白名单过滤 → 压缩空白 → 长度钳制

const NICK_MAX_LEN = 16;
const NICK_MIN_LEN = 2;
// 昵称修改冷却: 24 小时, 防止频繁改名骚扰他人
export const NICK_COOLDOWN_MS = 24 * 60 * 60 * 1000;

// 与客户端 NicknamePolicy.Sanitize 保持一致
export function sanitizeNickname(raw) {
  if (raw === null || raw === undefined) return "";
  let s = String(raw);
  // 去掉 TMP 富文本标签, 否则可注入 <size> 之类影响他人界面
  s = s.replace(/<\/?(?:align|allcaps|alpha|b|br|color|cspace|font|gradient|i|indent|line-height|line-indent|link|lowercase|margin|mark|material|mspace|nobr|noparse|page|pos|quad|rotate|s|size|smallcaps|space|sprite|strikethrough|style|sub|sup|u|uppercase|voffset|width)(?:[=\s][^<>]*)?>/gi, "");
  // 控制字符与竖线(签名分隔符)统一替换为空格
  s = s.replace(/[\u0000-\u001f\u007f|]/g, " ");
  // 白名单过滤: 字母(含中日韩)/数字/空格/_-·.
  let out = "";
  for (const ch of s) {
    if (/[\p{L}\p{N}]/u.test(ch) || ch === " " || ch === "_" || ch === "-" || ch === "." || ch === "·") {
      out += ch;
    }
  }
  out = out.replace(/\s{2,}/g, " ").trim();
  if (out.length > NICK_MAX_LEN) out = out.slice(0, NICK_MAX_LEN).trim();
  if (out.length > 0 && out.length < NICK_MIN_LEN) return "";
  return out;
}
