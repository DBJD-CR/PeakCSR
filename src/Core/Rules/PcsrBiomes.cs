namespace PeakCSR
{
    // 游戏内真实群系列表
    // 顺序与游戏 RichPresence 的海拔顺序一致: 海岸 -> 天底
    public static class PcsrBiomes
    {
        public struct Biome
        {
            public string Zh;
            public string En;
            // 地图签名里的字符; 'W' 用于城塞(游戏中读作 THE CITADEL)
            public char Sig;

            public Biome(string zh, string en, char sig)
            {
                Zh = zh;
                En = en;
                Sig = sig;
            }
        }

        public static readonly Biome[] All =
        {
            new Biome("海岸",     "SHORE",       'S'),
            new Biome("雨林",     "TROPICS",     'T'),
            new Biome("森蕈",     "ROOTS",       'R'),
            new Biome("雪山",     "ALPINE",      'A'),
            new Biome("方山",     "MESA",        'M'),
            new Biome("火山",     "CALDERA",     'V'),
            new Biome("熔炉",     "THE KILN",    'K'),
            new Biome("雾沼",     "GLOOM",       'G'),
            new Biome("城塞",     "THE CITADEL", 'W'),
            new Biome("顶峰",     "PEAK",        'P'),
            new Biome("天底",     "NADIR",       'N')
        };

        public static int Count { get { return All.Length; } }

        public static string Label(int index, bool chinese)
        {
            if (index < 0 || index >= All.Length) return "";
            return chinese ? All[index].Zh : All[index].En;
        }

        public static string[] Labels(bool chinese)
        {
            string[] result = new string[All.Length];
            for (int i = 0; i < All.Length; i++)
            {
                result[i] = chinese ? All[i].Zh : All[i].En;
            }
            return result;
        }

        // 从地图签名字符查回群系索引, 找不到返回 -1
        public static int IndexOfSig(char sig)
        {
            for (int i = 0; i < All.Length; i++)
            {
                if (All[i].Sig == sig) return i;
            }
            return -1;
        }

        // 判断某个群系是否出现在该地图的签名串里 (签名串形如 "STAMD")
        public static bool SignatureContains(string biomeSignature, int biomeIndex)
        {
            if (string.IsNullOrEmpty(biomeSignature)) return false;
            if (biomeIndex < 0 || biomeIndex >= All.Length) return false;
            char sig = All[biomeIndex].Sig;
            return biomeSignature.IndexOf(sig) >= 0;
        }
    }
}
