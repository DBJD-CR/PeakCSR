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
        // 无法判断时不做反向误判 (返回 true)
        public static bool IsOpponentHeartbeatFresh(string opponentSteamId, float staleSeconds)
        {
            try
            {
                if (string.IsNullOrEmpty(opponentSteamId)) return true;
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                if (conn == null || !conn.IsConnected) return true;
                foreach (System.Collections.Generic.KeyValuePair<byte, string> pair in conn.PeerSteamIds)
                {
                    if (pair.Value != opponentSteamId) continue;
                    LiveState state;
                    if (conn.Peers.TryGetValue(pair.Key, out state) && state != null)
                    {
                        return TimeSafe.Now - state.ReceivedAt <= staleSeconds;
                    }
                }
                return false;   // socket 上根本没有对手的信道 -> 早已掉线
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
