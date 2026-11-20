# PeakCSR（PCSR 1v1 实时排位）

基于 `PEAK RANKED` 构建的 MCSR 风格实时 1v1 同种子竞速排位扩展模组。

## 功能特性

- **1v1 自动匹配**：筛选专属大厅并自动创建排位房间，通过魔数标识精确区分原版娱乐房。
- **PCSR 赛制**：全山标准局（A0）、高难天阶局（A1~A8）、单群系冲刺局。
- **死亡判负 + 盲打种子**：无复活，开赛前隐藏种子，对手阵亡/掉线立即判胜。
- **实时 ELO 握手**：通过 Live 连接交换 ELO 与版本信息，校验对手是否安装 PeakCSR。
- **防刷分保护**：30 分钟内同一对手超过 3 局不结算 ELO，分差过大自动降低权重。
- **原生主菜单木牌**：在主菜单右侧 RANKED 路牌柱末尾追加一块 `PCSR 1v1` 木牌，样式与相邻木牌完全一致（含段位图标与 ELO 显示）。
- **原生风格排位窗口**：与 RANKED 同一套视觉语言（蓝白标题栏、分区 Box、pill 按钮、点线分隔），集成赛制选择、实时对局遥测与全服天梯。
- **多语言**：默认自动跟随游戏内语言，也可手动切换中英文。

## 操作说明

| 操作 | 功能 |
| --- | --- |
| 点击 `PCSR 1v1` 木牌 | 打开 PCSR 排位窗口 |
| `F10` | 打开 PCSR 排位窗口 |
| `Esc` | 关闭 PCSR 排位窗口 |

真正进入跑图阶段后窗口会自动收起，保证原生游戏 HUD 完整可见。

## 依赖

- `BepInEx-BepInExPack_PEAK`
- `PeakCode-PEAK_RANKED`（PeakCSR 强依赖其原生 UI 和服务组件）

---

PeakCSR is an independent add-on mod and is not affiliated with or endorsed by ThomasAusHH (author of PEAK RANKED) or Landfall. All credit for the underlying Live Race infrastructure belongs to ThomasAusHH.
