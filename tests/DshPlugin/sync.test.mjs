// 用一个假的“游戏 /settings 服务”测同步逻辑：只在版本号不一样时推、游戏重开后重推、改设置推新版本。
// 运行：node --test tests/DshPlugin/sync.test.mjs（设置 DSH_HOME 指向 DSH 的家目录时，还会测设置页表单）
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'

// 设置备份写到临时目录，别动真的 DSH 目录
const { tmpdir } = await import('node:os')
process.env.DSH_STARDEW_BACKUP = (await import('node:path')).join(tmpdir(), `bwf-sync-test-${process.pid}.json`)
// 同版本的空备份：插件就不会去读本机游戏里模组的 config.json 来恢复设置（测试要的是默认值）
{
  const fs = await import('node:fs')
  const version = JSON.parse(fs.readFileSync(new URL('../../package.json', import.meta.url), 'utf8')).version
  fs.writeFileSync(process.env.DSH_STARDEW_BACKUP, JSON.stringify({ version, settings: {} }))
}
const plugin = await import('../../lib/index.js')

function fakeGame() {
  const state = { revision: '', posts: [], thoughts: [] }
  const server = createServer((req, res) => {
    let body = ''
    req.on('data', (c) => (body += c))
    req.on('end', () => {
      if (req.url === '/thought' && req.method === 'POST') {
        state.thoughts.push(JSON.parse(body))
        res.end(JSON.stringify({ ok: true }))
      } else if (req.method === 'POST') {
        const msg = JSON.parse(body)
        state.revision = msg.revision
        state.posts.push(msg)
        res.end(JSON.stringify({ message: '已应用', revision: state.revision }))
      } else {
        res.end(JSON.stringify({
          instance: 'x',
          revision: state.revision,
          session: state.session ?? '不限时长',
          sessionEnded: state.sessionEnded ?? false,
          playerInControl: state.playerInControl ?? false,
          agentRunning: state.agentRunning ?? false,
          goal: state.goal ?? null,
        }))
      }
    })
  })
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve({ server, state, port: server.address().port })))
}

function fakeCtx(services = {}) {
  const logs = []
  const cleanups = []
  const handlers = new Map()
  return {
    logs,
    ctx: {
      logger: (level, msg) => logs.push(`${level} ${msg}`),
      effect: (fn) => { cleanups.push(fn()) },
      get: (name) => services[name],
      on: (name, fn) => {
        handlers.set(name, [...(handlers.get(name) ?? []), fn])
        return () => handlers.set(name, handlers.get(name).filter((f) => f !== fn))
      },
    },
    emit: (name, ...args) => (handlers.get(name) ?? []).forEach((fn) => fn(...args)),
    dispose: () => cleanups.forEach((c) => c?.()),
  }
}

/** 假的会话预设注册表：和 @deepseek-ai/dsh-agent-preset-registry 一样有 definitions 和 register。 */
function fakePresets(basePlugins = [{ id: 'persona' }, { id: 'tool-bash' }]) {
  const definitions = new Map([['standard', { config: { id: 'standard', plugins: basePlugins } }]])
  const registered = []
  return {
    definitions,
    registered,
    async register(def) {
      definitions.set(def.id, { config: def })
      registered.push(def)
      return async () => { definitions.delete(def.id) }
    },
  }
}

const wait = (ms) => new Promise((r) => setTimeout(r, ms))

test('第一次推送设置，之后版本号一样就不重复推', async () => {
  const { server, state, port } = await fakeGame()
  const { ctx, dispose } = fakeCtx()
  plugin.apply(ctx, { port, sessionDays: 2, sessionMinutes: 30, extraInstructions: '专心钓鱼' })
  await wait(300)
  assert.equal(state.posts.length, 1)
  assert.deepEqual(state.posts[0].settings, {
    sessionDays: 2, sessionMinutes: 30, freezeTime: true, idleSeconds: 120, forageRadius: 1, companionMode: 'hybrid',
    allowSpendSharedMoney: false, aiHomeLocation: '', coSleepDefault: '',
    passbyEmoteEnabled: true, passbyEmoteChance: 0.35, passbyEmoteCooldownMinutes: 5,
    idleFollowEnabled: true, idleFollowIntervalSeconds: 180, idleFollowDurationSeconds: 30,
    proactiveGiftEnabled: true, proactiveGiftChance: 0.5, proactiveGiftCooldownDays: 1,
    extraInstructions: '专心钓鱼', reservedItems: '', guideFile: 'guide/攻略.md',
    guides: {},
  })
  dispose()
  // 同样的设置再启动一次（相当于 DSH 重启）：游戏已经是这个版本，不再推
  const again = fakeCtx()
  plugin.apply(again.ctx, { port, sessionDays: 2, sessionMinutes: 30, extraInstructions: '专心钓鱼' })
  await wait(300)
  assert.equal(state.posts.length, 1)
  again.dispose()
  server.close()
})

test('游戏重开（忘了版本号）或者改了设置，会重新推', async () => {
  const { server, state, port } = await fakeGame()
  const first = fakeCtx()
  plugin.apply(first.ctx, { port, sessionDays: 1 })
  await wait(300)
  first.dispose()
  state.revision = '' // 游戏重开
  const second = fakeCtx()
  plugin.apply(second.ctx, { port, sessionDays: 1 })
  await wait(300)
  second.dispose()
  const third = fakeCtx()
  plugin.apply(third.ctx, { port, sessionDays: 5 }) // 改了设置
  await wait(300)
  third.dispose()
  assert.equal(state.posts.length, 3)
  assert.equal(state.posts[2].settings.sessionDays, 5)
  assert.notEqual(state.posts[0].revision, state.posts[2].revision)
  server.close()
})

test('攻略：设置页默认填好插件自带的攻略；只推玩家改过的篇目', async () => {
  assert.ok(plugin.BUNDLED_GUIDES.guideMain.includes('游戏常识'), '主攻略要读进来当默认值')
  const { server, state, port } = await fakeGame()
  const f = fakeCtx()
  try {
    // 没改、清空都不推；改了钓鱼专题才推
    plugin.apply(f.ctx, { port, guideMain: plugin.BUNDLED_GUIDES.guideMain, guideMining: '', guideFishing: '只钓鲟鱼' })
    await wait(300)
    assert.deepEqual(state.posts[0].settings.guides, { 钓鱼: '只钓鲟鱼' })
  } finally {
    f.dispose()
    server.close()
  }
})

