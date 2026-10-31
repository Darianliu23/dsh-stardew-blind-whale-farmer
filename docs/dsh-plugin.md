# dsh-stardew-blind-whale-farmer

在 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness)（DSH）的「设置 → 插件」里调整 **大肥鱼种白饭 Blind Whale Farmer** 星露谷模组。

| 设置 | 默认 | 说明 |
|---|---|---|
| 每次玩几个游戏日 | 0（不限） | 睡一觉算一天。到了以后游戏动作会被拒绝，AI 会停下向你汇报。每次读档重新计 |
| 每次玩多少分钟 | 0（不限） | 现实分钟，从这次第一个动作开始算。到了先让她回家睡觉（会存档），再停下汇报。改这个值会从现在重新计时 |
| 思考时冻结时间 | 开 | AI 思考时游戏时间不走 |
| 空闲多少秒算不玩了 | 120 | 超过这个时间没有新指令，游戏恢复走时 |
| 额外要求 | 空 | 比如“这次专心钓鱼，攒钱买背包”。AI 每次读游戏状态都会看到 |
| 攻略文件 | `guide/zh-CN.md` | 相对游戏 `Mods/BlindWhaleFarmer` 目录 |
| 端口 | 18080 | 和模组 `config.json` 的 `McpPort` 一致 |
| 创建“大肥鱼种白饭”会话预设 | **关** | 装上后默认关闭，要玩时在这里打开。见下方“只在选中的会话里玩” |
| 在所有会话里提供 stardew 入口工具 | **关** | 不换预设、老会话也能玩。见下方“不换预设也能玩：入口工具” |
| 预设名字 | 大肥鱼种白饭 | 新建会话时看到的名字 |
| 基础预设 | standard | 在哪个预设的基础上加星露谷工具 |
| 单个动作最长等多少分钟 | 16 | 钓鱼、下矿一次最长要 15 分钟 |

## 只在选中的会话里玩

DSH 的会话可以选“预设”（agent preset），每个预设带一套自己的插件。在设置里打开开关后，这个插件会注册一个 **“大肥鱼种白饭”预设 = 基础预设（默认 `standard`）的全部插件 + 连游戏的 MCP 客户端**：

- 新建会话时选“大肥鱼种白饭”，这个会话才会连上游戏、拿到 `get_state` 等工具和游戏说明；
- 其他会话（普通聊天、写代码……）里**完全没有**星露谷的工具和说明，不占它们的上下文；
- 每次 DSH 启动都从基础预设现读一遍，DSH 升级 `standard` 时自动跟着变；
- 预设的改动只影响之后新建的会话，已经开着的会话不变。
- **已经对话过的会话用不了**：DSH 规定会话开始聊天后就不能再换预设（源码里会报 `agent-preset/locked`），所以要玩请新建一个会话，在发第一条消息前选这个预设。游戏进度在存档里，AI 用 `update_notes` 写的长期笔记也会出现在每次 `get_state` 的最前面，新会话能接着上次的计划玩。

所以**不要**再在 profile 的 `cordis.patch.yml` 里全局挂 `@deepseek-ai/dsh-mcp-client` 连星露谷，否则每个会话都会带上这些工具。

## 不换预设也能玩：入口工具

打开“在所有会话里提供 stardew 入口工具”后，**每个会话（包括已经聊过的老会话）**都会多一个工具 `stardew`：

- 常驻上下文里只有这一个工具的简短说明（一两百 token），不会把四十多个游戏工具和长说明塞进每个会话；
- AI 要玩时先调 `{"action":"help"}`，拿到操作规则和全部动作的精简列表；攻略用 `{"action":"get_guide"}` 按需读；
- 之后每个动作都是 `{"action":"动作名","args":{参数}}`，例如 `{"action":"move_to","args":{"x":64,"y":15}}`，插件把它转发给游戏里的 MCP 服务器；
- 游戏没开时会直接告诉 AI“连不上游戏”，不会卡住。

和预设比：预设里每个动作是独立的工具、参数检查更严格、规则一开始就在上下文里，专门开会话玩体验更好；入口工具胜在随时随地。两个开关可以同时打开。

## 怎么起作用

模组在游戏里开了一个只监听本机的服务（和 MCP 同一个端口），`/settings` 接收设置。
这个插件每 15 秒问一次游戏“现在用的是哪一版设置”，不一样就推过去：

- 改了设置，十几秒内生效，不用重启游戏和 DSH；
- 游戏没开时什么也不做，开了以后自动补推；
- 模组会把设置写回自己的 `config.json`，不开 DSH 时也照样有效。

## 安装

DSH 的桌面版和网页版用的是不同的 profile（`<DSH_HOME>/profiles/desktop` 和 `profiles/web`），**每个要用的 profile 都要装一次**。

**从 GitHub 装**：插件就在仓库根目录，在 DSH 的插件页面用 `github:Darianliu23/dsh-stardew-blind-whale-farmer` 安装即可（只会装 `lib/`、`cordis.patch.yml` 等几个文件，不会把模组源码装进 DSH）。

**从本地文件夹装（开发时）**：在 profile 目录里

1. `package.json` 的 `dependencies` 加 `"dsh-stardew-blind-whale-farmer": "link:<本仓库路径>"`，`dsh.profile.bundles` 里加 `"dsh-stardew-blind-whale-farmer"`；
2. 在 profile 目录运行 `pnpm install`，然后重启 DSH。

注意用和这个 profile 一致的 pnpm（看 `node_modules/.modules.yaml` 里的 `packageManager`）：桌面版的 profile 是 DSH 桌面版自带的
pnpm 11 建的（在 `DeepSeek Harness/resources/runtime/primary-runtime/dependencies/pnpm`），用别的大版本 pnpm 会要求重建整个 `node_modules`。

设置页的表单需要 `@deepseek-ai/schemastery`。用 link 装时插件会到 `DSH_HOME` 的 profile 目录和 DSH 程序目录里去找；
实在找不到也不会加载失败，只是设置页里没有表单，按默认值工作。

## 测试

```bash
node --test tests/DshPlugin/sync.test.mjs
```

用一个假的游戏 `/settings` 服务测同步逻辑：只在版本不一样时推、游戏重开后重推、改设置推新版本、游戏没开时安静等待；能找到 schemastery 时还会测设置表单。
