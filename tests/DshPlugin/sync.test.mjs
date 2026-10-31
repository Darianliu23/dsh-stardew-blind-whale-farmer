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
  return {
    logs,
    ctx: {
      logger: (level, msg) => logs.push(`${level} ${msg}`),
      effect: (fn) => { cleanups.push(fn()) },
      get: (name) => services[name],
    },
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
    sessionDays: 2, sessionMinutes: 30, freezeTime: true, idleSeconds: 120, extraInstructions: '专心钓鱼', guideFile: 'guide/zh-CN.md',
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

test('设置页表单（需要能找到 schemastery）', { skip: !plugin.Config && '没找到 schemastery（设置 DSH_HOME 再测）' }, () => {
  const resolved = plugin.Config({})
  assert.equal(resolved.sessionDays, 0)
  assert.equal(resolved.sessionMinutes, 0)
  assert.equal(resolved.freezeTime, true)
  assert.equal(resolved.port, 18080)
  assert.equal(resolved.guideFile, 'guide/zh-CN.md')
  assert.equal(resolved.presetEnabled, false, '会话预设默认关闭')
  assert.throws(() => plugin.Config({ sessionDays: -1 }))
})
