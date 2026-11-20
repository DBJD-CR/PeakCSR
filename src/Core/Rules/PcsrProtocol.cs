using System;

namespace PeakCSR
{
    // 协议包种类 (Unknown 兼容旧客户端任意扩展命令: 只要带 elo 字段就按同步数据处理)
    public enum PcsrPacketKind
    {
        Syn,
        Ack,
        VoteReq,
        VoteYes,
        VoteNo,
        RerollRoom,
        Void,
        Forfeit,
        // 我方登顶完赛宣告, 携带真实完赛用时。
        // 这是唯一可信的胜负依据: 游戏本体 stats.won 只代表"我到终点了",
        // 不代表"我是第一名", 双方先后点亮篝火时它两边都为 true。
        Peak,
        Unknown
    }

    // 强类型协议包 (wire 格式与旧版逐字节兼容, 新老客户端混飞无感知)
    public struct PcsrPacket
    {
        public PcsrPacketKind Kind;
        // SYN/ACK/Unknown 的 elo 同步字段
        public bool HasElo;
        public int OpponentElo;
        public string PluginVersion;
        public bool HasRatedFlag;
        public bool RatedFlag;
        public McsrRuleFormat Format;
        // VOTE_REQ / VOTE_YES / REROLL_ROOM 载荷
        public int Seed;
        public int LevelIndex;
        public int Route;
        public string RoomCode;
        // PEAK 载荷: 完赛用时 (毫秒), 双方用它做先后判定
        public bool HasPeakMs;
        public long PeakMs;
    }

    // __PCSR1V1| 协议编解码统一收口 (取代散落各处的字符串拼接与 Split('|') 分支链)
    public static class PcsrProtocol
    {
        public const string PacketPrefix = "__PCSR1V1|";

        public static string EncodeSyn(int elo, string version, McsrRuleFormat format, bool rated)
        {
            return PacketPrefix + "SYN|" + elo + "|" + version + "|" + (int)format + "|" + (rated ? "1" : "0");
        }

        public static string EncodeAck(int elo, string version, McsrRuleFormat format, bool rated)
        {
            return PacketPrefix + "ACK|" + elo + "|" + version + "|" + (int)format + "|" + (rated ? "1" : "0");
        }

        public static string EncodeVoteReq(int seed, int levelIndex, int route)
        {
            return PacketPrefix + "VOTE_REQ|" + seed + "|" + levelIndex + "|" + route;
        }

        public static string EncodeVoteYes(int seed, int levelIndex, int route)
        {
            return PacketPrefix + "VOTE_YES|" + seed + "|" + levelIndex + "|" + route;
        }

        public static string EncodeVoteNo()
        {
            return PacketPrefix + "VOTE_NO";
        }

        public static string EncodeRerollRoom(string roomCode, int seed, int levelIndex, int route)
        {
            return PacketPrefix + "REROLL_ROOM|" + roomCode + "|" + seed + "|" + levelIndex + "|" + route;
        }

        // 登顶宣告: 只带用时, 不带名次 (名次由双方用时比较得出, 不信任单方自述)
        public static string EncodePeak(long elapsedMs)
        {
            if (elapsedMs < 0) elapsedMs = 0;
            return PacketPrefix + "PEAK|" + elapsedMs;
        }

        public static string EncodeVoid()
        {
            return PacketPrefix + "VOID";
        }

        public static string EncodeForfeit()
        {
            return PacketPrefix + "FORFEIT";
        }

        // 解码规则与旧 OnLiveChatReceived 分支链严格对齐:
        // 未识别命令但带 elo 字段时返回 Kind=Unknown, 供握手同步逻辑复用
        public static bool TryDecode(string body, out PcsrPacket packet)
        {
            packet = new PcsrPacket();
            packet.Format = McsrRuleFormat.StandardAllPeak;
            if (string.IsNullOrEmpty(body)) return false;
            if (!body.StartsWith(PacketPrefix, StringComparison.Ordinal)) return false;

            string[] parts = body.Split('|');
            if (parts.Length < 2) return false;

            string cmd = parts[1];

            if (cmd == "VOTE_REQ" && parts.Length >= 5)
            {
                packet.Kind = PcsrPacketKind.VoteReq;
                packet.Seed = ParseInt(parts[2]);
                packet.LevelIndex = ParseInt(parts[3]);
                packet.Route = ParseInt(parts[4]);
                return true;
            }
            if (cmd == "VOTE_NO")
            {
                packet.Kind = PcsrPacketKind.VoteNo;
                return true;
            }
            if (cmd == "VOTE_YES" && parts.Length >= 5)
            {
                packet.Kind = PcsrPacketKind.VoteYes;
                packet.Seed = ParseInt(parts[2]);
                packet.LevelIndex = ParseInt(parts[3]);
                packet.Route = ParseInt(parts[4]);
                return true;
            }
            if (cmd == "REROLL_ROOM" && parts.Length >= 6)
            {
                packet.Kind = PcsrPacketKind.RerollRoom;
                packet.RoomCode = SanitizeRoomCode(parts[2]);
                packet.Seed = ParseInt(parts[3]);
                packet.LevelIndex = ParseInt(parts[4]);
                packet.Route = ParseInt(parts[5]);
                return true;
            }
            if (cmd == "PEAK" && parts.Length >= 3)
            {
                packet.Kind = PcsrPacketKind.Peak;
                long ms;
                if (long.TryParse(parts[2], out ms))
                {
                    packet.HasPeakMs = true;
                    packet.PeakMs = ms < 0 ? 0 : ms;
                }
                return true;
            }
            if (cmd == "VOID")
            {
                packet.Kind = PcsrPacketKind.Void;
                return true;
            }
            if (cmd == "FORFEIT")
            {
                packet.Kind = PcsrPacketKind.Forfeit;
                return true;
            }

            // SYN/ACK/未知命令: elo 与 rated 为可选同步字段 (旧协议向后兼容)
            if (parts.Length < 4) return false;
            if (cmd == "SYN") packet.Kind = PcsrPacketKind.Syn;
            else if (cmd == "ACK") packet.Kind = PcsrPacketKind.Ack;
            else packet.Kind = PcsrPacketKind.Unknown;

            int elo;
            if (int.TryParse(parts[2], out elo))
            {
                packet.HasElo = true;
                packet.OpponentElo = elo;
            }
            // 字段 3 是插件版本 (EncodeSyn / EncodeAck 会写), 解码时也要还原, 否则收包侧永远拿不到
            if (!string.IsNullOrEmpty(parts[3]))
            {
                packet.PluginVersion = parts[3];
            }
            if (parts.Length >= 6)
            {
                packet.HasRatedFlag = true;
                packet.RatedFlag = parts[5] != "0";
                int fmtIdx = ParseInt(parts[4]);
                if (fmtIdx < 0) fmtIdx = 0;
                if (fmtIdx > 2) fmtIdx = 2;
                packet.Format = (McsrRuleFormat)fmtIdx;
            }
            return true;
        }

        // 房间码清洗: 只保留字母数字并统一大写
        public static string SanitizeRoomCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return "";
            char[] buf = new char[code.Length];
            int n = 0;
            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                if ((c >= '0' && c <= '9') || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'))
                {
                    buf[n++] = char.ToUpperInvariant(c);
                }
            }
            return new string(buf, 0, n);
        }

        private static int ParseInt(string s)
        {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }
    }
}
