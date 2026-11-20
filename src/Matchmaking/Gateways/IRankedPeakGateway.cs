using System;
using System.Collections.Generic;
using RankedPeak.Live;
using RankedPeak.Models;
using RankedPeak.Services;
using RankedPeak.Timer;

namespace PeakCSR
{
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
        void SendChat(string text);

        // 左下角聊天框注入高亮系统提示 (原业务层反向调用补丁类的行为迁移至此)
        void PushSystemChat(string richTextBody);

        // 当前会话赛制 (Gateway 编码房间魔数时取用; 由 MatchCoordinator 注入)
        Func<McsrRuleFormat> CoordinatorFormat { get; set; }

        string GetBiomeLabel(int levelIndex);
        string GetSegmentBiomeName(int levelIndex, int segmentIndex);
        // 按"群系在本座山里的出现序号"(0 起) 查询该群系占用的进度点区间。
        bool TryGetBiomePointSpan(int biomeOrdinal, out int startIndex, out int count);
        int PickRandomAuthenticLevel(int excludeIndex);
        int SprintBiomeCount();
        List<long> GetLiveSplits(string steamId);
        void ClearLiveSplits();
        bool IsOpponentHeartbeatFresh(string opponentSteamId);
    }
}
