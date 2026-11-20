# PeakCSR 云端全服天梯（Cloudflare Worker + D1）保姆级部署教程 🐾

无需购买任何服务器、无需域名、无需绑定信用卡，只需 **3 分钟** 即可在 Cloudflare 免费开通支持**「双端签名共识 + 原版 RaceService 交叉验证」**的全服天梯后端！

---

## 第一步：创建 D1 免费数据库（1 分钟）

1. 打开并登录 [Cloudflare 控制台](https://dash.cloudflare.com/)（没有账号用邮箱免费注册即可）。
2. 在左侧菜单栏点击 **存储和数据库 (Workers & Pages / Storage & Databases)** -> **D1 SQL 数据库 (D1 SQL Database)**。
3. 点击右上角蓝色按钮 **创建数据库 (Create Database)**：
   - 数据库名称填：`peakcsr-db`
   - 点击 **创建 (Create)**。
4. 进入刚创建的 `peakcsr-db` 页面，点击顶部标签栏的 **控制台 (Console)**：
   - 打开咱们仓库里的 [`cloudflare/schema.sql`](schema.sql) 文件，全选复制里面的 SQL 语句，粘贴到输入框里，点击 **执行 (Execute)**。看到表创建成功即可！

---

## 第二步：部署 Worker 代码（1 分钟）

> Worker 已模块化 (`cloudflare/src/` 下按 handlers / domain / db / lib 分层)，
> 评分参数直接 import 仓库根目录的 [`shared/rating-rules.json`](../shared/rating-rules.json)
> 单一事实源，因此推荐用 wrangler 命令行部署（在线编辑器无法粘贴多文件模块）。

**方式 A：wrangler 命令行（推荐）**

```powershell
cd cloudflare
npm install            # 首次需要
npx wrangler deploy
```

**方式 B：在线编辑器单文件拼接**

1. 在左侧菜单栏点击 **计算 (Workers & Pages)** -> **概览 (Overview)**。
2. 点击右上角 **创建 (Create)** -> 选择 **创建 Worker (Create Worker)**：
   - 名称填：`peakcsr-ladder`
   - 点击右下角 **部署 (Deploy)**。
3. 部署成功后，点击右上角的 **编辑代码 (Edit code)** 进入在线编辑器：
   - 把编辑器里自带的默认代码**全部删光**；
   - 按顺序把 `src/index.js` 引用的各模块内容与 `src/index.js` 合并成一个文件粘贴进去
     （把 `import` 段替换为各文件的常量与函数定义，把 `import rules from ... rating-rules.json`
     替换为 `const rules = { ... }` 字面量，内容从 [`shared/rating-rules.json`](../shared/rating-rules.json) 复制）；
   - 点击右上角的 **部署 (Deploy)** 保存代码！

---

## 第三步：把数据库绑定给 Worker（30 秒）

1. 点击左上角返回 `peakcsr-ladder` Worker 的详情页，点击顶部标签栏的 **设置 (Settings)** -> **绑定 (Bindings)**。
2. 点击 **添加 (Add)** -> 选择 **D1 数据库 (D1 database)**：
   - **变量名称 (Variable name)** 必须填：`DB` （全大写！）
   - **D1 数据库 (D1 database)** 下拉选择刚才建好的：`peakcsr-db`
   - 点击 **部署 (Deploy)** 保存！

---

## 第三步补充：配置两个机密变量（强烈推荐）

仓库是公开的，**所有密钥都必须用 Worker 的加密变量注入，绝不能写进代码**。

在 Worker 详情页进入 **设置 (Settings)** -> **变量和机密 (Variables and Secrets)**，点击 **添加 (Add)** -> 类型选 **机密 (Secret)**，逐个添加：

| 名称 | 用途 | 不配置的后果 |
| --- | --- | --- |
| `UPSTREAM_API_KEY` | 「原版 RANKED 后端交叉验证」通道，防止一方强退逃单 | 只走「双端签名共识」，双方都上报且胜利者一致才结算 |
| `ADMIN_TOKEN` | 后台后门 `/api/admin`，人工处理恶意拔线 / 误判纠正 | `/api/admin` 全部返回 403，后门关闭 |

添加完点击 **部署 (Deploy)**。

> `ADMIN_TOKEN` 自己随便生成一串长随机字符串即可（例如 `openssl rand -hex 24` 的结果），
> 之后调用后台接口时放在请求头 `X-Admin-Token` 里。

---

## 第三步补充 2：数据表升级（存量库必须做一次）

新库直接执行 [`schema.sql`](schema.sql) 即可。**已建过表的老库必须做一次增量升级**，
否则 `match_reports.route` / `settled_matches.route` 这两列缺失会让上报与历史查询直接报错。
注意 `schema.sql` 用的是 `CREATE TABLE IF NOT EXISTS`，重跑**不会**给已有表补列。

在 D1 数据库 `peakcsr-db` 的 **控制台 (Console)** 依次执行：
**这是推荐做法，不需要清空任何数据。**

```sql
-- 存量库补齐本轮新增列 (已有可忽略报错)
ALTER TABLE match_reports ADD COLUMN route INTEGER NOT NULL DEFAULT 0;
ALTER TABLE settled_matches ADD COLUMN route INTEGER NOT NULL DEFAULT 0;
```

> 只有主键结构从 `invite_code` 换成 `match_key` 的那次历史改造需要重建表；
> 之后的本轮改动**都可以通过上面的 ALTER 平滑升级**，不必 DROP 任何表。
>
> 如果确实想从零开始（测试期清库），再按下面顺序执行：
>
> ```sql
> DROP TABLE IF EXISTS settled_matches;
> DROP TABLE IF EXISTS match_reports;
> DROP TABLE IF EXISTS players;
> DROP TABLE IF EXISTS admin_audit;
> ```
>
> 然后重新全选粘贴 [`schema.sql`](schema.sql) 执行。

---

## 第四步：把 Worker 地址填进配置文件（30 秒）

1. 在 `peakcsr-ladder` 页面顶部，复制你的专属免费地址（长得像 `https://peakcsr-ladder.你的名字.workers.dev`）。
2. 打开 `C:\Users\ASUS\AppData\Roaming\Thunderstore Mod Manager\DataFolder\PEAK\profiles\RANKED\BepInEx\config\com.peakcsr.ranked1v1.cfg`（或者直接在 [`src/Plugin.cs`](../src/Plugin.cs) 的默认值里填入），将 `CloudApiUrl` 设置为该地址即可！

---

## 第五步：核对接口是否正常（自检）

部署后直接用浏览器打开下面两个地址，都应该返回 JSON 而不是错误页：

- `https://你的地址/api/leaderboard?limit=5` → `{"ok":true,...}`
- `https://你的地址/api/profile?steamId=你的SteamID64` → `{"ok":true,...}`

后台后门自检（PowerShell）：

```powershell
$body = @{ action = "reset"; note = "self check" } | ConvertTo-Json
Invoke-RestMethod -Uri "https://你的地址/api/admin" -Method Post `
  -ContentType "application/json" `
  -Headers @{ "X-Admin-Token" = "你设置的ADMIN_TOKEN" } `
  -Body $body
```

后台可用动作：

| action | 参数 | 说明 |
| --- | --- | --- |
| `recent` | `limit`（可选，默认 20） | 列出最近结算的对局，用来找要作废的 `matchKey` |
| `void` | `matchKey`，或 `inviteCode` + `seed` | 作废某场对局，回滚双方 ELO 与胜负场次 |
| `set-elo` | `steamId`、`elo` | 直接改某人的 ELO（会同时更新 Peak） |
| `reset` | 无 | 清空所有对局与战绩，保留玩家档案 |
| `wipe` | 无 | 连玩家档案一起清空 |

所有后台操作都会写入 `admin_audit` 表留痕。

### 用脚本操作（推荐，不用手写 JSON）

仓库里带了 [`tools/pcsr-admin.ps1`](../tools/pcsr-admin.ps1)，先把 token 放进环境变量（只在本窗口生效，不会写进任何文件）：

```powershell
$env:PCSR_ADMIN_TOKEN = "你设置的ADMIN_TOKEN"
```

然后在**仓库根目录**执行：

```powershell
# 1) 看最近 20 场结算，找到要处理的那一行
.\tools\pcsr-admin.ps1 -Action recent

# 2) 作废某一场（推荐：直接用 recent 列出来的 matchKey）
.\tools\pcsr-admin.ps1 -Action void -MatchKey "SPY4NQ|761912949" -Note "对手拔线，人工回滚"

# 2b) 也可以给房间号 + 种子，脚本自己拼 matchKey
.\tools\pcsr-admin.ps1 -Action void -InviteCode SPY4NQ -Seed 761912949

# 3) 手动修正某人分数
.\tools\pcsr-admin.ps1 -Action set-elo -SteamId 76561198000000000 -Elo 1443 -Note "误扣分回补"

# 4) 测试期清库（保留玩家档案，清零战绩与对局）
.\tools\pcsr-admin.ps1 -Action reset

# 5) 彻底清空（连玩家档案一起删，需要手动输入 WIPE 确认）
.\tools\pcsr-admin.ps1 -Action wipe
```

> **典型场景：某玩家被网络问题误扣分**
>
> 1. `-Action recent` 找到那一局的 `matchKey`
> 2. `-Action void -MatchKey "..."` → 双方 ELO 与胜负场次一起回滚，对局标记为 `is_void`
> 3. 玩家下次进游戏，客户端会从云端拉回正确的权威分数
>
> **典型场景：确认某人恶意拔线**
> 直接 `-Action void` 把该局作废（双方都不扣分），然后按社区规则线下处理，
> 后续可再加人工封禁表，不需要改评分算法误伤所有掉线玩家。

---

## 附：实时日志窗口

游戏进程本身没有控制台窗口，日志只写进 `BepInEx/LogOutput.log`。
本插件内置了一个日志跟随窗口，配置在 `com.peakcsr.ranked1v1.cfg` 的 `[Logging]` 段：

| 配置项 | 默认值 | 说明 |
| --- | --- | --- |
| `AutoOpenLogWindow` | `true` | 启动游戏时自动弹一个独立终端，实时 tail 日志文件 |
| `LogWindowFilterPeakCsr` | `true` | 只显示含 `[PeakCSR]` 的行，关掉则显示完整日志 |
| `LogWindowHotkey` | `F9` | 配合 **Ctrl** 使用，即 `Ctrl + F9` 开关日志窗口 |

> 想看原版 RANKED 的日志就把 `LogWindowFilterPeakCsr` 设为 `false`。