test('游戏没开时安静地等，不报错', async () => {
  const { ctx, logs, dispose } = fakeCtx()
  plugin.apply(ctx, { port: 1 }) // 没人监听的端口
  await wait(300)
  dispose()
  assert.ok(!logs.some((l) => l.startsWith('error')))
})

test('注册“星露谷”会话预设 = standard 的全部插件 + 连游戏的 MCP 客户端；卸载时移除', async () => {
  const presets = fakePresets()
  const { ctx, logs, dispose } = fakeCtx({ agentPresets: presets })
  plugin.apply(ctx, { port: 1, presetEnabled: true, presetName: '大肥鱼种白饭', toolTimeoutMinutes: 16 })
  await wait(100)
  assert.equal(presets.registered.length, 1)
  const def = presets.registered[0]
  assert.equal(def.id, 'dsh-stardew-blind-whale-farmer')
  assert.equal(def.name, '大肥鱼种白饭')
  assert.deepEqual(def.plugins.slice(0, 2), [{ id: 'persona' }, { id: 'tool-bash' }])
  const mcp = def.plugins[2]
  assert.equal(mcp.name, '@deepseek-ai/dsh-mcp-client')
  assert.equal(mcp.config.url, 'http://127.0.0.1:1/mcp')
  assert.equal(mcp.config.transport, 'streamable-http')
  assert.equal(mcp.config.toolCallTimeoutMs, 16 * 60 * 1000)
  assert.ok(logs.some((l) => l.includes('已创建会话预设')))
  dispose()
  await wait(10)
  assert.ok(!presets.definitions.has('dsh-stardew-blind-whale-farmer'), '卸载插件时预设也要移除')
})

test('默认不创建预设：装上插件后要在设置里手动开启', async () => {
  const presets = fakePresets()
  const { ctx, dispose } = fakeCtx({ agentPresets: presets })
  plugin.apply(ctx, { port: 1 })
  await wait(100)
  assert.equal(presets.registered.length, 0)
  dispose()
})

test('预设服务比插件晚出现也能注册；关掉开关就不注册；不会和同名预设冲突', async () => {
  const late = fakePresets()
  const services = {}
  const a = fakeCtx(services)
  plugin.apply(a.ctx, { port: 1, presetEnabled: true })
  await wait(100)
  services.agentPresets = late // 两秒一轮的重试里补上
  await wait(2300)
  assert.equal(late.registered.length, 1)
  a.dispose()

  const off = fakePresets()
  const b = fakeCtx({ agentPresets: off })
  plugin.apply(b.ctx, { port: 1, presetEnabled: false })
  await wait(100)
  assert.equal(off.registered.length, 0)
  b.dispose()

  const taken = fakePresets()
  taken.definitions.set('dsh-stardew-blind-whale-farmer', { config: { id: 'dsh-stardew-blind-whale-farmer', plugins: [] } })
  const c = fakeCtx({ agentPresets: taken })
  plugin.apply(c.ctx, { port: 1, presetEnabled: true })
  await wait(100)
  assert.equal(taken.registered.length, 0)
  assert.ok(c.logs.some((l) => l.includes('没有重复创建')))
  c.dispose()
})

/** 假的游戏 MCP 服务器：initialize 返回“规则 + 攻略 + MCP 说明”，tools/list 两个工具，tools/call 记下参数。 */
function fakeGameMcp() {
  const calls = []
  const server = createServer((req, res) => {
    let body = ''
    req.on('data', (c) => (body += c))
    req.on('end', () => {
      // 插件的设置同步也会来 GET/POST /settings，这里只管 MCP
      if (req.url !== '/mcp') return res.end(JSON.stringify({ revision: 'x' }))
      const msg = JSON.parse(body)
      let result
      if (msg.method === 'initialize')
        result = { instructions: '## 行动原则\n先看状态。\n\n## 游戏常识\n很长的攻略……\n\n## 通过 MCP 玩的额外说明\n先 get_state。' }
      else if (msg.method === 'tools/list')
        result = { tools: [
          { name: 'get_state', description: '读取当前游戏状态。每次行动前先调用。', inputSchema: { type: 'object', properties: {} } },
          { name: 'move_to', description: '走到某一格。会自动寻路', inputSchema: { type: 'object', properties: { x: {}, y: {}, run: {} }, required: ['x', 'y'] } },
        ] }
      else if (msg.method === 'tools/call') {
        // 和真的模组一样：工具调用用 SSE 回复，先发心跳注释，再发结果事件
        calls.push(msg.params)
        result = { content: [{ type: 'text', text: `成功：${msg.params.name} ${JSON.stringify(msg.params.arguments)}` }], isError: false }
        res.setHeader('content-type', 'text/event-stream; charset=utf-8')
        res.write(': start\n\n: keep-alive\n\n')
        return res.end(`event: message\ndata: ${JSON.stringify({ jsonrpc: '2.0', id: msg.id, result })}\n\n`)
      }
      res.end(JSON.stringify({ jsonrpc: '2.0', id: msg.id, result }))
    })
  })
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve({ server, calls, port: server.address().port })))
}

function fakeToolRegistry() {
  const tools = new Map()
  return { tools, register(def) { tools.set(def.name, def); return () => tools.delete(def.name) } }
}

test('入口工具默认不注册；开启后注册 stardew，卸载时移除', async () => {
  const reg = fakeToolRegistry()
  const off = fakeCtx({ tools: reg })
  plugin.apply(off.ctx, { port: 1 })
  assert.equal(reg.tools.size, 0)
  off.dispose()

  const on = fakeCtx({ tools: reg })
  try {
    plugin.apply(on.ctx, { port: 1, gatewayEnabled: true })
    assert.ok(reg.tools.has('stardew'))
    const def = reg.tools.get('stardew')
    assert.ok(def.description.length < 400, '常驻说明要短：' + def.description.length)
    // 找得到 DSH 的 dsh-tools 时，defineTool 会把简写参数转成标准 JSON Schema（参数在 properties 里）；找不到时原样保留
    const params = def.parameters.properties ?? def.parameters
    assert.equal(params.args.additionalProperties, true)
    assert.ok(params.action)
  } finally {
    on.dispose()
  }
  assert.equal(reg.tools.size, 0)
})

