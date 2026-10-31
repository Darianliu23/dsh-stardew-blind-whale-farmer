// dsh-stardew-blind-whale-farmer：在 DSH 的「设置 → 插件」里调整大肥鱼种白饭（Blind Whale Farmer）星露谷模组。
//
// 怎么起作用：
//   模组在游戏里开了一个只监听本机的 HTTP 服务（和 MCP 同一个端口），/settings 接收设置。
//   这个插件每 15 秒问一次游戏“你现在用的是哪一版设置”，不一样就把 DSH 里的设置推过去。
//   所以：改了设置马上生效；游戏没开时什么也不做，开了以后自动补推；游戏重开也会重推。
//   模组会把设置写回自己的 config.json，下次不开 DSH 也照样有效。
//
// 为什么 schemastery 要绕一圈找：
//   设置页表单靠导出的 Config（schemastery 写的配置结构）生成。从 npm / GitHub 装的时候，
//   它作为依赖装在插件旁边，直接 import 就行；但用 link: 从本地文件夹装时，插件的真实路径在仓库里，
//   那里找不到它（dsh-speak-up 的作者也在注释里提过这个坑）。所以 import 失败时再去 DSH 自己的
//   目录里找；实在找不到就不导出 Config——插件照样按默认值工作，只是设置页里没有表单，不会整个加载失败。

