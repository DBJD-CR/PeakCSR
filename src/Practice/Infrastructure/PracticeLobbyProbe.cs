using System;
using System.Collections.Generic;
using RankedPeak.Services;

namespace PeakCSR.Practice
{
    // 大厅探针 (纯拉取, 无会话)
    //
    // 直接复用排位那条 FetchLobbies + LobbyFilter, 保证"能匹配到"与"排位能匹配到"
    public sealed class PracticeLobbyProbe : IPracticeLobbyProbe
    {
        private readonly IRankedPeakGateway _gw;
        private bool _busy;

        public PracticeLobbyProbe(IRankedPeakGateway gateway)
        {
            _gw = gateway;
        }

        public bool Busy { get { return _busy; } }

        public void Fetch(PracticeRuleset ruleset, string excludeInviteCode,
            Action<List<PracticeMatchCandidate>> onDone, Action<string> onError)
        {
            if (_busy) return;
            if (_gw == null || _gw.ServiceAvailable == false)
            {
                if (onError != null) onError("RaceService 尚未就绪");
                return;
            }

            _busy = true;
            try
            {
                _gw.FetchLobbies(
                    delegate (List<RaceLobbyEntry> entries, int total)
                    {
                        _busy = false;
                        List<PracticeMatchCandidate> found;
                        try
                        {
                            found = Filter(entries, ruleset, excludeInviteCode);
                        }
                        catch (Exception ex)
                        {
                            if (onError != null) onError(ex.Message);
                            return;
                        }
                        if (onDone != null) onDone(found);
                    },
                    delegate (string err)
                    {
                        _busy = false;
                        if (onError != null) onError(err);
                    });
            }
            catch (Exception ex)
            {
                _busy = false;
                if (onError != null) onError(ex.Message);
            }
        }

        // 从大厅列表里挑出与练习模式赛制完全一致的空位房
        private static List<PracticeMatchCandidate> Filter(
            List<RaceLobbyEntry> entries, PracticeRuleset ruleset, string excludeInviteCode)
        {
            List<PracticeMatchCandidate> result = new List<PracticeMatchCandidate>();
            if (entries == null) return result;

            for (int i = 0; i < entries.Count; i++)
            {
                RaceLobbyEntry e = entries[i];
                if (e == null) continue;
                // 排除刚被判定失效(开赛/满房/自己放弃)的房号
                if (!LobbyFilter.MatchesQueue(e, ruleset.Format, ruleset.Ascent, excludeInviteCode)) continue;

                PracticeMatchCandidate c = new PracticeMatchCandidate();
                c.InviteCode = e.InviteCode;
                c.HostName = e.HostName;
                c.FoundAt = UnityEngine.Time.realtimeSinceStartup;
                result.Add(c);
            }
            return result;
        }
    }
}
