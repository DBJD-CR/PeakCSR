using System;
using Photon.Pun;
using Photon.Realtime;
using RankedPeak.Live;

namespace PeakCSR
{
    // 本机连接健康度读取 (Photon 强信号 + RANKED 实时 socket 弱信号)
    // 纯探测无状态: "本局是否连上过" 的记忆由 ConnectivityGuardian 承担
    public static class PhotonConnectivityProbe
    {
        // Photon 是强信号: 游戏本体已经掉线, 对局不可能继续
        public static bool IsPhotonHealthy()
        {
            try
            {
                ClientState state = PhotonNetwork.NetworkClientState;
                bool disconnected = state == ClientState.Disconnected
                    || state == ClientState.PeerCreated
                    || state == ClientState.ConnectingToNameServer;
                return !disconnected && PhotonNetwork.IsConnected;
            }
            catch
            {
                return true;   // Photon 不可用时不参与判断
            }
        }

        // RANKED 实时 socket 是弱信号: 返回 "socket 是否健康", 与 "本局是否连上过" 无关
        public static bool IsLiveSocketHealthy()
        {
            try
            {
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                if (conn != null && conn.IsConnected) return true;
                return true;
            }
            catch
            {
                return true;
            }
        }

        public static bool IsLiveConnected()
        {
            try
            {
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                return conn != null && conn.IsConnected;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsLiveActive()
        {
            try
            {
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                return conn != null && conn.IsActive;
            }
            catch
            {
                return false;
            }
        }

        // 对手实时心跳是否仍在更新 (RANKED 每秒采样一次, 6 秒内必有新帧)
        //
        // 无法判断时一律返回 true (证据不足不做反向误判)。这里的"无法判断"
        // 包含三种情况, 缺一就会把能打完的好局判成掉线:
        //   1) socket 根本没连着
        //   2) socket 连着但本机路由不健康 (IsActive 为假), 说明正在重连
        //   3) socket 连着但 PeerSteamIds 里暂时找不到对手
        //      —— WebSocket 抖动/重连窗口里 peer 表被清空是常态,
        //         "表里没有"不等于"对手已经走了"。

        // 对手实时槽位的三种状态
        //
        // 必须区分 Unknown 与 Absent, 否则无法同时满足两件互相冲突的事:
        //   1) socket 抖动导致 peer 表清空时, 不能判对手掉线 (否则误作废)
        //   2) 对手真的退出游戏、槽位长期消失时, 必须能判掉线 (否则僵死)
        // 解法: 表为空/socket 不可信 -> Unknown (证据不足);
        //       表非空且确实没有对手 -> Absent (真消失);
        //       调用方再用"上次见到时间"对 Absent 做去抖。
        public enum OpponentSlotState
        {
            Unknown,
            Present,
            Absent
        }

        public static OpponentSlotState ProbeOpponentSlot(string opponentSteamId)
        {
            try
            {
                if (string.IsNullOrEmpty(opponentSteamId)) return OpponentSlotState.Unknown;
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                if (conn == null || !conn.IsConnected || !conn.IsActive) return OpponentSlotState.Unknown;
                System.Collections.Generic.IReadOnlyDictionary<byte, string> peers = conn.PeerSteamIds;
                // 表为空 = socket 正在重建路由, 什么都不能断定
                if (peers == null || peers.Count == 0) return OpponentSlotState.Unknown;
                foreach (System.Collections.Generic.KeyValuePair<byte, string> pair in peers)
                {
                    if (pair.Value == opponentSteamId) return OpponentSlotState.Present;
                }
                return OpponentSlotState.Absent;
            }
            catch
            {
                return OpponentSlotState.Unknown;
            }
        }

        public static bool IsOpponentHeartbeatFresh(string opponentSteamId, float staleSeconds)
        {
            try
            {
                if (string.IsNullOrEmpty(opponentSteamId)) return true;
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                if (conn == null || !conn.IsConnected) return true;
                // socket 正在重连: 此刻的 peer 表不可信
                if (!conn.IsActive) return true;

                OpponentSlotState slot = ProbeOpponentSlot(opponentSteamId);
                // 表为空/socket 重建中: 证据不足, 不能判掉线
                if (slot == OpponentSlotState.Unknown) return true;

                System.Collections.Generic.IReadOnlyDictionary<byte, string> peers = conn.PeerSteamIds;
                foreach (System.Collections.Generic.KeyValuePair<byte, string> pair in peers)
                {
                    if (pair.Value != opponentSteamId) continue;
                    LiveState state;
                    if (conn.Peers.TryGetValue(pair.Key, out state) && state != null)
                    {
                        return TimeSafe.Now - state.ReceivedAt <= staleSeconds;
                    }
                    // 找到了对手的槽位但还没有任何一帧: 同样属于证据不足
                    return true;
                }
                // 表非空但确实没有对手槽位: 这是"真消失"的硬证据, 返回不新鲜
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    // realtimeSinceStartup 的纯包装 (Core 逻辑需要时间源但不宜直连 UnityEngine)
    internal static class TimeSafe
    {
        public static float Now
        {
            get { return UnityEngine.Time.realtimeSinceStartup; }
        }
    }
}
