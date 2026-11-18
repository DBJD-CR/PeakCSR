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

## 第二步：创建 Cloudflare Worker 并粘贴代码（1 分钟）

1. 在左侧菜单栏点击 **计算 (Workers & Pages)** -> **概览 (Overview)**。
2. 点击右上角 **创建 (Create)** -> 选择 **创建 Worker (Create Worker)**：
   - 名称填：`peakcsr-ladder`
   - 点击右下角 **部署 (Deploy)**。
3. 部署成功后，点击右上角的 **编辑代码 (Edit code)** 进入在线编辑器：
   - 把左侧编辑器里自带的 `worker.js` 默认代码**全部删光**；
   - 打开咱们仓库里的 [`cloudflare/worker.js`](worker.js) 文件，全选复制粘贴进去；
   - 点击右上角的 **部署 (Deploy)** 保存代码！

---

## 第三步：把数据库绑定给 Worker（30 秒）

1. 点击左上角返回 `peakcsr-ladder` Worker 的详情页，点击顶部标签栏的 **设置 (Settings)** -> **绑定 (Bindings)**。
2. 点击 **添加 (Add)** -> 选择 **D1 数据库 (D1 database)**：
   - **变量名称 (Variable name)** 必须填：`DB` （全大写！）
   - **D1 数据库 (D1 database)** 下拉选择刚才建好的：`peakcsr-db`
   - 点击 **部署 (Deploy)** 保存！

---

## 第三步补充：配置上游 API 密钥（可选但推荐）

「原版后端交叉验证」通道需要向 RANKED 官方后端发起查询，该密钥**绝不能写进代码**（仓库是公开的），必须用 Worker 的加密变量注入：

1. 在 Worker 详情页进入 **设置 (Settings)** -> **变量和机密 (Variables and Secrets)**。
2. 点击 **添加 (Add)** -> 类型选择 **机密 (Secret)**：
   - 名称填：`UPSTREAM_API_KEY`
   - 值填你自己的 RANKED 后端 API Key
3. 保存并 **部署 (Deploy)**。

> 不配置也能正常运行：此时仅使用「双端签名共识」判定，双方都上报且胜利者一致才结算；配置后会额外启用官方后端的权威交叉验证，可防止一方强退逃单。

---

## 第四步：把 Worker 地址填进配置文件（30 秒）

1. 在 `peakcsr-ladder` 页面顶部，复制你的专属免费地址（长得像 `https://peakcsr-ladder.你的名字.workers.dev`）。
2. 打开 `C:\Users\ASUS\AppData\Roaming\Thunderstore Mod Manager\DataFolder\PEAK\profiles\RANKED\BepInEx\config\com.peakcsr.ranked1v1.cfg`（或者直接在 [`src/Plugin.cs`](../src/Plugin.cs) 的默认值里填入），将 `CloudApiUrl` 设置为该地址即可！
