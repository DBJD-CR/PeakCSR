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
        private const float OpponentHeartbeatStaleSeconds = MatchSession.OpponentHeartbeatStaleSeconds;

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

        // 取 RANKED 的 LastRunRecord, 但必须能证明它属于本局
        //
        // RaceTimer.OnRunStarted 不会清 LastRunRecord, 所以主菜单里它一直留着上一局的记录。
        // 校验方式: seed 与 levelIndex 必须与本次对局一致; seed 未知(-1)时退而只校验关卡。
        // 两者都拿不到可靠值就返回 null, 让调用方走原有逻辑, 宁可少一次校正也不能误判。
        private RunRecord GetAuthoritativeRun(int seed, int levelIndex)
        {
            try
            {
                RunRecord rec = RaceTimer.Instance != null ? RaceTimer.Instance.LastRunRecord : null;
                if (rec == null) return null;
                if (rec.TotalMs <= 0) return null;
                if (rec.Result != RunResult.Completed) return null;

                // 关卡必须一致 (RANKED 记录的是实际加载的场景槽位)
                if (levelIndex >= 0 && rec.LevelIndex >= 0 && rec.LevelIndex != levelIndex) return null;
                // seed 双方都有效时必须一致; 任一方为 -1/0 视为"不可比", 不因此否决
                if (seed > 0 && rec.Seed > 0 && rec.Seed != seed) return null;

                return rec;
            }
            catch { return null; }
        }

        // RANKED 权威总用时: 取自 RunRecord.TotalMs (_finalMs), 在 EndRun 时落定
        public long AuthoritativeRunMs(int seed, int levelIndex)
        {
            RunRecord rec = GetAuthoritativeRun(seed, levelIndex);
            return rec != null ? rec.TotalMs : 0L;
        }

        // RANKED 权威分段 (段名 + 累计用时)
        // 返回副本, 避免调用方拿到 RANKED 内部可变 List 的引用
        public List<RankedSplitRaw> AuthoritativeRunSplits(int seed, int levelIndex)
        {
            List<RankedSplitRaw> result = new List<RankedSplitRaw>();
            RunRecord rec = GetAuthoritativeRun(seed, levelIndex);
            if (rec == null || rec.Splits == null) return result;
            try
            {
                for (int i = 0; i < rec.Splits.Count; i++)
                {
                    SplitRecord s = rec.Splits[i];
                    if (s == null) continue;
                    RankedSplitRaw item = new RankedSplitRaw();
                    item.SegmentName = s.SegmentName ?? "";
                    item.ElapsedMs = s.ElapsedMs;
                    result.Add(item);
                }
            }
            catch { }
            return result;
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
                RoomMagicCodec.GetMagicAutoEndMs(ResolveCoordinatorFormat(), ascent, ResolveCoordinatorRated()),
                rules, false, isPublic, onSuccess, onError);
        }

        public void UpdateRaceConfig(int seed, int levelIndex, int ascent, int route, bool isPublic, Action<string> onError)
        {
            RaceRules rules = new RaceRules { ReviveDead = false };
            RaceService.Instance.UpdateRaceConfig(
                seed, levelIndex, ascent, route, 2,
                false, false, true, true,
                RoomMagicCodec.GetMagicAutoEndMs(ResolveCoordinatorFormat(), ascent, ResolveCoordinatorRated()),
                rules, false, isPublic, onError);
        }

        private bool ResolveCoordinatorRated()
        {
            Func<bool> f = CoordinatorRated;
            if (f != null)
            {
                try { return f(); } catch { }
            }
            return true;
        }

        private PcsrRuleFormat ResolveCoordinatorFormat()
        {
            Func<PcsrRuleFormat> f = CoordinatorFormat;
            if (f != null)
            {
                try { return f(); } catch { }
            }
            return PcsrRuleFormat.StandardAllPeak;
        }

        // 当前会话赛制由协调器注入 (Gateway 编码房间魔数时取用)
        public Func<PcsrRuleFormat> CoordinatorFormat { get; set; }

        // 当前会话计分属性 (与赛制一起编入魔数, 让匹配池按 rated 隔离)
        public Func<bool> CoordinatorRated { get; set; }

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

        // 返回 true 仅当包真的交给实时连接; socket 未就绪/发送异常一律 false
        public bool SendChat(string text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            try
            {
                RaceLiveConnection conn = RaceLiveConnection.Instance;
                if (conn == null || !conn.IsConnected) return false;
                conn.SendChat(text);
                return true;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 发送实时聊天失败: " + ex.Message);
                return false;
            }
        }

        // 找不到聊天框时最多缓存这么多条, 避免长时间离线练习把内存撑大
        private const int MaxPendingChat = 32;
        private readonly List<string> _pendingChat = new List<string>();

        // 左下角聊天框注入高亮系统提示 (经反射调用 LiveChatOverlay.OnEntry)
        public void PushSystemChat(string richTextBody)
        {
            if (string.IsNullOrEmpty(richTextBody)) return;
            try
            {
                LiveChatOverlay overlay = UnityEngine.Object.FindAnyObjectByType<LiveChatOverlay>();
                if (overlay == null)
                {
                    // 练习局是离线单人 Level_* 场景, 聊天框根本不存在
                    // 先留痕并缓存, 等回到主菜单聊天框就绪时补推
                    PcsrLog.LogInfo("[PeakCSR] 聊天框尚未就绪, 系统消息已缓存待补推: " + richTextBody);
                    EnqueuePendingChat(richTextBody);
                    _nextFlushTryAt = Time.realtimeSinceStartup + FlushRetrySeconds;
                    return;
                }
                if (TryPushToOverlay(overlay, richTextBody))
                {
                    FlushPendingChat(overlay);
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 推送聊天框系统消息失败: " + ex.Message);
            }
        }

        // 补推节流
        private const float FlushRetrySeconds = 2f;
        private float _nextFlushTryAt;
        private LiveChatOverlay _cachedOverlay;

        // 由外部低频驱动: 聊天框一旦可用就把积压的消息补上
        public void TickPendingSystemChat()
        {
            if (_pendingChat.Count == 0) return;
            float now = Time.realtimeSinceStartup;
            if (now < _nextFlushTryAt) return;
            _nextFlushTryAt = now + FlushRetrySeconds;
            try
            {
                if (_cachedOverlay == null) _cachedOverlay = UnityEngine.Object.FindAnyObjectByType<LiveChatOverlay>();
                if (_cachedOverlay == null) return;
                FlushPendingChat(_cachedOverlay);
            }
            catch
            {
                _cachedOverlay = null;
            }
        }

        private void EnqueuePendingChat(string body)
        {
            if (_pendingChat.Count >= MaxPendingChat) _pendingChat.RemoveAt(0);
            _pendingChat.Add(body);
        }

        private void FlushPendingChat(LiveChatOverlay overlay)
        {
            if (_pendingChat.Count == 0) return;
            string[] pending = _pendingChat.ToArray();
            _pendingChat.Clear();
            for (int i = 0; i < pending.Length; i++)
            {
                if (!TryPushToOverlay(overlay, pending[i])) return;
            }
        }

        private bool TryPushToOverlay(LiveChatOverlay overlay, string body)
        {
            if (overlay == null || string.IsNullOrEmpty(body)) return false;
            if (_onChatEntryMethod == null)
            {
                _onChatEntryMethod = typeof(LiveChatOverlay).GetMethod("OnEntry",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }
            if (_onChatEntryMethod == null) return false;
            ChatEntry sysEntry = new ChatEntry
            {
                Kind = ChatEntryKind.System,
                Body = body,
                ReceivedAt = Time.realtimeSinceStartup,
                Seen = false
            };
            _onChatEntryMethod.Invoke(overlay, new object[] { sysEntry });
            return true;
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
                    result[i].X = pts[i].X;
                    result[i].Y = pts[i].Y;
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

        // 进度点 -> 该点所属的 Segment
        //
        // 不能用 map.biomes 的下标当 Segment 枚举值:
        //   Segment 是固定枚举 (Beach/Tropics/Alpine/Caldera/TheKiln/Peak),
        //   而 BiomeType 是另一套编号 (Mesa=6, Roots=7, Swamp=8...),
        //   当这张图没有 Tropics 时两套序号就错位, 会指到错误的篝火。
        // 正确做法: 用地图自己的 segments[i].biome 反查段号 (游戏本体的 GetBiomeForSegment 同源)
        private static Segment SegmentOfPoint(int pointIndex, ProgressPointEx[] pts, MapHandler map)
        {
            if (pts == null || map == null) return Segment.Void;
            try
            {
                Biome.BiomeType biome = (Biome.BiomeType)pts[pointIndex].BiomeRaw;

                // 该点在本局同 biome 进度点里排第几个 (0 起)
                // 一个群系可能横跨多个 Segment (火山群系同时包含 CALDERA 与 THE KILN),
                // 只按 biome 反查会永远命中第一个, 把出口篝火错认成入口篝火
                int occurrence = 0;
                for (int i = 0; i < pointIndex && i < pts.Length; i++)
                {
                    if ((Biome.BiomeType)pts[i].BiomeRaw == biome) occurrence++;
                }

                // 权威映射: segments[] 的下标即 Segment 枚举值
                MapHandler.MapSegment[] segments = map.segments;
                if (segments != null && segments.Length > 0)
                {
                    int seen = 0;
                    for (int i = 0; i < segments.Length; i++)
                    {
                        MapHandler.MapSegment seg = segments[i];
                        if (seg == null || seg.biome != biome) continue;
                        if (seen < occurrence)
                        {
                            seen++;
                            continue;
                        }
                        // 段号必须落在 Segment 枚举合法区间内
                        if (i > (int)Segment.Peak) return Segment.Void;
                        return (Segment)i;
                    }
                }

                // 兜底: 退回 biomes 下标 (仅当上面拿不到 segments 时)
                System.Collections.Generic.List<Biome.BiomeType> mapBiomes = map.biomes;
                if (mapBiomes == null) return Segment.Void;
                int idx = mapBiomes.IndexOf(biome);
                if (idx < 0 || idx > (int)Segment.Peak) return Segment.Void;
                return (Segment)idx;
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

        // 锚点链缓存
        private int _anchorCacheRoute = int.MinValue;
        private int _anchorCacheLevel = int.MinValue;
        private int _anchorCacheSeed = int.MinValue;
        private ProgressAnchor[] _anchorCache;
        private bool _anchorCacheValid;

        public void InvalidateAnchorCache()
        {
            _anchorCacheValid = false;
            _anchorCache = null;
            _anchorCacheRoute = int.MinValue;
            _anchorCacheLevel = int.MinValue;
            _anchorCacheSeed = int.MinValue;
        }

        // 缓存版: 每帧调用安全
        public bool TryGetRouteAnchors(int route, out ProgressAnchor[] anchors)
        {
            int level = -1;
            int seed = 0;
            try
            {
                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler != null && handler.progressPoints != null) level = handler.progressPoints.Length;
                RaceInfo race = CurrentRace;
                if (race != null)
                {
                    seed = race.Seed;
                    level = level * 31 + race.LevelIndex;
                }
            }
            catch
            {
            }

            if (_anchorCacheValid && _anchorCache != null
                && route == _anchorCacheRoute && level == _anchorCacheLevel && seed == _anchorCacheSeed)
            {
                anchors = _anchorCache;
                return true;
            }

            ProgressAnchor[] built;
            if (!BuildRouteAnchors(route, out built))
            {
                // 构建失败也不要每帧重试: 失败同样按地图维度缓存
                _anchorCacheRoute = route;
                _anchorCacheLevel = level;
                _anchorCacheSeed = seed;
                _anchorCache = null;
                _anchorCacheValid = true;
                anchors = null;
                return false;
            }

            _anchorCacheRoute = route;
            _anchorCacheLevel = level;
            _anchorCacheSeed = seed;
            _anchorCache = built;
            _anchorCacheValid = true;
            anchors = built;
            return true;
        }

        // 本局路线的"锚点链": 沿途必须依次经过的真实世界坐标, 用于 3D 弧长归一化
        //
        // 终点锚点按路线区分, 判据是游戏自己的 EndGame 触发方式:
        //   Campfire.IsMiniRunEndingCampfire 的 switch 只有 0/1/2 三个分支
        //   (MiniRunBiome 0=Tropics 1=Alpine 2=Caldera), 3 落进 _ => false。
        //   所以只有单群系海岸/雨林/雪山/森蕈/方山 (route 1..3) 是"点亮出口篝火即完赛"
        //   全山 (route 0) 没有终点篝火, 要点顶峰信号棒才 EndGame;
        //   火山/雾沼 (route 4/5) 同理, 点亮熔炉/城塞入口篝火只会 GoToSegment 到 Peak 段。
        //   这两类都以 PEAK 进度点为终点锚点。
        private bool BuildRouteAnchors(int route, out ProgressAnchor[] anchors)
        {
            anchors = null;
            try
            {
                MountainProgressHandler handler = MountainProgressHandler.Instance;
                if (handler == null || handler.progressPoints == null) return false;
                ProgressPointEx[] pts = ProgressPointEx.From(handler.progressPoints);
                if (pts.Length < 2) return false;

                int last = pts.Length - 1;
                while (last > 0 && pts[last].IsNadir) last--;
                if (last < 1) return false;

                int startIdx;
                int endIdx;
                if (!TryResolveRouteSpanFor(route, out startIdx, out endIdx)) return false;
                if (endIdx < startIdx || endIdx > last) return false;

                List<ProgressAnchor> list = new List<ProgressAnchor>();

                // 入口篝火: 本路线的真实出生点, 必须作为第 0 个锚点
                // 缺了它, 出生点到首个进度点这一段就是盲区
                Campfire entry = FindCampfireAdvancingTo(SegmentOfPoint(startIdx, pts, MapHandler.Instance));
                if (entry != null)
                {
                    Vector3 p = entry.transform.position;
                    ProgressAnchor a0 = new ProgressAnchor();
                    a0.X = p.x;
                    a0.Y = p.y;
                    a0.Z = p.z;
                    a0.PointIndex = -1;
                    list.Add(a0);
                }

                // 路线覆盖的进度点
                for (int i = startIdx; i <= endIdx; i++)
                {
                    if (!pts[i].HasPosition) continue;
                    ProgressAnchor ap = new ProgressAnchor();
                    ap.X = pts[i].X;
                    ap.Y = pts[i].Y;
                    ap.Z = pts[i].Z;
                    ap.PointIndex = i;
                    list.Add(ap);
                }

                if (list.Count < 1) return false;

                // 终点锚点
                if (IsSummitRoute(route))
                {
                    // 全山/火山/雾沼: 终点是顶峰, 不是篝火
                    int peakIdx = FindSummitPointIndex(pts, last);
                    if (peakIdx < 0) return false;

                    // 峰顶点可能已经随区间加入链内 (全山覆盖到 PEAK), 就地打标记;
                    // 只有它不在链里时才追加, 否则会多出一条反向尾巴
                    int at = IndexOfPointIndex(list, peakIdx);
                    if (at >= 0)
                    {
                        ProgressAnchor marked = list[at];
                        marked.IsSummit = true;
                        list[at] = marked;
                    }
                    else if (pts[peakIdx].HasPosition)
                    {
                        ProgressAnchor summit = new ProgressAnchor();
                        summit.X = pts[peakIdx].X;
                        summit.Y = pts[peakIdx].Y;
                        summit.Z = pts[peakIdx].Z;
                        summit.PointIndex = peakIdx;
                        summit.IsSummit = true;
                        list.Add(summit);
                    }
                }
                else
                {
                    // 海岸/雨林/森蕈/雪山/方山: 终点是点亮后即完赛的出口篝火。
                    // 出口 = "推进到本路线之后那一段"的篝火, 段号由起点段 +1 得到;
                    // 不能用进度点索引推算, 两者在本图并不等价
                    Segment startSeg = SegmentOfPoint(startIdx, pts, MapHandler.Instance);
                    if (startSeg != Segment.Void && (int)startSeg + 1 <= (int)Segment.TheKiln)
                    {
                        Campfire exit = FindCampfireAdvancingTo((Segment)((int)startSeg + 1));
                        if (exit != null)
                        {
                            Vector3 p = exit.transform.position;
                            ProgressAnchor aEnd = new ProgressAnchor();
                            aEnd.X = p.x;
                            aEnd.Y = p.y;
                            aEnd.Z = p.z;
                            aEnd.PointIndex = -1;
                            list.Add(aEnd);
                        }
                    }
                }

                if (list.Count < 2) return false;
                anchors = list.ToArray();
                return true;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 构建路线锚点链失败: " + ex.Message);
                anchors = null;
                return false;
            }
        }

        // 天底阶段的几何上下界 (Y 轴爬升量)
        // 结果按实例缓存: 天底 GameObject 只在整局里初始化一次。
        private static bool _nadirBoundsValid;
        private static float _nadirSpawnY;
        private static float _nadirExitY;

        public void InvalidateNadirBounds()
        {
            _nadirBoundsValid = false;
        }

        public bool TryGetNadirBounds(out float spawnY, out float exitY)
        {
            spawnY = 0f;
            exitY = 0f;
            if (_nadirBoundsValid)
            {
                spawnY = _nadirSpawnY;
                exitY = _nadirExitY;
                return true;
            }

            try
            {
                Peak.VoidBiome voidBiome = Peak.VoidBiome.instance;
                if (voidBiome == null) return false;

                // 出口: 天底里唯一会调 EndGame 的交互物件
                Peak.PeakGatePortal portal = UnityEngine.Object.FindAnyObjectByType<Peak.PeakGatePortal>();
                if (portal == null) return false;

                // 入口: 优先取本机座位的出生点, 与玩家实际落地位置一致
                UnityEngine.Vector3 spawn;
                try { spawn = voidBiome.GetSpawnPosition(0); }
                catch { spawn = UnityEngine.Vector3.zero; }
                if (spawn == UnityEngine.Vector3.zero && voidBiome.spawnPoints != null && voidBiome.spawnPoints.Length > 0)
                {
                    UnityEngine.Transform tf = voidBiome.spawnPoints[0];
                    if (tf == null) return false;
                    spawn = tf.position;
                }

                float low = spawn.y;
                float high = portal.transform.position.y;
                // 高度必须真的一上一下, 否则说明取到了错误物件
                if (high - low < 1f) return false;

                _nadirSpawnY = low;
                _nadirExitY = high;
                _nadirBoundsValid = true;

                spawnY = low;
                exitY = high;
                return true;
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 读取天底上下界失败: " + ex.Message);
                return false;
            }
        }

        // 本局终点篝火的 z 坐标
        // 全山与火山/雾沼没有终点篝火 (IsMiniRunEndingCampfire 恒 false), 返回 false
        public bool TryGetRouteExitCampfireZ(int route, out float z)
        {
            z = 0f;
            // 火山/雾沼没有终点篝火, 终点是顶峰
            if (IsSummitRoute(route)) return false;
            // 非顶峰路线的锚点链末点就是出口篝火, 直接复用缓存, 不再做全场景搜索
            ProgressAnchor[] anchors;
            if (!TryGetRouteAnchors(route, out anchors)) return false;
            if (anchors == null || anchors.Length < 2) return false;
            ProgressAnchor end = anchors[anchors.Length - 1];
            if (end.IsSummit) return false;
            z = end.Z;
            return true;
        }

        // 顶峰收尾的路线: 全山 (route 0) 与火山/雾沼
        //
        // 没有"点亮即完赛"的出口篝火, 必须爬到顶峰点信号棒才 EndGame;
        // 只有单群系 海岸/雨林/雪山/森蕈/方山 是点亮出口篝火即完赛
        private static bool IsSummitRoute(int route)
        {
            return route <= 0 || route >= 4;
        }

        private static int FindSummitPointIndex(ProgressPointEx[] pts, int last)
        {
            for (int i = last; i >= 0; i--)
            {
                if (string.Equals(BiomeSegmenter.NormalizeTitle(pts[i].Title), "PEAK", StringComparison.Ordinal))
                {
                    return i;
                }
            }
            return last;
        }

        private static int IndexOfPointIndex(List<ProgressAnchor> list, int pointIndex)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].PointIndex == pointIndex) return i;
            }
            return -1;
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

                // 单群系: 起点 = 该路线首个点
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

                // 终点分两类, 判据与 BuildRouteAnchors 一致:
                //   顶峰收尾路线 (火山/雾沼 route 4/5): 没有"点亮即完赛"的出口篝火,
                //   必须爬到 PEAK 点信号棒才 EndGame
                if (IsSummitRoute(route))
                {
                    endIndex = FindSummitPointIndex(pts, last);
                }
                else
                {
                    endIndex = lastOfBiome;
                }
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
