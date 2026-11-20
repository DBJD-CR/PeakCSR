// SHA-256 签名校验 (客户端 CloudLadderClient.ComputeSha256Hex16 的对端实现)
// 客户端取哈希前 8 字节转 hex (16 字符), 这里必须一致

// 客户端签名盐：必须与 src/Ladder/CloudLadderClient.cs 中的 SharedCloudSalt 保持一致
export const SHARED_SALT = "PEAKCSR_CLOUD_V1_2026";

export async function sha256Hex16(raw) {
  const buf = new TextEncoder().encode(raw);
  const hash = await crypto.subtle.digest("SHA-256", buf);
  const bytes = new Uint8Array(hash);
  let hex = "";
  for (let i = 0; i < 8; i++) {
    hex += bytes[i].toString(16).padStart(2, "0");
  }
  return hex;
}
