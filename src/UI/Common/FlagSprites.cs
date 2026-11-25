using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace PeakCSR
{
    // 国旗 Sprite 加载与缓存
    //
    // 资源来自 Twemoji (CC-BY 4.0), 构建时以 EmbeddedResource 打进 dll,
    // 资源名为 PeakCSR.assets.flags.<code>.png
    //
    // 为什么不用 emoji 字符:
    //   游戏用的 daruma/pangolin 字体没有 emoji 字形, TMP 的字形兜底只探测
    //   假名/韩文/中文/西里尔/希腊, 且 NativeUiFactory 会主动跳过名字含 emoji
    //   的资源; 国旗 emoji 还需要区域指示符连字规则, TMP 并不支持
    //   所以这里走位图路线, 由 Image 直接绘制
    public static class FlagSprites
    {
        // Twemoji 的旗帜是 72x72 方形画布 (圆角已烘焙进图)
        // 保持 1:1 才不会被拉变形; 18px 与昵称 20px 字号视觉同高
        public const float IconWidth = 18f;
        public const float IconHeight = 18f;

        private const string ResourcePrefix = "PeakCSR.assets.flags.";
        private const string ResourceSuffix = ".png";

        // code -> Sprite; null 表示已尝试加载但资源不存在, 避免反复反射查找
        private static readonly Dictionary<string, Sprite> _cache =
            new Dictionary<string, Sprite>(StringComparer.OrdinalIgnoreCase);

        private static readonly Assembly _asm = typeof(FlagSprites).Assembly;

        // 取国旗 Sprite; 未设置/无资源时返回 null, 调用方据此隐藏 Image
        public static Sprite Get(string code)
        {
            string normalized = PcsrCountries.Normalize(code);
            if (string.IsNullOrEmpty(normalized)) return null;

            Sprite cached;
            if (_cache.TryGetValue(normalized, out cached)) return cached;

            Sprite loaded = Load(normalized);
            _cache[normalized] = loaded;
            return loaded;
        }

        private static Sprite Load(string code)
        {
            try
            {
                string resName = ResourcePrefix + code.ToLowerInvariant() + ResourceSuffix;
                using (Stream stream = _asm.GetManifestResourceStream(resName))
                {
                    if (stream == null) return null;
                    byte[] bytes = new byte[stream.Length];
                    int read = 0;
                    while (read < bytes.Length)
                    {
                        int n = stream.Read(bytes, read, bytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    if (read <= 0) return null;

                    // 2x2 只是占位, LoadImage 会按 PNG 头重设尺寸
                    Texture2D tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    tex.filterMode = FilterMode.Bilinear;
                    tex.wrapMode = TextureWrapMode.Clamp;
                    if (!ImageConversion.LoadImage(tex, bytes, false))
                    {
                        UnityEngine.Object.Destroy(tex);
                        return null;
                    }
                    // 旗帜要缩到 18px 显示, mipmap 反而会糊, 这里关掉
                    tex.name = "PeakCSR_Flag_" + code;

                    Sprite sprite = Sprite.Create(
                        tex,
                        new Rect(0f, 0f, tex.width, tex.height),
                        new Vector2(0.5f, 0.5f),
                        100f, 0u, SpriteMeshType.FullRect);
                    sprite.name = "PeakCSR_Flag_" + code;
                    return sprite;
                }
            }
            catch (Exception ex)
            {
                PcsrLog.LogWarning("[PeakCSR] 国旗资源加载失败 (" + code + "): " + ex.Message);
                return null;
            }
        }

        // 语言切换等场景下窗口重建时, Sprite 缓存可以继续复用,
        // 但 Texture2D 必须在插件卸载时释放, 否则会泄漏
        public static void Clear()
        {
            foreach (KeyValuePair<string, Sprite> kv in _cache)
            {
                Sprite s = kv.Value;
                if (s == null) continue;
                Texture2D tex = s.texture;
                UnityEngine.Object.Destroy(s);
                if (tex != null) UnityEngine.Object.Destroy(tex);
            }
            _cache.Clear();
        }
    }
}
