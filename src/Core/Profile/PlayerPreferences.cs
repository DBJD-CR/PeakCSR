namespace PeakCSR
{
    // 玩家可自选的群系偏好 (索引见 PcsrBiomes, -1 表示未设置)
    //
    // 群系互斥规则的唯一事实源: PlayerRecord 只持有这个值对象
    // 偏好只影响展示, 不参与评分, 也不进 ELO 签名。
    public sealed class PlayerPreferences
    {
        public int FavoriteBiome = -1;
        public int DislikedBiome = -1;
        // 国籍 (ISO 3166-1 alpha-2 大写); 空串表示未设置
        public string Country = PcsrCountries.Unset;

        public static bool IsValidBiome(int index)
        {
            return index >= 0 && index < PcsrBiomes.Count;
        }

        public void Reset()
        {
            FavoriteBiome = -1;
            DislikedBiome = -1;
            Country = PcsrCountries.Unset;
        }

        // 越界一律视为未设置; 最爱与最讨厌不允许指向同一个群系
        public void Normalize()
        {
            if (!IsValidBiome(FavoriteBiome)) FavoriteBiome = -1;
            if (!IsValidBiome(DislikedBiome)) DislikedBiome = -1;
            if (FavoriteBiome >= 0 && FavoriteBiome == DislikedBiome) DislikedBiome = -1;
            Country = PcsrCountries.Normalize(Country);
        }

        // 国籍写入同样走归一口径, 避免非主权代码落盘
        public void SetCountry(string code)
        {
            Country = PcsrCountries.Normalize(code);
        }

        // 设置最爱: 越界夹到 -1; 若顶掉了原有的最讨厌, 直接清掉对方 (返回 true 表示清掉过)
        public bool SetFavorite(int index)
        {
            if (index < 0 || !IsValidBiome(index)) index = -1;
            FavoriteBiome = index;
            if (FavoriteBiome >= 0 && FavoriteBiome == DislikedBiome)
            {
                DislikedBiome = -1;
                return true;
            }
            return false;
        }

        // 设置最讨厌群系: 与最爱冲突时不动最爱, 直接拒绝这次设置
        public bool SetDisliked(int index)
        {
            if (index != -1 && !IsValidBiome(index)) return false;
            if (index >= 0 && index == FavoriteBiome) return false;
            DislikedBiome = index;
            return true;
        }
    }
}