import { createRequire } from 'node:module'
import { createHash } from 'node:crypto'
import { existsSync, readdirSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { pathToFileURL } from 'node:url'

export const name = 'dsh-stardew-blind-whale-farmer'

const SYNC_EVERY_MS = 15 * 1000

/**
 * 取 DSH 自带的包（@deepseek-ai/schemastery、@deepseek-ai/dsh-tools）：先直接 import，
 * 不行再去 DSH_HOME 的 profile 目录和 DSH 程序目录里找；都找不到返回 null，绝不让插件加载失败。
 */
async function loadHostModule(name) {
  try {
    return await import(name)
  } catch {}
  const bases = []
  // DSH 的 profile 目录（市场插件装在这里，schemastery 通常也在）
  const home = process.env.DSH_HOME
  if (home && existsSync(join(home, 'profiles'))) {
    for (const profile of readdirSync(join(home, 'profiles'))) bases.push(join(home, 'profiles', profile))
  }
  // DSH 主程序所在的目录，一级级往上找 node_modules
  for (const start of [process.argv[1], process.execPath, process.cwd()]) {
    let dir = start ? dirname(start) : null
    for (let i = 0; dir && i < 8; i++, dir = dirname(dir)) bases.push(dir)
  }
  for (const base of bases) {
    try {
      const resolved = createRequire(join(base, 'noop.js')).resolve(name)
      return await import(pathToFileURL(resolved).href)
    } catch {}
  }
  return null
}

const schemasteryModule = await loadHostModule('@deepseek-ai/schemastery')
const z = schemasteryModule ? (schemasteryModule.default ?? schemasteryModule) : null

// defineTool 只做校验、原样返回定义；dsh-tools 在桌面版 profile 里可能 import 不了（它有好几个 peer 依赖），
// 拿不到就用等价的“原样返回”（dsh-fish-memory 也是这么兜底的）
const dshToolsModule = await loadHostModule('@deepseek-ai/dsh-tools')
const defineTool = dshToolsModule?.defineTool ?? ((definition) => definition)

const DEFAULTS = {
  port: 18080,
  sessionDays: 0,
  sessionMinutes: 0,
  freezeTime: true,
  idleSeconds: 120,
  extraInstructions: '',
  guideFile: 'guide/zh-CN.md',
  presetEnabled: false,
  gatewayEnabled: false,
  presetName: '大肥鱼种白饭',
  basePreset: 'standard',
  toolTimeoutMinutes: 16,
}

/** 我们注册的会话预设 id（会话里保存的就是它）。 */
export const PRESET_ID = 'dsh-stardew-blind-whale-farmer'

export const Config = z
  ? z.object({
      sessionDays: z.natural().default(DEFAULTS.sessionDays)
        .description('每次游玩最多玩几个游戏日（睡一觉算一天），到了让她停下向你汇报。0 = 不限。每次读档重新计。'),
      sessionMinutes: z.natural().default(DEFAULTS.sessionMinutes)
        .description('每次游玩最多玩多少现实分钟（从这次第一个动作开始算），到了让她回家睡觉存档后停下。0 = 不限。改这个值会从现在重新计时。'),
      freezeTime: z.boolean().default(DEFAULTS.freezeTime)
        .description('AI 思考时冻结游戏时间（思考不占游戏里的时间）'),
      idleSeconds: z.number().min(10).max(3600).step(1).default(DEFAULTS.idleSeconds)
        .description('多少秒没有新指令就当 AI 不玩了，恢复游戏走时'),
      extraInstructions: z.string().role('textarea').default(DEFAULTS.extraInstructions)
        .description('给 AI 的额外要求，比如“这次专心钓鱼，攒钱买背包”。每次读游戏状态时都会看到。'),
      guideFile: z.string().default(DEFAULTS.guideFile)
        .description('攻略文件（相对游戏 Mods/DeepSeekPlaysStardew 目录）。想换打法就复制一份改名后填在这里。'),
      port: z.number().min(1).max(65535).step(1).default(DEFAULTS.port)
        .description('游戏里模组 MCP 服务器的端口（和模组 config.json 的 McpPort 一致）'),
      presetEnabled: z.boolean().default(DEFAULTS.presetEnabled)
        .description(
          '开启后会多出一个“大肥鱼种白饭”会话预设，新建会话时选它就能玩星露谷；只有选了它的会话才会连上游戏、加载玩游戏的工具和说明，其他会话的上下文完全不受影响。\n'
          + '⚠ 已经对话过的会话用不了：DSH 规定会话开始聊天后就不能再换预设（这是 DSH 的限制，插件改不了），所以要玩请新建一个会话、在发第一条消息前选这个预设。\n'
          + '不用担心接不上：游戏进度存在存档里，AI 用 update_notes 写的长期笔记也会出现在每次读取的游戏状态最前面，新会话能接着上次的计划玩。\n'
          + '开关改了以后如果预设列表里没有变化，重启一下 DSH。'),
      gatewayEnabled: z.boolean().default(DEFAULTS.gatewayEnabled)
        .description(
          '开启后，所有会话（包括已经聊过的老会话）都会多一个小工具“stardew”，不用换预设也能玩星露谷。\n'
          + '它只是一个入口：常驻上下文里只有这一个工具的简短说明（一两百 token），不会把四十多个游戏工具和长说明塞进每个会话；'
          + 'AI 真要玩时会先调 action="help" 把操作规则和动作列表读进那个会话，攻略用 action="get_guide" 按需读。\n'
          + '和“大肥鱼种白饭”预设的区别：预设里每个动作都是独立的工具、参数检查更严格、规则一开始就在上下文里，专门开会话玩体验更好；'
          + '入口工具胜在随时随地，聊天时顺口一句“去看看农场”就能玩。\n'
          + '已经开着的会话一般下一轮就能用上；如果没有出现，重启一下 DSH。'),
      presetName: z.string().default(DEFAULTS.presetName)
        .description('这个预设在会话里显示的名字'),
      basePreset: z.string().default(DEFAULTS.basePreset)
        .description('在哪个预设的基础上加星露谷工具（默认 standard：和普通会话一样的能力，再多一套玩游戏的工具）'),
      toolTimeoutMinutes: z.number().min(1).max(60).step(1).default(DEFAULTS.toolTimeoutMinutes)
        .description('单个游戏动作最长等多少分钟（钓鱼、下矿一次最长要 15 分钟）'),
    })
  : undefined

/** 推给模组的设置（字段名和模组 McpServer 的 /settings 对应）。 */
function settingsOf(config) {
  const c = { ...DEFAULTS, ...(config ?? {}) }
  return {
    sessionDays: c.sessionDays,
    sessionMinutes: c.sessionMinutes,
    freezeTime: c.freezeTime,
    idleSeconds: c.idleSeconds,
    extraInstructions: c.extraInstructions ?? '',
    guideFile: c.guideFile ?? '',
  }
}

export function apply(ctx, config) {
  const log = (level, msg) => {
    try { ctx.logger?.(level, '[dsh-stardew-blind-whale-farmer] ' + msg) } catch {}
  }
  if (!z) log('warn', '找不到 @deepseek-ai/schemastery，设置页里不会有表单；插件按默认设置工作')

  const port = Number(config?.port ?? DEFAULTS.port)
  const url = `http://127.0.0.1:${port}/settings`
  const settings = settingsOf(config)
  // 版本号 = 设置内容的哈希：游戏那边记着上次收到的版本号，不一样（改了设置、或者游戏重开过）才推
  const revision = createHash('sha256').update(JSON.stringify(settings)).digest('hex').slice(0, 16)
  let running = false

  const sync = async () => {
    if (running) return
    running = true
    try {
      const current = await fetch(url, { signal: AbortSignal.timeout(3000) }).then((r) => r.json())
      if (current?.revision === revision) return
      const res = await fetch(url, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify({ revision, settings }),
        signal: AbortSignal.timeout(5000),
      })
      const body = await res.json().catch(() => ({}))
      if (res.ok) log('info', body?.message ?? '已把设置推送到游戏')
      else log('warn', `游戏拒绝了设置（HTTP ${res.status}）：${body?.error ?? ''}`)
    } catch {
      // 游戏没开或者还在加载：下一轮再试
    } finally {
      running = false
    }
  }

  ctx.effect(() => {
    sync()
    const timer = setInterval(sync, SYNC_EVERY_MS)
    return () => clearInterval(timer)
  })

  if (config?.presetEnabled ?? DEFAULTS.presetEnabled) registerPreset(ctx, config, log)
  if (config?.gatewayEnabled ?? DEFAULTS.gatewayEnabled) registerGateway(ctx, config, log)
}