test('入口工具：help 给规则和动作列表（不含整份攻略），其他 action 转发给游戏', async () => {
  const { server, calls, port } = await fakeGameMcp()
  const reg = fakeToolRegistry()
  const { ctx, dispose } = fakeCtx({ tools: reg })
  try {
    plugin.apply(ctx, { port, gatewayEnabled: true })
    const tool = reg.tools.get('stardew')

    const help = await tool.execute({ action: 'help' }, {})
    assert.ok(help.includes('## 行动原则') && help.includes('## 通过 MCP 玩的额外说明'))
    assert.ok(!help.includes('很长的攻略'), 'help 里不应该带攻略全文')
    assert.ok(help.includes('- move_to(x, y, run?)：走到某一格'))
    assert.ok(help.includes('- get_state()：读取当前游戏状态'))

    const moved = await tool.execute({ action: 'move_to', args: { x: 64, y: 15 } }, {})
    assert.equal(moved, '成功：move_to {"x":64,"y":15}')
    const state = await tool.execute({ action: 'get_state' }, {})
    assert.equal(calls.at(-1).name, 'get_state')
    assert.deepEqual(calls.at(-1).arguments, {})
    assert.ok(state.startsWith('成功：get_state'))
  } finally {
    dispose()
    server.close()
  }
})

test('SSE 解析：跳过心跳注释，取出结果；没有结果时报错', () => {
  const ok = plugin.parseSseResult(': start\r\n\r\n: keep-alive\n\nevent: message\ndata: {"jsonrpc":"2.0","id":1,"result":{"x":1}}\n\n')
  assert.deepEqual(ok.result, { x: 1 })
  assert.throws(() => plugin.parseSseResult(': start\n\n: keep-alive\n\n'), /没有结果/)
})

test('入口工具：游戏没开时给出看得懂的提示', async () => {
  const reg = fakeToolRegistry()
  const { ctx, dispose } = fakeCtx({ tools: reg })
  plugin.apply(ctx, { port: 1, gatewayEnabled: true })
  const text = await reg.tools.get('stardew').execute({ action: 'get_state' }, {})
  assert.ok(text.startsWith('失败：连不上游戏'), text)
  dispose()
})

/** 假的会话：followup 记下收到的消息；status 可以改。 */
function fakeAgents() {
  const agent = { status: 'idle', inbox: { nextTurn: [] }, sent: [], followup(m) { this.sent.push(m) } }
  return { agent, service: { get: (id) => (id === 's1' ? agent : undefined) } }
}

/** 按 DSH 会话日志里的真实事件顺序回放一轮：调游戏工具 → 结果 → 最后一条回复 → 回合结束。 */
function playTurn(emit, { tool = 'mcp__stardew__water_crops', args = '{}', ok = true, resultContent = [], last, reason = 'completed' }) {
  const session = { id: 's1' }
  const ev = (type, data) => emit('session/event', session, { type, data })
  ev('turn/start', { turn: 1 })
  ev('tool/call', { callId: 'c1', name: tool, arguments: args })
  ev('tool/result', { message: { role: 'tool', toolCallId: 'c1', isError: !ok, content: resultContent } })
  ev('assistant/message', { message: { role: 'assistant', content: last } })
  ev('turn/end', { reason: { kind: reason } })
}

// 2026-11-01 DSH 会话日志里真实出现的坏回复
const BROKEN = [{ type: 'reasoning', text: ' ' }, { type: 'text', text: '</｜｜DSML｜｜ parameter>\n</｜｜DSML｜｜ invoke>\n</｜｜DSML｜｜ calls>' }]

test('自动续玩：玩游戏的会话里模型回复坏了，等空闲后发“接着玩”', async () => {
  const { agent, service } = fakeAgents()
  const f = fakeCtx({ agents: service })
  try {
    plugin.apply(f.ctx, { port: 1 })
    agent.status = 'running'
    playTurn(f.emit, { last: BROKEN })
    await wait(1700)
    assert.equal(agent.sent.length, 0, '还在跑的时候不发')
    agent.status = 'idle'
    await wait(1600)
    assert.equal(agent.sent.length, 1)
    assert.equal(agent.sent[0].role, 'user')
    assert.equal(agent.sent[0].content[0].text, plugin.AUTO_CONTINUE_TEXT)
    assert.ok(f.logs.some((l) => l.includes('已自动发')))
  } finally {
    f.dispose()
  }
})

test('自动续玩：正常说完、没玩游戏、被打断、关掉开关、用户已经发了话时都不发', async () => {
  const cases = [
    { config: {}, turn: { last: [{ type: 'text', text: '今天先玩到这里。' }] } },
    { config: {}, turn: { tool: 'bash', last: BROKEN } },
    { config: {}, turn: { last: BROKEN, reason: 'aborted' } },
    { config: { autoContinue: false }, turn: { last: BROKEN } },
    { config: {}, turn: { last: BROKEN }, userTyped: true },
  ]
  for (const c of cases) {
    const { agent, service } = fakeAgents()
    if (c.userTyped) agent.inbox.nextTurn.push({ id: 'u' })
    const f = fakeCtx({ agents: service })
    try {
      plugin.apply(f.ctx, { port: 1, ...c.config })
      playTurn(f.emit, c.turn)
      await wait(1700)
      assert.equal(agent.sent.length, 0, JSON.stringify(c))
    } finally {
      f.dispose()
    }
  }
})

test('自动续玩：连续坏掉超过上限就停；中间有一次游戏动作成功就重新计数', async () => {
  const { agent, service } = fakeAgents()
  const f = fakeCtx({ agents: service })
  try {
    plugin.apply(f.ctx, { port: 1, autoContinueMax: 2 })
    // 每轮的游戏动作都失败：只续 2 次
    for (let i = 0; i < 3; i++) playTurn(f.emit, { ok: false, last: BROKEN })
    await wait(1700)
    assert.equal(agent.sent.length, 2)
    assert.ok(f.logs.some((l) => l.includes('不再自动续玩')))
    // 一次成功的游戏动作之后又能续了
    playTurn(f.emit, { ok: true, last: BROKEN })
    await wait(1700)
    assert.equal(agent.sent.length, 3)
  } finally {
    f.dispose()
  }
})

