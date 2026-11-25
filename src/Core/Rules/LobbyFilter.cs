using System;
using System.Collections.Generic;
using RankedPeak.Services;

namespace PeakCSR
{
    // 大厅房间过滤与双房收敛的纯函数集合 (无状态, 同输入必同输出)
    // 注: RaceLobbyEntry 是 RANKED 服务端快照的纯数据类型, 不依赖 UnityEngine
    public static class LobbyFilter
    {
        // PCSR 专属可加入房间的通用形态: 2 人房且当前 1 人, 关闭复活/幽灵, 盲打种子
        public static bool IsJoinableCsrWaitingRoom(RaceLobbyEntry e)
        {
            if (e == null) return false;
            return e.MaxPlayers == 2 && e.Players == 1 && !e.ReviveDead && !e.GhostsVisible && e.SecretSeed;
        }

        // 排队主轮询的房间匹配判定
        // lastOwnedRaceCode: 自己刚作废的房间在服务端可能还挂着, 绝不能再把自己送回去
        public static bool MatchesQueue(RaceLobbyEntry item, McsrRuleFormat wantFormat, int wantAscent, string lastOwnedRaceCode)
        {
            if (item == null) return false;
            if (!item.IsLobby || item.IsFull || !item.Compatible) return false;
            // 只接受仍在 lobby 的房间: 已开赛/加载中的房间是别人的进行时,
            // 掉线残留的公开房正是靠这一条挡在门外
            if (item.Status != "lobby") return false;
            if (item.MaxPlayers != 2 || item.Players != 1) return false;
            if (item.ReviveDead || item.GhostsVisible || !item.SecretSeed) return false;
            if (string.IsNullOrEmpty(item.InviteCode)) return false;
            if (!string.IsNullOrEmpty(lastOwnedRaceCode)
                && string.Equals(item.InviteCode, lastOwnedRaceCode, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (item.AutoEndMs == RoomMagicCodec.GetMagicAutoEndMs(wantFormat, wantAscent)) return true;
            if (RoomMagicCodec.IsCsrMagicMs(item.AutoEndMs)) return false;

            McsrRuleFormat fmt;
            int asc;
            if (!RoomMagicCodec.TryDecodeRuleset(item.Ascent, item.Route, out fmt, out asc)) return false;
            return fmt == wantFormat && asc == wantAscent;
        }

        // 双房收敛选房规则 (保证任意两台机器计算结果一致, 不依赖本地 SteamId 之外的时序):
        //   1) 优先选"非我、赛制完全一致、1/2 人可以加入"的房间
        //   2) 若双方都是房主, 用房号字典序决定谁搬家: 房号更大的主动迁移到更小的
        //      (房间号由服务端生成, 随机性足够, 双方看到同一对房号时结论必然一致)
        public static RaceLobbyEntry FindConvergeTarget(
            List<RaceLobbyEntry> entries, string myRoomCode, McsrRuleFormat wantFormat, int wantAscent)
        {
            if (entries == null) return null;

            RaceLobbyEntry best = null;
            for (int i = 0; i < entries.Count; i++)
            {
                RaceLobbyEntry e = entries[i];
                if (e == null) continue;
                if (string.Equals(e.InviteCode, myRoomCode, StringComparison.OrdinalIgnoreCase)) continue;
                if (!RoomMagicCodec.IsCsrMagicMs(e.AutoEndMs)) continue;
                if (e.AutoEndMs != RoomMagicCodec.GetMagicAutoEndMs(wantFormat, wantAscent)) continue;
                if (!e.Compatible) continue;
                if (e.Status != "lobby" || e.IsFull) continue;
                if (e.MaxPlayers != 2 || e.Players != 1) continue;
                if (e.ReviveDead || e.GhostsVisible || !e.SecretSeed) continue;

                // 谁搬家: 房号字典序更大的一方迁移, 双方结论一致
                bool myRoomKnown = !string.IsNullOrEmpty(myRoomCode);
                if (myRoomKnown && string.Compare(myRoomCode, e.InviteCode, StringComparison.OrdinalIgnoreCase) <= 0)
                {
                    continue;   // 我的房号更小, 我不动
                }

                if (best == null
                    || string.Compare(e.InviteCode, best.InviteCode, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    best = e;
                }
            }
            return best;
        }

        // 在大厅列表里按房号查找
        public static RaceLobbyEntry FindByCode(List<RaceLobbyEntry> entries, string code)
        {
            if (entries == null || string.IsNullOrEmpty(code)) return null;
            for (int i = 0; i < entries.Count; i++)
            {
                RaceLobbyEntry e = entries[i];
                if (e == null) continue;
                if (string.Equals(e.InviteCode, code, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }
    }
}
