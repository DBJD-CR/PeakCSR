namespace PeakCSR.Practice
{
    // 探针发现的可加入对手房间
    //
    // 纯值对象, 不含任何网络类型 (RaceLobbyEntry 等只在基础设施层出现),
    // 便于领域层与 RANKED 解耦。
    public struct PracticeMatchCandidate
    {
        public string InviteCode;
        public string HostName;

        // 发现时刻 (Time.realtimeSinceStartup), 用于提示"匹配到多久了"
        public float FoundAt;

        public bool IsValid
        {
            get { return !string.IsNullOrEmpty(InviteCode); }
        }
    }
}