test('配置结构和默认值（需要能找到 schemastery）', { skip: !plugin.Config && '没找到 schemastery（设置 DSH_HOME 再测）' }, () => {
  // 字段是 volatile 的活引用，用 currentConfig 取值
  const resolved = plugin.currentConfig(plugin.Config({}))
  assert.equal(resolved.sessionDays, 3)
  assert.equal(resolved.sessionMinutes, 60)
  assert.equal(resolved.freezeTime, true)
  assert.equal(resolved.port, 18080)
  assert.equal(resolved.guideFile, 'guide/攻略.md')
  assert.equal(resolved.presetEnabled, false, '会话预设默认关闭')
  assert.equal(resolved.gatewayEnabled, false, '入口工具默认关闭')
  assert.equal(resolved.autoContinue, true, '自动续玩默认开启')
  assert.equal(resolved.keepAwake, true, '保持唤醒默认开启')
  assert.equal(resolved.autoContinueDays, 3, '睡醒自动续玩默认 3 天')
  assert.equal(resolved.coSleepDefault, '')
  assert.equal(resolved.passbyEmoteEnabled, true)
  assert.equal(resolved.passbyEmoteChance, 0.35)
  assert.equal(resolved.passbyEmoteCooldownMinutes, 5)
  assert.equal(resolved.idleFollowEnabled, true)
  assert.equal(resolved.idleFollowIntervalSeconds, 180)
  assert.equal(resolved.idleFollowDurationSeconds, 30)
  assert.equal(resolved.proactiveGiftEnabled, true)
  assert.equal(resolved.proactiveGiftChance, 0.5)
  assert.equal(resolved.proactiveGiftCooldownDays, 1)
  assert.throws(() => plugin.Config({ sessionDays: -1 }))
  assert.throws(() => plugin.Config({ proactiveGiftChance: 1.5 }))
  assert.throws(() => plugin.Config({ proactiveGiftCooldownDays: 0 }))
  assert.throws(() => plugin.Config({ proactiveGiftCooldownDays: 29 }))
  assert.throws(() => plugin.Config({ autoContinueDays: -1 }))
  assert.throws(() => plugin.Config({ autoContinueDays: 31 }))
})

test('设置是 volatile：DSH 交来的是活引用，配置卡片改了值不重启插件，下一轮同步就推新值', { timeout: 40000 }, async () => {
  const { server, state, port } = await fakeGame()
  const { ctx, dispose } = fakeCtx()
  // 和 DSH 一样用 Config 解析：volatile 字段变成 { get() } 活引用
  const config = plugin.Config({ port, forageRadius: 2 })
  assert.equal(typeof config.forageRadius.get, 'function')
  assert.equal(plugin.currentConfig(config).forageRadius, 2)
  plugin.apply(ctx, config)
  await wait(300)
  assert.equal(state.posts.at(-1).settings.forageRadius, 2)
  // 卡片保存后 DSH 就地改引用里的值（这里直接写，效果一样）
  const write = Object.getOwnPropertySymbols(config.forageRadius)[0]
  config.forageRadius[write](5)
  await wait(15500)
  assert.equal(state.posts.length, 2)
  assert.equal(state.posts.at(-1).settings.forageRadius, 5)
  dispose()
  server.close()
})

test('按时长续玩：玩家要的时长没玩够、AI 正常说完就停了，自动发“接着玩”；玩够了、被打断、没玩游戏都不发', async () => {
  const { server, state, port } = await fakeGame()
  try {
    // 没玩够：发一条带剩余时间的“接着玩”
    {
      const { agent, service } = fakeAgents()
      const f = fakeCtx({ agents: service })
      try {
        state.goal = { minutes: 30, played: 2.4, pending: true }
        plugin.apply(f.ctx, { port })
        playTurn(f.emit, { last: [{ type: 'text', text: '爸爸～本鱼把 35 株全浇了一遍' }] })
        await wait(1900)
        assert.equal(agent.sent.length, 1)
        assert.equal(agent.sent[0].content[0].text, plugin.goalContinueText(state.goal))
        assert.ok(agent.sent[0].content[0].text.includes('还剩约 28 分钟'))
        assert.ok(f.logs.some((l) => l.includes('玩家要玩 30 分钟，还没玩够')))
      } finally {
        f.dispose()
      }
    }
    // 按游戏日：“玩 4 个游戏日”睡了一觉就停了
    {
      const { agent, service } = fakeAgents()
      const f = fakeCtx({ agents: service })
      try {
        state.goal = { minutes: 0, played: 0, days: 4, daysDone: 1, pending: true }
        plugin.apply(f.ctx, { port })
        playTurn(f.emit, { last: [{ type: 'text', text: '东西塞箱子，回屋睡到夏 21 日' }] })
        await wait(1900)
        assert.equal(agent.sent.length, 1)
        const text = agent.sent[0].content[0].text
        assert.ok(text.includes('玩 4 个游戏日，现在才第 2/4 天'), text)
        assert.ok(text.includes('sleep'))
        assert.ok(!text.includes('分钟'))
        assert.ok(f.logs.some((l) => l.includes('玩家要玩 4 个游戏日，还没玩够')))
      } finally {
        f.dispose()
      }
    }
    const cases = [
      { goal: { minutes: 30, played: 31, pending: false }, turn: {}, config: { keepAwake: false } },
      { goal: null, turn: {}, config: { keepAwake: false } },
      { goal: { minutes: 30, played: 2, pending: true }, turn: { reason: 'aborted' } },
      { goal: { minutes: 30, played: 2, pending: true }, turn: { tool: 'bash' } },
      { goal: { minutes: 30, played: 2, pending: true }, turn: {}, config: { autoContinue: false } },
      { goal: { minutes: 30, played: 2, pending: true }, turn: {}, userTyped: true },
    ]
    for (const c of cases) {
      const { agent, service } = fakeAgents()
      if (c.userTyped) agent.inbox.nextTurn.push({ id: 'u' })
      const f = fakeCtx({ agents: service })
      try {
        state.goal = c.goal
        plugin.apply(f.ctx, { port, ...c.config })
        playTurn(f.emit, { last: [{ type: 'text', text: '好了' }], ...c.turn })
        await wait(1900)
        assert.equal(agent.sent.length, 0, JSON.stringify(c))
      } finally {
        f.dispose()
      }
    }
  } finally {
    server.close()
  }
})

test('按时长续玩：游戏动作一直失败时也受次数上限管', async () => {
  const { server, state, port } = await fakeGame()
  const { agent, service } = fakeAgents()
  const f = fakeCtx({ agents: service })
  try {
    state.goal = { minutes: 30, played: 1, pending: true }
    plugin.apply(f.ctx, { port, autoContinueMax: 2 })
    for (let i = 0; i < 3; i++) {
      playTurn(f.emit, { ok: false, last: [{ type: 'text', text: '没浇成' }] })
      await wait(100)
    }
    await wait(1900)
    assert.equal(agent.sent.length, 2)
    assert.ok(f.logs.some((l) => l.includes('不再自动续玩')))
  } finally {
    f.dispose()
    server.close()
  }
})

