using System;
using System.Collections.Generic;
using System.Reflection;
using RankedPeak.Live;
using RankedPeak.Models;
using RankedPeak.Services;
using RankedPeak.Timer;
using RankedPeak.Ui;
using UnityEngine;

namespace PeakCSR
{
    // RankedPeak 外部依赖的具体网关封装 (原 MatchController 里对 RaceService/RaceLiveConnection/RaceTimer
    // 的直接散调全部收口于此; 聊天框系统消息注入也迁移到这里, 业务层不再反向依赖补丁类)
    public sealed class RankedPeakGateway : IRankedPeakGateway
    {
        private const float OpponentHeartbeatStaleSeconds = 6f;

        private MethodInfo _onChatEntryMethod;

        public bool ServiceAvailable
        {
            get { return RaceService.Instance != null; }
        }

        public bool IsHost
        {
            get
            {
                RaceService svc = RaceService.Instance;
                return svc != null && svc.IsHost;
            }
        }

        public bool InRace
        {
            get
            {
                RaceService svc = RaceService.Instance;
                return svc != null && svc.InRace;
            }
        }

        public bool RaceIsOver
        {
            get
            {
                RaceService svc = RaceService.Instance;
                return svc != null && svc.RaceIsOver;
            }
        }

        public RaceInfo CurrentRace
        {
            get
            {
                RaceService svc = RaceService.Instance;
                return svc != null ? svc.CurrentRace : null;
            }
        }

        public RaceInfo LastFinishedRace
        {
            get
            {
                RaceService svc = RaceService.Instance;
                return svc != null ? svc.LastFinishedRace : null;
            }
        }

        public long? MsUntilStart
        {
            get
            {
                RaceService svc = RaceService.Instance;
                return svc != null ? svc.MsUntilStart : null;
            }
        }

        public RaceParticipant Self
        {
            get
            {
                RaceService svc = RaceService.Instance;
                return svc != null ? svc.Self : null;
            }
        }

        public string GameVersion
        {
            get
            {
                try { return MapCatalog.GameVersion ?? ""; } catch { return ""; }
            }
        }

        public string MapSetHash
        {
            get
            {
                try { return MapCatalog.MapSetHash ?? ""; } catch { return ""; }
            }
        }

        public string MySteamId
        {
            get
            {
                try { return SteamIdentity.GetSteamId64() ?? ""; } catch { return ""; }
            }
        }

        public string VanillaPlayerName
        {
            get
            {
                try { return PlayerNameSanitizer.Current("") ?? ""; } catch { return ""; }
            }
        }

        public bool LiveConnected
        {
            get { return PhotonConnectivityProbe.IsLiveConnected(); }
        }

        public bool LiveActive
        {
            get { return PhotonConnectivityProbe.IsLiveActive(); }
        }

        public TimerState TimerState
        {
            get
            {
                try { return RaceTimer.Instance != null ? RaceTimer.Instance.State : TimerState.Idle; }
                catch { return TimerState.Idle; }
            }
        }

        public long TimerElapsedMs
        {
            get
            {
                try { return RaceTimer.Instance != null ? RaceTimer.Instance.CurrentElapsedMs : 0L; }
                catch { return 0L; }
            }
        }

        public event Action<RaceInfo> RaceUpdated
        {
            add { RaceService.RaceUpdated += value; }
            remove { RaceService.RaceUpdated -= value; }
        }

        public event Action<ChatEntry> ChatEntryReceived
        {
            add { RaceLiveConnection.ChatEntryReceived += value; }
            remove { RaceLiveConnection.ChatEntryReceived -= value; }
        }

        public void FetchLobbies(Action<List<RaceLobbyEntry>, int> onDone, Action<string> onError)
        {
            RaceService.Instance.FetchLobbies(onDone, onError);
        }

        public void CreateRace(int seed, int levelIndex, int ascent, int route, bool isPublic,
            Action<RaceInfo> onSuccess, Action<string> onError)
        {
            RaceRules rules = new RaceRules();
            rules.ReviveDead = false;
            RaceService.Instance.CreateRace(
                seed, levelIndex, ascent, route, 2,
                false, false, true, true,
                RoomMagicCodec.GetMagicAutoEndMs(ResolveCoordinatorFormat(), ascent),
                rules, false, isPublic, onSuccess, onError);
        }

        public void UpdateRaceConfig(int seed, int levelIndex, int ascent, int route, bool isPublic, Action<string> onError)
        {
            RaceRules rules = new RaceRules { ReviveDead = false };
            RaceService.Instance.UpdateRaceConfig(
                seed, levelIndex, ascent, route, 2,
                false, false, true, true,
                RoomMagicCodec.GetMagicAutoEndMs(ResolveCoordinatorFormat(), ascent),
                rules, false, isPublic, onError);
        }