/** 预设里连游戏的 MCP 客户端这一行（DSH 自带的 @deepseek-ai/dsh-mcp-client）。 */
export function mcpClientRow(config) {
  const c = { ...DEFAULTS, ...(config ?? {}) }
  return {
    id: 'mcp-stardew',
    name: '@deepseek-ai/dsh-mcp-client',
    config: {
      serverName: 'stardew',
      transport: 'streamable-http',
      url: `http://127.0.0.1:${c.port}/mcp`,
      toolCallTimeoutMs: c.toolTimeoutMinutes * 60 * 1000,
      // 先开 DSH 后开游戏也没关系：一直在后台重连（间隔最长 30 秒，100 次约 50 分钟）
      reconnect: { maxAttempts: 100 },
    },
  }
}

/**
 * 注册“星露谷”会话预设 = 基础预设（默认 standard）的全部子插件 + 连游戏的 MCP 客户端。
 * 只有选了这个预设的会话才会加载星露谷的工具和说明，其他会话的上下文里没有它们。
 *
 * 每次 DSH 启动都从基础预设现读一遍，DSH 升级 standard 时这里自动跟着变，不会留着旧副本。
 * 不在 inject 里声明 agentPresets：cordis 只有“必需依赖”，声明了的话没有预设服务的 DSH 上
 * 整个插件（包括设置同步）都起不来；所以运行时去取，取不到就只做设置同步。
 */
function registerPreset(ctx, config, log) {
  const c = { ...DEFAULTS, ...(config ?? {}) }
  ctx.effect(() => {
    let disposed = false
    let unregister = null
    let tries = 0
    const attempt = async () => {
      if (disposed) return
      let presets
      try {
        presets = ctx.get?.('agentPresets')
      } catch {}
      const base = presets?.definitions?.get?.(c.basePreset)
      if (!presets || !base) {
        // 预设都是启动时注册的，可能比本插件晚一点；等 2 分钟还没有就放弃
        if (++tries <= 60) timer = setTimeout(attempt, 2000)
        else log('warn', presets ? `找不到基础预设“${c.basePreset}”，没有创建星露谷预设` : '这个版本的 DSH 没有会话预设服务，没有创建星露谷预设')
        return
      }
      if (presets.definitions.has(PRESET_ID)) {
        log('warn', `已经有 id 为“${PRESET_ID}”的预设了（可能在 profile 里手动写过），没有重复创建`)
        return
      }
      try {
        const remove = await presets.register({
          id: PRESET_ID,
          name: c.presetName,
          description: `在“${c.basePreset}”预设的基础上，加上玩星露谷的工具（需要游戏开着、装了大肥鱼种白饭模组）`,
          order: 50,
          // 每一行浅拷贝一份：Loader 可能会给行补 id 之类的字段，别改到 standard 自己的数据
          plugins: [...base.config.plugins.map((row) => ({ ...row })), mcpClientRow(c)],
        })
        if (disposed) await remove?.()
        else {
          unregister = remove
          log('info', `已创建会话预设“${c.presetName}”（${PRESET_ID}）：新建会话时选它就能玩星露谷`)
        }
      } catch (error) {
        log('warn', '创建星露谷预设失败：' + (error?.message ?? error))
      }
    }
    let timer = setTimeout(attempt, 0)
    return () => {
      disposed = true
      clearTimeout(timer)
      unregister?.()
    }
  })
}

// ───────────────────────── 单入口工具 stardew ─────────────────────────

/** 入口工具的名字。 */
export const GATEWAY_TOOL = 'stardew'

/** 发一条 JSON-RPC 给游戏里的 MCP 服务器（和预设里的 MCP 客户端走同一个地址），返回 result。 */
async function mcpCall(port, method, params, signal) {
  const res = await fetch(`http://127.0.0.1:${port}/mcp`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', accept: 'application/json, text/event-stream' },
    body: JSON.stringify({ jsonrpc: '2.0', id: Date.now(), method, params }),
    signal,
  })
  const body = await res.json()
  if (body.error) throw new Error(body.error.message ?? `MCP 错误 ${body.error.code}`)
  return body.result
}

/**
 * action="help" 的内容：操作规则 + MCP 说明 + 精简的动作列表。
 * 连接说明里还夹着整份攻略（“## 游戏常识”到“## 通过 MCP 玩的额外说明”之间），这里去掉，要看再用 get_guide。
 */