test('按时长续玩：会话里有 DSH 的 /目标 在跑时交给它续，不重复发；目标停了才发', async () => {
  const { server, state, port } = await fakeGame()
  try {
    for (const [phase, expected] of [['active', 0], ['paused', 1]]) {
      const { agent, service } = fakeAgents()
      const f = fakeCtx({ agents: service, goals: { get: (a) => (a === agent ? { phase } : undefined) } })
      try {
        state.goal = { minutes: 30, played: 2, pending: true }
        plugin.apply(f.ctx, { port })
        playTurn(f.emit, { last: [{ type: 'text', text: '浇完了' }] })
        await wait(1900)
        assert.equal(agent.sent.length, expected, phase)
      } finally {
        f.dispose()
      }
    }
  } finally {
    server.close()
  }
})

const SLEEP_WOKE = [{ type: 'text', text: '成功：睡醒了，春 2 日 06:00，可以开始新的一天' }]
const SLEEP_ENTERING = [{ type: 'text', text: '成功：上床睡觉了，正在进入新的一天（结算界面会自动确认）' }]

test('睡醒自动续玩（关闭保持唤醒）：睡过觉会续，而且到上限就停；支持入口工具 stardew 和“正在进入新的一天”', async () => {
  const { server, port } = await fakeGame()
  const { agent, service } = fakeAgents()
  const f = fakeCtx({ agents: service })
  try {
    plugin.apply(f.ctx, { port, keepAwake: false, autoContinueDays: 2 })
    // 第 1 天：预设工具 sleep 睡醒
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '今天先睡了，晚安。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 1)
    assert.equal(agent.sent[0].content[0].text, plugin.sleepContinueText(1, 2))
    assert.ok(f.logs.some((l) => l.includes('睡醒自动续玩 第 1/2 天')))

    // 第 2 天：入口工具 stardew action="sleep" 返回“正在进入新的一天”
    playTurn(f.emit, { tool: 'stardew', args: '{"action":"sleep"}', resultContent: SLEEP_ENTERING, last: [{ type: 'text', text: '上床存档啦。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 2)
    assert.equal(agent.sent[1].content[0].text, plugin.sleepContinueText(2, 2))
    assert.ok(f.logs.some((l) => l.includes('睡醒自动续玩 第 2/2 天')))

    // 第 3 天：达到上限 2 天，停下等玩家
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '又过了一天。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 2)
    assert.ok(f.logs.some((l) => l.includes('已自动续玩 2 天，停下等玩家')))
  } finally {
    f.dispose()
    server.close()
  }
})

test('睡醒自动续玩（关闭保持唤醒）：玩家发话后计数清零', async () => {
  const { server, port } = await fakeGame()
  const { agent, service } = fakeAgents()
  const f = fakeCtx({ agents: service })
  try {
    plugin.apply(f.ctx, { port, keepAwake: false, autoContinueDays: 2 })
    // 第 1 次续玩（1/2）
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '睡醒了。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 1)
    assert.equal(agent.sent[0].content[0].text, plugin.sleepContinueText(1, 2))

    // 玩家自己发一条消息（DSH 真实格式）：计数清零，下一次睡醒重新从 1/2 开始
    f.emit('session/event', { id: 's1' }, { type: 'user/message', data: { content: [{ type: 'text', text: '继续' }], source: { kind: 'user' } } })
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '又睡醒了。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 2)
    assert.equal(agent.sent[1].content[0].text, plugin.sleepContinueText(1, 2))

    // 再睡一天（2/2）达到上限
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '再睡一天。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 3)
    assert.equal(agent.sent[2].content[0].text, plugin.sleepContinueText(2, 2))

    // 到上限后停下
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '停下。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 3)

    // 停下后玩家再发一条消息（同时验证兼容旧的 data.message.source.kind 格式）：计数清零，重新从 1/2 开始
    f.emit('session/event', { id: 's1' }, { type: 'user/message', data: { message: { role: 'user', content: [{ type: 'text', text: '再来' }], source: { kind: 'user' } } } })
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '重新开始。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 4)
    assert.equal(agent.sent[3].content[0].text, plugin.sleepContinueText(1, 2))
  } finally {
    f.dispose()
    server.close()
  }
})

test('睡醒自动续玩：非玩家 user/message（time-context/runtime-context/repeat-tool-reminder/插件 followup/无 kind）不清零，到上限就停', async () => {
  const { server, port } = await fakeGame()
  const { agent, service } = fakeAgents()
  const f = fakeCtx({ agents: service })
  const session = { id: 's1' }
  const userMsg = (kind) => f.emit('session/event', session, {
    type: 'user/message',
    data: kind === undefined
      ? { content: [{ type: 'text', text: '无来源消息' }] }
      : { content: [{ type: 'text', text: kind }], source: { kind } },
  })
  try {
    plugin.apply(f.ctx, { port, keepAwake: false, autoContinueDays: 2 })
    // 第 1 天：夹着各种 DSH 内部注入消息和缺省 kind
    userMsg('agent-instructions')
    userMsg('runtime-context')
    userMsg('skill-catalog')
    userMsg('time-context')
    userMsg(undefined)
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '第 1 天睡了。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 1)
    assert.equal(agent.sent[0].content[0].text, plugin.sleepContinueText(1, 2))

    // 第 2 天：夹着本插件 followup 和各类系统消息，计数不能清零
    userMsg(plugin.name)
    userMsg('time-context')
    userMsg('runtime-context')
    userMsg('repeat-tool-reminder')
    userMsg(undefined)
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '第 2 天睡了。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 2)
    assert.equal(agent.sent[1].content[0].text, plugin.sleepContinueText(2, 2))

    // 第 3 天：同样夹着非玩家消息，但已到上限 2 天，必须停下
    userMsg(plugin.name)
    userMsg('time-context')
    userMsg('repeat-tool-reminder')
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '第 3 天睡了。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 2)
    assert.ok(f.logs.some((l) => l.includes('已自动续玩 2 天，停下等玩家')))
  } finally {
    f.dispose()
    server.close()
  }
})

test('睡醒自动续玩：先发 user/message(插件名) 再 turn/start，以及 followup 内同步触发 turn/start 都不清零', async () => {
  const { server, port } = await fakeGame()
  try {
    // 场景 1：DSH 先发 user/message(kind=插件名) 再发 turn/start
    {
      const { agent, service } = fakeAgents()
      const f = fakeCtx({ agents: service })
      try {
        plugin.apply(f.ctx, { port, keepAwake: false, autoContinueDays: 2 })
        playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '第 1 天。' }] })
        await wait(1800)
        assert.equal(agent.sent.length, 1)
        assert.equal(agent.sent[0].content[0].text, plugin.sleepContinueText(1, 2))

        f.emit('session/event', { id: 's1' }, { type: 'user/message', data: { content: agent.sent[0].content, source: { kind: plugin.name } } })
        playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '第 2 天。' }] })
        await wait(1800)
        assert.equal(agent.sent.length, 2)
        assert.equal(agent.sent[1].content[0].text, plugin.sleepContinueText(2, 2))

        f.emit('session/event', { id: 's1' }, { type: 'user/message', data: { content: agent.sent[1].content, source: { kind: plugin.name } } })
        playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '第 3 天。' }] })
        await wait(1800)
        assert.equal(agent.sent.length, 2)
      } finally {
        f.dispose()
      }
    }

    // 场景 2：agent.followup() 内部同步触发 turn/start（以及本插件 user/message）
    {
      const { agent, service } = fakeAgents()
      const f = fakeCtx({ agents: service })
      agent.followup = (m) => {
        agent.sent.push(m)
        f.emit('session/event', { id: 's1' }, { type: 'user/message', data: { content: m.content, source: m.source } })
        f.emit('session/event', { id: 's1' }, { type: 'turn/start', data: { turn: agent.sent.length + 1 } })
      }
      try {
        plugin.apply(f.ctx, { port, keepAwake: false, autoContinueDays: 2 })
        playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '第 1 天。' }] })
        await wait(1800)
        assert.equal(agent.sent.length, 1)
        assert.equal(agent.sent[0].content[0].text, plugin.sleepContinueText(1, 2))

        // followup 已经同步触发了 turn/start，接下来完成这一轮的剩余事件
        const finishSleepTurn = () => {
          const session = { id: 's1' }
          f.emit('session/event', session, { type: 'tool/call', data: { callId: 'c1', name: 'mcp__stardew__sleep', arguments: '{}' } })
          f.emit('session/event', session, { type: 'tool/result', data: { message: { role: 'tool', toolCallId: 'c1', isError: false, content: SLEEP_WOKE } } })
          f.emit('session/event', session, { type: 'assistant/message', data: { message: { role: 'assistant', content: [{ type: 'text', text: '睡了。' }] } } })
          f.emit('session/event', session, { type: 'turn/end', data: { reason: { kind: 'completed' } } })
        }
        finishSleepTurn()
        await wait(1800)
        assert.equal(agent.sent.length, 2)
        assert.equal(agent.sent[1].content[0].text, plugin.sleepContinueText(2, 2))

        finishSleepTurn()
        await wait(1800)
        assert.equal(agent.sent.length, 2)
      } finally {
        f.dispose()
      }
    }
  } finally {
    server.close()
  }
})

