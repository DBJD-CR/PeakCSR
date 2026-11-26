namespace PeakCSR.Practice
{
    // 全局练习运行态标记
    //
    // 只放"当前是否有练习局在跑"这一个事实, 供补丁侧做最小侵入判定:
    // 练习局的 RunRecord 不得写入 PB / 历史
    // 练习局不得上传云端
    // 练习局的 Photon 断线不得触发排位作废
    // 不持有会话对象, 避免补丁反向依赖应用层
    public static class PracticeRunFlag
    {
        private static bool _active;

        public static bool IsActive
        {
            get { return _active; }
        }

        public static void Engage()
        {
            _active = true;
        }

        public static void Release()
        {
            _active = false;
        }
    }
}
