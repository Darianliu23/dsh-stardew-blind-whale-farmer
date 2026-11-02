// 用一个假的“游戏 /settings 服务”测同步逻辑：只在版本号不一样时推、游戏重开后重推、改设置推新版本。
// 运行：node --test tests/DshPlugin/sync.test.mjs（设置 DSH_HOME 指向 DSH 的家目录时，还会测设置页表单）
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { createServer } from 'node:http'

const plugin = await import('../../lib/index.js')

function fakeGame() {
  const state = { revision: '', posts: [] }
  const server = createServer((req, res) => {
    let body = ''
    req.on('data', (c) => (body += c))
    req.on('end', () => {
      if (req.method === 'POST') {
        const msg = JSON.parse(body)
        state.revision = msg.revision
        state.posts.push(msg)
        res.end(JSON.stringify({ message: '已应用', revision: state.revision }))
      } else {
        res.end(JSON.stringify({ instance: 'x', revision: state.revision }))
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
    sessionDays: 2, sessionMinutes: 30, freezeTime: true, idleSeconds: 120, extraInstructions: '专心钓鱼', guideFile: 'guide/攻略.md',
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
function playTurn(emit, { tool = 'mcp__stardew__water_crops', ok = true, last, reason = 'completed' }) {
  const session = { id: 's1' }
  const ev = (type, data) => emit('session/event', session, { type, data })
  ev('turn/start', { turn: 1 })
  ev('tool/call', { callId: 'c1', name: tool, arguments: '{}' })
  ev('tool/result', { message: { role: 'tool', toolCallId: 'c1', isError: !ok, content: [] } })
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

test('设置页表单（需要能找到 schemastery）', { skip: !plugin.Config && '没找到 schemastery（设置 DSH_HOME 再测）' }, () => {
  const resolved = plugin.Config({})
  assert.equal(resolved.sessionDays, 0)
  assert.equal(resolved.sessionMinutes, 0)
  assert.equal(resolved.freezeTime, true)
  assert.equal(resolved.port, 18080)
  assert.equal(resolved.guideFile, 'guide/攻略.md')
  assert.equal(resolved.presetEnabled, false, '会话预设默认关闭')
  assert.equal(resolved.gatewayEnabled, false, '入口工具默认关闭')
  assert.equal(resolved.autoContinue, true, '自动续玩默认开启')
  assert.throws(() => plugin.Config({ sessionDays: -1 }))
})