test('睡醒自动续玩：没睡觉、未进新一天、autoContinueDays=0、总开关关、被打断、接管中、游玩时长到了都不续', async () => {
  const { server, state, port } = await fakeGame()
  try {
    const cases = [
      { name: '没睡觉正常说完', turn: { tool: 'mcp__stardew__water_crops', resultContent: [{ type: 'text', text: '成功：浇完水了' }] } },
      { name: 'sleep 失败', turn: { tool: 'mcp__stardew__sleep', ok: false, resultContent: [{ type: 'text', text: '失败：现在才 10:00，一天还长' }] } },
      { name: '夜里选职业还没进新一天', turn: { tool: 'mcp__stardew__sleep', resultContent: [{ type: 'text', text: '成功：睡下了，夜里技能升级要选职业（选完游戏才会存档、进入新的一天，不用再 sleep）' }] } },
      { name: 'autoContinueDays=0', config: { autoContinueDays: 0 }, turn: { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE } },
      { name: 'autoContinue=false', config: { autoContinue: false }, turn: { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE } },
      { name: '被打断', turn: { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, reason: 'aborted' } },
      { name: '玩家已发话', turn: { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE }, userTyped: true },
      { name: '玩家接管中', game: { playerInControl: true }, turn: { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE } },
      { name: '内置 AI 运行中', game: { agentRunning: true }, turn: { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE } },
      { name: '游玩时长到了(sessionEnded)', game: { sessionEnded: true }, turn: { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE } },
      { name: '游玩时长到了(旧版 session 文字)', game: { session: '本次游玩已经结束（游戏日 1/1 天）：不要再执行游戏动作，向玩家汇报。' }, turn: { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE } },
      { name: '游玩分钟到了(旧版 session 文字)', game: { session: '本次游玩时间到了（已玩 30/30 分钟）：马上 sleep 回家存档，然后汇报。' }, turn: { tool: 'mcp__stardew__sleep', resultContent: SLEEP_ENTERING } },
    ]
    for (const c of cases) {
      state.goal = null
      state.session = c.game?.session ?? '不限时长'
      state.sessionEnded = c.game?.sessionEnded ?? false
      state.playerInControl = c.game?.playerInControl ?? false
      state.agentRunning = c.game?.agentRunning ?? false
      const { agent, service } = fakeAgents()
      if (c.userTyped) agent.inbox.nextTurn.push({ id: 'u' })
      const f = fakeCtx({ agents: service })
      try {
        plugin.apply(f.ctx, { port, keepAwake: false, ...c.config })
        playTurn(f.emit, { last: [{ type: 'text', text: '汇报完毕。' }], ...c.turn })
        await wait(1800)
        assert.equal(agent.sent.length, 0, c.name)
      } finally {
        f.dispose()
      }
    }
  } finally {
    server.close()
  }
})

test('睡醒自动续玩：有 pendingGoal 时优先走原目标逻辑，不消耗睡醒续玩天数', async () => {
  const { server, state, port } = await fakeGame()
  const { agent, service } = fakeAgents()
  const f = fakeCtx({ agents: service })
  try {
    state.goal = { minutes: 0, played: 0, days: 4, daysDone: 1, pending: true }
    plugin.apply(f.ctx, { port, keepAwake: false, autoContinueDays: 1 })
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '睡到第二天了。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 1)
    assert.equal(agent.sent[0].content[0].text, plugin.goalContinueText(state.goal))

    // 目标结束后，睡醒自动续玩额度仍完整（第 1/1 天）
    state.goal = null
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '又睡了一觉。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 2)
    assert.equal(agent.sent[1].content[0].text, plugin.sleepContinueText(1, 1))
  } finally {
    f.dispose()
    server.close()
  }
})