export function helpText(instructions, tools) {
  let rules = instructions ?? ''
  const guideStart = rules.indexOf('## 游戏常识')
  const mcpNotes = rules.indexOf('## 通过 MCP 玩的额外说明')
  if (guideStart >= 0 && mcpNotes > guideStart) rules = rules.slice(0, guideStart) + rules.slice(mcpNotes)
  const lines = tools.map((t) => {
    const props = t.inputSchema?.properties ?? {}
    const required = new Set(t.inputSchema?.required ?? [])
    const params = Object.keys(props).map((k) => (required.has(k) ? k : k + '?')).join(', ')
    const summary = String(t.description ?? '').split(/[。.\n]/)[0]
    return `- ${t.name}(${params})：${summary}`
  })
  return [
    `# 用 ${GATEWAY_TOOL} 工具玩星露谷`,
    `下面说的每个“工具”，都通过 ${GATEWAY_TOOL} 工具调用：action 填动作名，args 填参数对象，例如 {"action":"move_to","args":{"x":64,"y":15}}。参数后面带 ? 的可以不填。`,
    '',
    rules.trim(),
    '',
    `## 全部动作（${tools.length} 个；不确定参数含义时，先按名字和说明猜，失败信息会告诉你哪里不对）`,
    ...lines,
  ].join('\n')
}

/**
 * 在所有会话里注册一个入口工具：常驻上下文里只有它自己的简短说明，
 * 真要玩时 action="help" 再把规则和动作列表读进来；具体动作转发给游戏里的 MCP 服务器。
 * 和预设一样不在 inject 里声明 tools，运行时去取，取不到就不注册（不影响设置同步）。
 */
function registerGateway(ctx, config, log) {
  const c = { ...DEFAULTS, ...(config ?? {}) }
  const timeoutMs = c.toolTimeoutMinutes * 60 * 1000
  const tool = defineTool({
    name: GATEWAY_TOOL,
    description:
      '玩《星露谷物语》（需要游戏开着、装了“大肥鱼种白饭”模组）。第一次用先调 action="help"，会告诉你操作规则和全部动作；'
      + '每次行动前先 action="get_state" 看游戏状态；然后 action=动作名、args=参数对象，比如 {"action":"water_crops"}、{"action":"move_to","args":{"x":64,"y":15}}。'
      + '攻略用 action="get_guide"。',
    parameters: {
      action: { type: 'string', required: true, description: '动作名：help、get_state、get_guide，或 help 列出的任意动作' },
      args: { type: 'object', additionalProperties: true, description: '动作的参数对象；没有参数就不填' },
    },
    output: {
      schema: { type: 'string' },
      render: (_args, value) => [{ type: 'text', text: String(value) }],
    },
    async execute(args, exec) {
      const action = String(args?.action ?? '').trim()
      const signal = exec?.signal ? AbortSignal.any([exec.signal, AbortSignal.timeout(timeoutMs)]) : AbortSignal.timeout(timeoutMs)
      try {
        if (!action || action === 'help') {
          const init = await mcpCall(c.port, 'initialize', {
            protocolVersion: '2025-06-18', capabilities: {}, clientInfo: { name: 'dsh-stardew-gateway', version: '1' },
          }, signal)
          const list = await mcpCall(c.port, 'tools/list', {}, signal)
          return helpText(init?.instructions, list?.tools ?? [])
        }
        const result = await mcpCall(c.port, 'tools/call', { name: action, arguments: args?.args ?? {} }, signal)
        const text = (result?.content ?? []).filter((p) => p.type === 'text').map((p) => p.text).join('\n')
        return text || '（游戏没有返回内容）'
      } catch (error) {
        if (signal.aborted && !exec?.signal?.aborted) return `失败：等了 ${c.toolTimeoutMinutes} 分钟还没做完，已放弃等待（游戏里的动作可能还在继续）。先 action="get_state" 看看现在的情况`
        const reason = error?.cause?.code === 'ECONNREFUSED' || /fetch failed/i.test(String(error?.message))
          ? '连不上游戏：请确认星露谷已经通过 SMAPI 启动、装了“大肥鱼种白饭”模组，并且读取了存档'
          : String(error?.message ?? error)
        return '失败：' + reason
      }
    },
  })
  ctx.effect(() => {
    let tools
    try {
      tools = ctx.get?.('tools')
    } catch {}
    if (!tools?.register) {
      log('warn', '这个版本的 DSH 没有工具注册服务，没有添加 stardew 入口工具')
      return () => {}
    }
    let off
    try {
      off = tools.register(tool)
      log('info', '已在所有会话里添加 stardew 入口工具')
    } catch (error) {
      log('warn', '添加 stardew 入口工具失败：' + (error?.message ?? error))
    }
    return () => {
      try { typeof off === 'function' ? off() : off?.dispose?.() } catch {}
    }
  })
}