        private McsrRuleFormat ResolveCoordinatorFormat()
        {
            Func<McsrRuleFormat> f = CoordinatorFormat;
            if (f != null)
            {
                try { return f(); } catch { }
            }
            return McsrRuleFormat.StandardAllPeak;
        }

        // 当前会话赛制由协调器注入 (Gateway 编码房间魔数时取用)
        public Func<McsrRuleFormat> CoordinatorFormat { get; set; }

        public void JoinRace(string code, Action<RaceInfo> onSuccess, Action<string> onError)
        {
            RaceService.Instance.JoinRace(code, onSuccess, onError);
        }

        public void LeaveRace()
        {
            try { RaceService.Instance.LeaveRace(); } catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] LeaveRace 异常: " + ex.Message);
            }
        }

        public void StartRace(Action<string> onError)
        {
            RaceService.Instance.StartRace(onError);
        }

        public void SetPublic(bool isPublic, Action<string> onError)
        {
            RaceService.Instance.SetPublic(isPublic, onError);
        }

        public void RemoveParticipant(string steamId, string name, Action<string> onError)
        {
            RaceService.Instance.RemoveParticipant(steamId, name, false, onError);
        }

        public void Forfeit()
        {
            RaceService.Instance.Forfeit();
        }

        public void SendChat(string text)
        {
            RaceLiveConnection conn = RaceLiveConnection.Instance;
            if (conn == null || !conn.IsConnected) return;
            conn.SendChat(text);
        }

        // 左下角聊天框注入高亮系统提示 (经反射调用 LiveChatOverlay.OnEntry)
        // 原实现在 SilentHandshakeChatPatch.PushSystemChatMessage, 迁移到网关后业务层不再依赖补丁类
        public void PushSystemChat(string richTextBody)
        {
            if (string.IsNullOrEmpty(richTextBody)) return;
            try
            {
                LiveChatOverlay overlay = UnityEngine.Object.FindAnyObjectByType<LiveChatOverlay>();
                if (overlay == null) return;
                if (_onChatEntryMethod == null)
                {
                    _onChatEntryMethod = typeof(LiveChatOverlay).GetMethod("OnEntry", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                }
                if (_onChatEntryMethod != null)
                {
                    ChatEntry sysEntry = new ChatEntry
                    {
                        Kind = ChatEntryKind.System,
                        Body = richTextBody,
                        ReceivedAt = Time.realtimeSinceStartup,
                        Seen = false
                    };
                    _onChatEntryMethod.Invoke(overlay, new object[] { sysEntry });
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 推送聊天框系统消息失败: " + ex.Message);
            }
        }

        public string GetBiomeLabel(int levelIndex)
        {
            try { return MapCatalog.GetBiomeLabel(levelIndex) ?? ""; } catch { return ""; }
        }

        public string GetSegmentBiomeName(int levelIndex, int segmentIndex)
        {
            try { return MapCatalog.SegmentBiomeName(levelIndex, segmentIndex) ?? ""; } catch { return ""; }
        }

        // 随机挑选一个非重复的真实地图槽位, 全部失败则回退当前槽位
        public int PickRandomAuthenticLevel(int excludeIndex)
        {
            int slots;
            try { slots = MapCatalog.SlotCount; } catch { slots = 0; }
            if (slots <= 0) slots = 14;

            System.Random rng = new System.Random(Guid.NewGuid().GetHashCode());
            for (int attempt = 0; attempt < 30; attempt++)
            {
                int candidate = rng.Next(0, slots);
                if (candidate == excludeIndex) continue;
                try
                {
                    if (MapCatalog.IsSlotAuthentic(candidate)) return candidate;
                }
                catch
                {
                    return candidate;
                }
            }
            try { return MapCatalog.CurrentLevelIndex; } catch { return excludeIndex < 0 ? 0 : excludeIndex; }
        }

        // 单群系冲刺的本局路线取值上限 (MiniRunBiomeCount + 1, 至少 2)
        public int SprintBiomeCount()
        {
            try { return Math.Max(2, RunConfigService.MiniRunBiomeCount + 1); }
            catch { return 5; }
        }

        public List<long> GetLiveSplits(string steamId)
        {
            List<long> result = new List<long>();
            try
            {
                System.Collections.Generic.IReadOnlyList<long> track = LiveSplitTrack.For(steamId);
                if (track != null)
                {
                    for (int i = 0; i < track.Count; i++) result.Add(track[i]);
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 读取 RANKED 原始分段失败: " + ex.Message);
            }
            return result;
        }

        public void ClearLiveSplits()
        {
            try { LiveSplitTrack.Clear(); } catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 清空 RANKED 原始分段失败: " + ex.Message);
            }
        }

        public bool IsOpponentHeartbeatFresh(string opponentSteamId)
        {
            return PhotonConnectivityProbe.IsOpponentHeartbeatFresh(opponentSteamId, OpponentHeartbeatStaleSeconds);
        }
    }
}