test('保持唤醒（默认开）：普通回合说完会自动续玩，不限于睡觉后，且不受 autoContinueDays 限制；pendingGoal 仍优先', async () => {
  const { server, state, port } = await fakeGame()
  const { agent, service } = fakeAgents()
  const f = fakeCtx({ agents: service })
  try {
    // 默认开启 keepAwake；即使 autoContinueDays=0 也继续唤醒
    plugin.apply(f.ctx, { port, autoContinueDays: 0 })
    // 普通回合（浇水，未睡觉、无目标）正常说完 -> 自动续玩
    playTurn(f.emit, { tool: 'mcp__stardew__water_crops', resultContent: [{ type: 'text', text: '成功：浇完水了' }], last: [{ type: 'text', text: '水浇好了，接下来等你指示。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 1)
    assert.equal(agent.sent[0].content[0].text, plugin.KEEP_AWAKE_TEXT)
    assert.ok(f.logs.some((l) => l.includes('保持唤醒，自动接着玩')))

    // 睡醒回合同样走保持唤醒，不受 autoContinueDays=0 限制
    playTurn(f.emit, { tool: 'mcp__stardew__sleep', resultContent: SLEEP_WOKE, last: [{ type: 'text', text: '晚安，明天见。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 2)
    assert.equal(agent.sent[1].content[0].text, plugin.KEEP_AWAKE_TEXT)

    // 有 pendingGoal 时优先走原目标逻辑
    state.goal = { minutes: 30, played: 5, pending: true }
    playTurn(f.emit, { tool: 'mcp__stardew__water_crops', resultContent: [{ type: 'text', text: '成功' }], last: [{ type: 'text', text: '汇报一下。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 3)
    assert.equal(agent.sent[2].content[0].text, plugin.goalContinueText(state.goal))
  } finally {
    f.dispose()
    server.close()
  }
})

test('保持唤醒：游玩上限到了、玩家接管、内置 AI 运行中、被打断、玩家已发话、总开关关、游戏未连上、关闭开关时不续', async () => {
  const { server, state, port } = await fakeGame()
  try {
    const cases = [
      { name: '游玩上限到了(sessionEnded)', game: { sessionEnded: true } },
      { name: '游玩上限到了(旧版天数文字)', game: { session: '本次游玩已经结束（游戏日 3/3 天）：不要再执行游戏动作，向玩家汇报。' } },
      { name: '游玩上限到了(旧版分钟文字)', game: { session: '本次游玩时间到了（已玩 60/60 分钟）：马上 sleep 回家存档，然后汇报。' } },
      { name: '玩家接管中', game: { playerInControl: true } },
      { name: '内置 AI 运行中', game: { agentRunning: true } },
      { name: '回合被中断', turn: { reason: 'aborted' } },
      { name: '回合被取消', turn: { reason: 'cancelled' } },
      { name: '非星露谷工具', turn: { tool: 'bash' } },
      { name: '玩家已发话', userTyped: true },
      { name: '总开关 autoContinue=false', config: { autoContinue: false } },
      { name: '关闭保持唤醒 keepAwake=false（无目标普通回合）', config: { keepAwake: false } },
      { name: '游戏未连上', config: { port: 1 } },
    ]
    for (const c of cases) {
      state.goal = null
      state.session = c.game?.session ?? '不限时长'
      state.sessionEnded = c.game?.sessionEnded ?? false
      state.playerInControl = c.game?.playerInControl ?? false
      state.agentRunning = c.game?.agentRunning ?? false
      const { agent, service } = fakeAgents()
      if (c.userTyped) agent.inbox.nextTurn.push({ id: 'u' })
      const f = fakeCtx({ agents: service })
      try {
        plugin.apply(f.ctx, { port, ...c.config })
        playTurn(f.emit, {
          tool: 'mcp__stardew__water_crops',
          resultContent: [{ type: 'text', text: '成功：浇完水了' }],
          last: [{ type: 'text', text: '浇完了。' }],
          ...c.turn,
        })
        await wait(1800)
        assert.equal(agent.sent.length, 0, c.name)
      } finally {
        f.dispose()
      }
    }
  } finally {
    server.close()
  }
})

test('保持唤醒：连续没有成功游戏动作达到 autoContinueMax 就停；有一次成功动作后又能续', async () => {
  const { server, port } = await fakeGame()
  const { agent, service } = fakeAgents()
  const f = fakeCtx({ agents: service })
  try {
    plugin.apply(f.ctx, { port, autoContinueMax: 2 })
    for (let i = 0; i < 3; i++) {
      playTurn(f.emit, { ok: false, last: [{ type: 'text', text: '失败了。' }] })
      await wait(100)
    }
    await wait(1800)
    assert.equal(agent.sent.length, 2)
    assert.ok(f.logs.some((l) => l.includes('不再自动续玩')))

    // 一次成功的游戏动作后重新计数，又能续了
    playTurn(f.emit, { ok: true, resultContent: [{ type: 'text', text: '成功' }], last: [{ type: 'text', text: '好了。' }] })
    await wait(1800)
    assert.equal(agent.sent.length, 3)
    assert.equal(agent.sent[2].content[0].text, plugin.KEEP_AWAKE_TEXT)
  } finally {
    f.dispose()
    server.close()
  }
})

// ───────────────────────── 在游戏左上角显示 AI 想法 ─────────────────────────

test('显示 AI 想法：星露谷会话的文字和思考事件会转发，非星露谷会话不转发', async () => {
  const { server, state, port } = await fakeGame()
  const f = fakeCtx()
  try {
    plugin.apply(f.ctx, { port })
    // 非星露谷普通会话：不转发
    f.emit('session/event', { id: 'chat-only' }, {
      type: 'assistant/message',
      data: { message: { role: 'assistant', content: [{ type: 'text', text: '这是一段普通聊天，不该发到游戏里' }] } },
    })
    await wait(120)
    assert.equal(state.thoughts.length, 0)

    // 带星露谷预设的会话：转发 text（同时有 reasoning 和 text 时优先 text）
    const presetSession = { id: 'stardew-preset', header: { agentPreset: plugin.PRESET_ID } }
    f.emit('session/event', presetSession, {
      type: 'assistant/message',
      data: {
        message: {
          role: 'assistant',
          content: [
            { type: 'reasoning', text: '先想想今天干嘛' },
            { type: 'text', text: '早安！先去给防风草浇水。' },
          ],
        },
      },
    })
    await wait(150)
    assert.equal(state.thoughts.length, 1)
    assert.equal(state.thoughts[0].text, '早安！先去给防风草浇水。')

    // 等节流窗口过去后，只带 reasoning 的星露谷会话也会转发 reasoning
    await wait(1050)
    f.emit('session/event', { id: 'gateway-session' }, {
      type: 'tool/call',
      data: { callId: 'c1', name: 'stardew', arguments: '{"action":"get_state"}' },
    })
    f.emit('session/event', { id: 'gateway-session' }, {
      type: 'assistant/message',
      data: {
        message: {
          role: 'assistant',
          content: [{ type: 'reasoning', text: '体力和时间都够，等会儿去海边钓鱼。' }],
        },
      },
    })
    await wait(150)
    assert.equal(state.thoughts.length, 2)
    assert.equal(state.thoughts[1].text, '体力和时间都够，等会儿去海边钓鱼。')
  } finally {
    f.dispose()
    server.close()
  }
})

test('显示 AI 想法：工具调用 JSON 参数和坏掉的工具调用文字不会当成想法转发', async () => {
  const { server, state, port } = await fakeGame()
  const f = fakeCtx()
  try {
    plugin.apply(f.ctx, { port })
    const session = { id: 's-tool', header: { agentPreset: plugin.PRESET_ID } }
    const agent = { id: 's-tool', session }

    // 只有 tool-call 块的 assistant/message：不转发
    f.emit('session/event', session, {
      type: 'assistant/message',
      data: {
        message: {
          role: 'assistant',
          content: [{ type: 'tool-call', id: 'c1', name: 'mcp__stardew__move_to', arguments: '{"x":64,"y":15}' }],
        },
      },
    })
    // 流式 tool-call-delta：不转发 JSON 参数
    f.emit('agent/assistant-stream', { agent, frame: { type: 'start', turn: 1, step: 1 } })
    f.emit('agent/assistant-stream', {
      agent,
      frame: { type: 'chunk', chunk: { type: 'tool-call-delta', index: 0, id: 'c2', name: 'mcp__stardew__buy', argumentsDelta: '{"item":"Parsnip Seeds","count":15}' } },
    })
    f.emit('agent/assistant-stream', { agent, frame: { type: 'end' } })
    // 坏掉的工具调用文字：不转发
    f.emit('session/event', session, {
      type: 'assistant/message',
      data: {
        message: {
          role: 'assistant',
          content: [{ type: 'text', text: '</｜DSML｜ invoke name="water_crops">' }],
        },
      },
    })
    await wait(150)
    assert.equal(state.thoughts.length, 0)

    // 同时包含自然语言 text 和 tool-call：只转发自然语言部分，不含 JSON 参数
    f.emit('session/event', session, {
      type: 'assistant/message',
      data: {
        message: {
          role: 'assistant',
          content: [
            { type: 'text', text: '去皮埃尔商店买种子' },
            { type: 'tool-call', id: 'c3', name: 'mcp__stardew__travel_to', arguments: '{"location":"SeedShop"}' },
          ],
        },
      },
    })
    await wait(150)
    assert.equal(state.thoughts.length, 1)
    assert.equal(state.thoughts[0].text, '去皮埃尔商店买种子')
  } finally {
    f.dispose()
    server.close()
  }
})

test('显示 AI 想法：截短到 200 字单行，流式增量节流到最多每秒一次并合并最新内容', async () => {
  const { server, state, port } = await fakeGame()
  const f = fakeCtx()
  try {
    plugin.apply(f.ctx, { port })
    const session = { id: 's-stream', header: { agentPreset: plugin.PRESET_ID } }
    const agent = { id: 's-stream', session }

    // 1. 截短 + 合成一行 + 过滤控制字符
    const longRaw = '第一行\r\n第二行\t带控制\u0000\u0007字符，' + '种田'.repeat(150)
    const formatted = plugin.formatThoughtText(longRaw)
    assert.ok(formatted.length <= plugin.THOUGHT_MAX_CHARS)
    assert.ok(formatted.endsWith('…'))
    assert.ok(!/[\r\n\t\u0000-\u001f]/.test(formatted))
    assert.ok(formatted.startsWith('第一行 第二行 带控制字符，'))

    // 2. 流式增量：短时间内发 20 个 chunk，立刻发第 1 次，1 秒内不再发，1 秒后发合并后的完整内容
    f.emit('agent/assistant-stream', { agent, frame: { type: 'start', turn: 1, step: 1 } })
    const parts = ['今天', '天气', '真好', '，先去', '给作物', '浇水', '再去', '海边', '钓鱼', '吧！']
    for (const part of parts) {
      f.emit('agent/assistant-stream', {
        agent,
        frame: { type: 'chunk', chunk: { type: 'reasoning-delta', index: 0, text: part } },
      })
    }
    f.emit('agent/assistant-stream', { agent, frame: { type: 'end' } })

    await wait(200)
    assert.equal(state.thoughts.length, 1, '1 秒节流窗口内只应发出首次请求')
    assert.equal(state.thoughts[0].text, '今天')

    await wait(1000)
    assert.equal(state.thoughts.length, 2, '节流窗口结束后应发出合并后的完整想法')
    assert.equal(state.thoughts[1].text, parts.join(''))
  } finally {
    f.dispose()
    server.close()
  }
})

test('显示 AI 想法：模组没开时不报错、不影响插件运行', async () => {
  const f = fakeCtx()
  try {
    plugin.apply(f.ctx, { port: 1 }) // 没人监听的端口
    const session = { id: 's-offline', header: { agentPreset: plugin.PRESET_ID } }
    f.emit('session/event', session, {
      type: 'assistant/message',
      data: { message: { role: 'assistant', content: [{ type: 'text', text: '游戏没开时发一条想法' }] } },
    })
    await wait(250)
    assert.ok(!f.logs.some((l) => l.startsWith('error')), '不能产生 error 日志')
  } finally {
    f.dispose()
  }
})

test('显示 AI 想法：关掉开关不转发（支持活引用动态切换）', async () => {
  const { server, state, port } = await fakeGame()
  const f = fakeCtx()
  let enabled = false
  try {
    plugin.apply(f.ctx, { port, showThoughtInGame: { get: () => enabled } })
    const session = { id: 's-toggle', header: { agentPreset: plugin.PRESET_ID } }
    f.emit('session/event', session, {
      type: 'assistant/message',
      data: { message: { role: 'assistant', content: [{ type: 'text', text: '开关关着，不该转发' }] } },
    })
    await wait(150)
    assert.equal(state.thoughts.length, 0)

    // 动态打开开关后立即生效
    enabled = true
    f.emit('session/event', session, {
      type: 'assistant/message',
      data: { message: { role: 'assistant', content: [{ type: 'text', text: '开关打开了，应该转发' }] } },
    })
    await wait(150)
    assert.equal(state.thoughts.length, 1)
    assert.equal(state.thoughts[0].text, '开关打开了，应该转发')
  } finally {
    f.dispose()
    server.close()
  }
})
