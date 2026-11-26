using System;
using System.Collections.Generic;
using RankedPeak.Live;
using RankedPeak.Models;
using RankedPeak.Services;
using RankedPeak.Timer;

namespace PeakCSR
{
    // RANKED 权威分段的一条记录 (防腐层投影, Core 不依赖 RankedPeak.Models)
    public struct RankedSplitRaw
    {
        // 段名真值 (RANKED 用 GetSegmentDisplayName 生成, 与进度点 title 同源)
        public string SegmentName;
        // 到达该段终点时的累计用时
        public long ElapsedMs;
    }

    // 外部依赖抽象 (RankedPeak 服务与游戏本体的防腐层接口)
    public interface IRankedPeakGateway
    {
        bool ServiceAvailable { get; }
        bool IsHost { get; }
        bool InRace { get; }
        bool RaceIsOver { get; }
        RaceInfo CurrentRace { get; }
        RaceInfo LastFinishedRace { get; }
        long? MsUntilStart { get; }
        RaceParticipant Self { get; }
        // 房间 AutoEndMs 折算的"约多少秒后自动关房", 用于播报收尾倒计时
        int AutoEndSecondsApprox { get; }

        string GameVersion { get; }
        string MapSetHash { get; }
        string MySteamId { get; }
        string VanillaPlayerName { get; }

        bool LiveConnected { get; }
        bool LiveActive { get; }
        TimerState TimerState { get; }
        long TimerElapsedMs { get; }
        // RANKED 权威完赛记录 (EndRun 时落定, TotalMs 与左侧排行榜完全同源)
        //
        // 传本局的 seed/levelIndex
        long AuthoritativeRunMs(int seed, int levelIndex);
        // RANKED 权威分段 (段名 + 累计用时); 同样只在本局记录匹配时返回, 否则为空
        List<RankedSplitRaw> AuthoritativeRunSplits(int seed, int levelIndex);

        event Action<RaceInfo> RaceUpdated;
        event Action<ChatEntry> ChatEntryReceived;

        void FetchLobbies(Action<List<RaceLobbyEntry>, int> onDone, Action<string> onError);
        void CreateRace(int seed, int levelIndex, int ascent, int route, bool isPublic,
            Action<RaceInfo> onSuccess, Action<string> onError);
        void UpdateRaceConfig(int seed, int levelIndex, int ascent, int route, bool isPublic, Action<string> onError);
        void JoinRace(string code, Action<RaceInfo> onSuccess, Action<string> onError);
        void LeaveRace();
        void StartRace(Action<string> onError);
        void SetPublic(bool isPublic, Action<string> onError);
        void RemoveParticipant(string steamId, string name, Action<string> onError);
        void Forfeit();
        // 返回 true 表示本次确实交给了实时连接 (socket 已连接且未抛异常)
        bool SendChat(string text);

        // 左下角聊天框注入高亮系统提示 (原业务层反向调用补丁类的行为迁移至此)
        void PushSystemChat(string richTextBody);
        // 聊天框晚就绪时补推积压消息
        void TickPendingSystemChat();

        // 当前会话赛制 (Gateway 编码房间魔数时取用; 由 MatchCoordinator 注入)
        Func<PcsrRuleFormat> CoordinatorFormat { get; set; }

        // 当前会话计分属性: 与赛制一起编入房间魔数, 让匹配池按 rated 天然隔离
        Func<bool> CoordinatorRated { get; set; }

        string GetBiomeLabel(int levelIndex);
        string GetSegmentBiomeName(int levelIndex, int segmentIndex);
        // 按"群系在本座山里的出现序号"(0 起) 查询该群系占用的进度点区间。
        bool TryGetBiomePointSpan(int biomeOrdinal, out int startIndex, out int count);

        // 读取当前山体的真实进度点 (含段名 title 与世界坐标)
        ProgressPointInfo[] GetProgressPoints();
        // 查询"某段终点进度点索引"对应的出口篝火 Z 坐标
        bool TryGetCampfireZ(int boundaryPointIndex, out float z);
        // 本局路线的进度点区间 (route 为 1-based 群系序号, 0 表示全山)
        bool TryResolveRouteSpanFor(int route, out int startIndex, out int endIndex);
        // 本局路线的锚点链 (入口篝火 + 沿途进度点 + 终点锚点), 用于 3D 弧长归一化
        // 内部按 (route, 地图, 种子) 缓存, 每帧调用安全
        bool TryGetRouteAnchors(int route, out ProgressAnchor[] anchors);
        // 天底阶段的几何上下界: 出生点(低) 与 天底出口 PeakGatePortal(高)。
        // 天底是独立坐标空间的 GameObject, 与主山锚点链无法共用一条单调链,
        // 只能在天底内部单独归一化。需要该 GameObject 已激活, 否则返回 false。
        bool TryGetNadirBounds(out float spawnY, out float exitY);
        // 本局终点篝火 (火山/雾沼路线没有终点篝火, 返回 false), 用于"未点亮不给 100%"
        bool TryGetRouteExitCampfireZ(int route, out float z);
        // 换地图时清空锚点缓存
        void InvalidateAnchorCache();
        int PickRandomAuthenticLevel(int excludeIndex);
        int SprintBiomeCount();
        List<long> GetLiveSplits(string steamId);
        void ClearLiveSplits();
        bool IsOpponentHeartbeatFresh(string opponentSteamId);
    }
}
