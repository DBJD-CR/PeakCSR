# 第三方资源声明

本项目包含以下第三方资源，其版权归原作者所有，此处按要求列明来源与许可。

## Twemoji（国旗图形）

- 来源：[Twemoji](https://github.com/twitter/twemoji)
- 用途：`assets/flags` 目录下的国旗位图，由 Twemoji `assets/72x72` 目录里的旗帜文件按 ISO 3166-1 alpha-2 重命名而来，编译时作为 EmbeddedResource 打进 `PeakCSR.dll`
- 版权：Copyright 2020 Twitter, Inc and other contributors
- 许可：图形部分采用 [CC-BY 4.0](https://creativecommons.org/licenses/by/4.0/)

本项目对原始素材的改动仅限于：重命名文件、按需删除不合适的条目、在运行时缩放显示。未修改图形内容本身。

## 取舍说明

为符合主权表述口径，以下条目未纳入资源包，并未随 mod 分发：

- 港澳台（`tw` / `hk` / `mo`）：统一归一为中国（`CN`）
- 非主权实体或地位未定地区（`un` / `eu` / `xk` / `aq`）
- 海外领地与特殊地理代码（`cp` / `dg` / `ea` / `ic` / `ta` / `ac` / `sh`）
