using System;

namespace PeakCSR.Practice
{
    // 练习模式赛制值对象
    //
    // 与排位使用同一套 PcsrRuleFormat + 天阶 + 路线, 唯一区别是不进入任何结算通道。
    // 这样练习的难度/路线与真实对局逐项一致, 练习结果才有参考价值。
    public struct PracticeRuleset
    {
        public PcsrRuleFormat Format;
        public int Ascent;
        public int Route;

        public static PracticeRuleset Create(PcsrRuleFormat format, int ascent, int route)
        {
            PracticeRuleset r = new PracticeRuleset();
            r.Format = format;
            r.Ascent = RoomMagicCodec.ClampAscent(ascent);
            r.Route = route < 0 ? 0 : route;
            return r;
        }

        // 单群系冲刺必须带 1..N 的路线; 其余赛制一律全山 (route 0)
        // sprintRouteRoller: 由调用方注入随机源, 保证与真实建房时的取路逻辑同一口径
        public static PracticeRuleset CreateFor(PcsrRuleFormat format, int ascent, Func<int> sprintRouteRoller)
        {
            int route = 0;
            if (format == PcsrRuleFormat.BiomeSprint && sprintRouteRoller != null)
            {
                route = sprintRouteRoller();
                if (route < 1) route = 1;
            }
            return Create(format, ascent, route);
        }

        public string DisplayName
        {
            get { return MatchNaming.GetFormatDisplayName(Format, Ascent); }
        }
    }
}
