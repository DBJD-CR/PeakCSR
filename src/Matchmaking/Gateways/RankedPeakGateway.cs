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

        // AutoEndMs 是服务端下发的"该局结束后多少毫秒自动收房",
        // PCSR 房间用 RoomMagicCodec 编码过赛制, 但原始毫秒值仍可从 CurrentRace 读到。
        public int AutoEndSecondsApprox
        {
            get
            {
                try
                {
                    RaceInfo race = CurrentRace;
                    if (race == null) return 30;
                    int ms = race.AutoEndMs;
                    if (ms <= 0) return 30;
                    // 编码过的魔数不是真实毫秒, 统一按默认 30 秒播报
                    if (RoomMagicCodec.IsCsrMagicMs(ms)) return 30;
                    int seconds = (int)System.Math.Round(ms / 1000.0);
                    if (seconds < 1) seconds = 1;
                    if (seconds > 120) seconds = 120;
                    return seconds;
                }
                catch
                {
                    return 30;
                }
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

        // 读取真实进度点: 段名一律取 title, 不做任何字符映射
        public ProgressPointInfo[] GetProgressPoints()
        {
            try
            {
                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return new ProgressPointInfo[0];
                ProgressPointEx[] pts = ProgressPointEx.From(handler.progressPoints);
                ProgressPointInfo[] result = new ProgressPointInfo[pts.Length];
                for (int i = 0; i < pts.Length; i++)
                {
                    result[i].HasPosition = pts[i].HasPosition;
                    result[i].Z = pts[i].Z;
                    result[i].IsNadir = pts[i].IsNadir;
                    result[i].Biome = pts[i].BiomeOrdinal;
                    result[i].Title = pts[i].Title;
                }
                return result;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 读取进度点失败: " + ex.Message);
                return new ProgressPointInfo[0];
            }
        }

        // 查找"某段终点"对应的出口篝火 Z
        public bool TryGetCampfireZ(int boundaryPointIndex, out float z)
        {
            z = 0f;
            try
            {
                MountainProgressHandler handler = MountainProgressHandler.Instance;
                MapHandler map = MapHandler.Instance;
                if (handler == null || handler.progressPoints == null || map == null) return false;

                ProgressPointEx[] pts = ProgressPointEx.From(handler.progressPoints);
                if (boundaryPointIndex < 0 || boundaryPointIndex >= pts.Length) return false;

                // 段出口对应的目标 Segment: 用目标进度点的 biome 反查该群系在本次地图里的段序号
                Segment targetSegment = SegmentOfPoint(boundaryPointIndex, pts, map);
                if (targetSegment == Segment.Void) return false;

                Campfire fire = FindCampfireAdvancingTo(targetSegment);
                if (fire == null) return false;

                z = fire.transform.position.z;
                return true;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 查询出口篝火失败: " + ex.Message);
                return false;
            }
        }

        private static Segment SegmentOfPoint(int pointIndex, ProgressPointEx[] pts, MapHandler map)
        {
            if (pts == null || map == null) return Segment.Void;
            try
            {
                System.Collections.Generic.List<Biome.BiomeType> mapBiomes = map.biomes;
                if (mapBiomes == null) return Segment.Void;
                Biome.BiomeType biome = (Biome.BiomeType)pts[pointIndex].BiomeRaw;
                int idx = mapBiomes.IndexOf(biome);
                if (idx < 0) return Segment.Void;
                // 群系序号 -> 段枚举 依次对应 0..5
                int seg = idx;
                if (seg < 0 || seg > (int)Segment.Peak) return Segment.Void;
                return (Segment)seg;
            }
            catch
            {
                return Segment.Void;
            }
        }

        private static Campfire FindCampfireAdvancingTo(Segment segment)
        {
            try
            {
                Campfire[] all = UnityEngine.Object.FindObjectsByType<Campfire>(FindObjectsSortMode.None);
                for (int i = 0; i < all.Length; i++)
                {
                    Campfire f = all[i];
                    if (f == null || f.advanceToSegment != segment) continue;
                    // 迷你跑终点篝火的 advanceToSegment 也指向下一段, 同样被命中
                    return f;
                }
            }
            catch
            {
            }
            return null;
        }

        // 本局路线的进度点区间: 优先用真实群系归属
        public bool TryResolveRouteSpanFor(int route, out int startIndex, out int endIndex)
        {
            startIndex = 0;
            endIndex = -1;
            try
            {
                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return false;
                ProgressPointEx[] pts = ProgressPointEx.From(handler.progressPoints);
                if (pts.Length < 2) return false;

                int last = pts.Length - 1;
                while (last > 0 && pts[last].IsNadir) last--;
                if (last < 1) return false;

                // 全山: 0 .. last
                if (route <= 0)
                {
                    startIndex = 0;
                    endIndex = last;
                    return true;
                }

                // 单群系: 起点 = 该路线首个点; 终点 = 路线终点篝火所在的段末尾
                int biomeOrdinal = route - 1;
                int first = -1;
                int lastOfBiome = -1;
                for (int i = 0; i <= last; i++)
                {
                    if (pts[i].BiomeOrdinal != biomeOrdinal) continue;
                    if (first < 0) first = i;
                    lastOfBiome = i;
                }
                if (first < 0) return false;

                startIndex = first;
                // 迷你跑只有 1 个群系段, 段终点就是该段最后一个点的下一个点
                endIndex = lastOfBiome;
                return endIndex >= startIndex;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 解析路线进度点区间失败: " + ex.Message);
                return false;
            }
        }

        // 查询"群系出现序号 ordinal 对应的进度点区间"。
        public bool TryGetBiomePointSpan(int biomeOrdinal, out int startIndex, out int count)
        {
            startIndex = 0;
            count = 0;
            try
            {
                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return false;

                ProgressPointEx[] pts = ProgressPointEx.From(handler.progressPoints);
                if (pts.Length == 0) return false;

                // 尾部 NADIR 之类辅助点不参与区间计算
                int last = pts.Length - 1;
                while (last > 0 && pts[last].IsNadir) last--;
                if (last < 0) return false;

                for (int i = 0; i <= last; i++)
                {
                    if (pts[i].BiomeOrdinal != biomeOrdinal) continue;
                    if (count == 0) startIndex = i;
                    count++;
                }
                return count > 0;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 查询群系进度点区间失败: " + ex.Message);
                return false;
            }
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
                    // 校验抛异常时这一格的真实性无法确认, 直接跳过继续采样
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
