// 开游戏、读存档的流程（不真的开游戏：状态、启动、读档都换成假的）。
// 运行：node --test tests/DshPlugin/launch.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { launchGame } from '../../lib/game-control.js'

/** 假游戏：按顺序返回状态；记下启动和读档。 */
function fakeGame({ states, running = false, startError = null, mismatch = '', loadReplies = ['成功：正在读取存档 测试_1（上次游玩 11-07 00:05），要十几秒'] }) {
  const log = { starts: 0, loads: [] }
  let i = 0
  const replies = [...loadReplies]
  return {
    log,
    deps: {
      status: async () => states[Math.min(i++, states.length - 1)],
      start: () => { log.starts++; return startError },
      running: () => running,
      loadSave: async (save) => { log.loads.push(save); return replies.length > 1 ? replies.shift() : replies[0] },
      sleep: async () => {},
      mismatch: () => mismatch,
    },
  }
}

test('游戏已经在存档里：什么都不做', async () => {
  const g = fakeGame({ states: ['world'] })
  const text = await launchGame({ deps: g.deps })
  assert.match(text, /^成功：游戏已经开着/)
  assert.equal(g.log.starts, 0)
  assert.deepEqual(g.log.loads, [])
})

test('游戏没开：启动 SMAPI，等到标题画面，读指定的存档，等进游戏', async () => {
  const g = fakeGame({ states: ['off', 'off', 'title', 'title', 'world'] })
  const text = await launchGame({ save: '测试', deps: g.deps })
  assert.equal(g.log.starts, 1)
  assert.deepEqual(g.log.loads, ['测试'])
  assert.match(text, /^成功：启动了 SMAPI，读取了存档 测试_1，已经进入游戏/)
})

test('SMAPI 已经在启动：不再开第二个', async () => {
  const g = fakeGame({ states: ['off', 'title', 'world'], running: true })
  const text = await launchGame({ deps: g.deps })
  assert.equal(g.log.starts, 0)
  assert.match(text, /没有再开一个/)
})

test('标题画面还没出来：等一会儿再读档', async () => {
  const g = fakeGame({ states: ['title', 'world'], loadReplies: ['失败：现在不在标题画面（游戏正在启动或正在读档）', '成功：正在读取存档 农场A（上次游玩 11-07 00:05）'] })
  const text = await launchGame({ deps: g.deps })
  assert.equal(g.log.loads.length, 2)
  assert.match(text, /读取了存档 农场A/)
})

test('存档名不对：把模组的说明原样告诉 AI', async () => {
  const g = fakeGame({ states: ['title'], loadReplies: ['失败：没有名字里带“不存在”的存档。现有存档：测试_1（11-07 00:05）'] })
  const text = await launchGame({ save: '不存在', deps: g.deps })
  assert.match(text, /^失败：没有名字里带“不存在”的存档/)
})

test('找不到游戏或启动失败：直接说原因', async () => {
  const g = fakeGame({ states: ['off'], startError: '找不到星露谷的安装目录' })
  assert.equal(await launchGame({ deps: g.deps }), '失败：找不到星露谷的安装目录')
})

test('启动后一直连不上模组：超时说明', async () => {
  const g = fakeGame({ states: ['off'] })
  const text = await launchGame({ startTimeoutMs: 0, deps: g.deps })
  assert.match(text, /^失败：启动了 SMAPI，但 0 分钟还没连上模组/)
})

test('预设里的开游戏工具：参数传过去，成功时提醒等游戏工具出现', async () => {
  const { launchTool, LAUNCH_TOOL } = await import('../../lib/launcher.js')
  const calls = []
  const tool = launchTool({ port: 18081, gamePath: 'D:/Games/SDV' }, async (opts) => { calls.push(opts); return '成功：读取了存档 测试_1，已经进入游戏' })
  assert.equal(tool.name, LAUNCH_TOOL)
  const text = await tool.execute({ save: '测试' })
  assert.equal(calls[0].port, 18081)
  assert.equal(calls[0].gamePath, 'D:/Games/SDV')
  assert.equal(calls[0].save, '测试')
  assert.match(text, /等半分钟再试/)
})

test('连不上模组时端口和模组 config.json 不一样：直接说端口的事', async () => {
  const g = fakeGame({ states: ['off'], mismatch: '插件设置里的端口是 18081，但游戏模组 config.json 里的 McpPort 是 18080，两边连不上。' })
  const text = await launchGame({ startTimeoutMs: 0, deps: g.deps })
  assert.match(text, /^失败：启动了 SMAPI，但连不上模组：插件设置里的端口是 18081.*McpPort 是 18080/)
})

test('portMismatchNote：一样或读不到不说，不一样说清两边的值', async () => {
  const { portMismatchNote } = await import('../../lib/game-control.js')
  assert.equal(portMismatchNote(18080, '', () => 18080), '')
  assert.equal(portMismatchNote(18080, '', () => null), '')
  assert.match(portMismatchNote(18081, '', () => 18080), /端口是 18081.*McpPort 是 18080.*改成 18080/)
})
