// 设置备份：DSH 更新插件后设置回到默认值时，从插件目录外的备份恢复。
// 运行：node --test tests/DshPlugin/backup.test.mjs
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'

const dir = mkdtempSync(join(tmpdir(), 'bwf-backup-'))
process.env.DSH_STARDEW_BACKUP = join(dir, 'settings-backup.json')
const plugin = await import('../../lib/index.js')
const version = JSON.parse(readFileSync(new URL('../../package.json', import.meta.url), 'utf8')).version

const silent = () => {}
const writeBackup = (v, settings) => writeFileSync(process.env.DSH_STARDEW_BACKUP, JSON.stringify({ version: v, settings }))
const readBackup = () => JSON.parse(readFileSync(process.env.DSH_STARDEW_BACKUP, 'utf8'))
const tick = () => new Promise((resolve) => setTimeout(resolve, 5))
/** 假的插件上下文：settings 服务记下写回的设置；noService 时没有设置服务，fail 时写回出错。 */
function fakeCtx({ noService = false, fail = null } = {}) {
  const updates = []
  const ctx = {
    fiber: { entry: { options: { id: 'bwf' } } },
    effect() {},
    get: (name) => (name === 'settings' && !noService
      ? { async update(id, patch) { if (fail) throw new Error(fail); updates.push([id, patch]) } }
      : undefined),
  }
  return { ctx, updates }
}
const fast = { every: 1, tries: 3 }
/** 清掉上一个测试留下的“恢复过”状态（DSH 交来改过的设置时会清）。 */
const reset = () => plugin.restoreFromBackup({}, { port: 18099 }, silent)

test('只记玩家改过的设置', () => {
  assert.deepEqual(plugin.customizedSettings({ forageRadius: 1, port: 18081, extraInstructions: '' }), { port: 18081 })
})

test('更新后设置全是默认值：从旧版本的备份恢复，并用设置服务写回 DSH', async () => {
  reset()
  writeBackup('0.0.1', { forageRadius: 5, extraInstructions: '专心钓鱼', bogus: 1 })
  const { ctx, updates } = fakeCtx()
  const restored = plugin.restoreFromBackup(ctx, {}, silent, undefined, fast)
  assert.deepEqual(restored, { forageRadius: 5, extraInstructions: '专心钓鱼' })
  await tick()
  assert.deepEqual(updates, [['bwf', { forageRadius: 5, extraInstructions: '专心钓鱼' }]])
  // 写回之前（或者写不回去时）推给游戏的也是恢复的值
  assert.equal(plugin.currentConfig({}).forageRadius, 5)
  // 备份跟着更新成新版本
  plugin.saveBackup({}, silent)
  assert.equal(readBackup().version, version)
  assert.deepEqual(readBackup().settings, { forageRadius: 5, extraInstructions: '专心钓鱼' })
})

test('写不回 DSH 时也按备份继续用', async () => {
  for (const opts of [{ noService: true }, { fail: '磁盘满了' }]) {
    reset()
    writeBackup('0.0.1', { idleSeconds: 300 })
    const logs = []
    const restored = plugin.restoreFromBackup(fakeCtx(opts).ctx, {}, (level, msg) => logs.push(level + ' ' + msg), undefined, fast)
    assert.deepEqual(restored, { idleSeconds: 300 })
    assert.equal(plugin.currentConfig({}).idleSeconds, 300)
    await tick()
    assert.match(logs.join('\n'), /重新填一次/)
  }
})

test('恢复后插件被重启、DSH 交来的还是默认值：接着用恢复的值，备份不会被盖成空的', async () => {
  reset()
  writeBackup('0.0.1', { forageRadius: 5 })
  plugin.restoreFromBackup(fakeCtx({ noService: true }).ctx, {}, silent, undefined, fast)
  plugin.saveBackup({}, silent)
  // 备份已经换成新版本号了；插件重启后 DSH 交来的仍是默认值
  assert.equal(readBackup().version, version)
  const second = fakeCtx()
  assert.deepEqual(plugin.restoreFromBackup(second.ctx, {}, silent, undefined, fast), { forageRadius: 5 })
  await tick()
  assert.deepEqual(second.updates, [['bwf', { forageRadius: 5 }]])
  plugin.saveBackup({}, silent)
  assert.deepEqual(readBackup().settings, { forageRadius: 5 })
  // DSH 里有了这些值以后，玩家再把设置改回默认，备份跟着变空
  plugin.saveBackup({ forageRadius: 5 }, silent)
  plugin.saveBackup({}, silent)
  assert.deepEqual(readBackup().settings, {})
})

test('同一版本、或者现在已经有改过的设置：不恢复', () => {
  reset()
  writeBackup(version, { forageRadius: 5 })
  assert.equal(plugin.restoreFromBackup({}, {}, silent), null)
  assert.equal(plugin.currentConfig({}).forageRadius, 1)
  writeBackup('0.0.1', { forageRadius: 5 })
  assert.equal(plugin.restoreFromBackup({}, { port: 18081 }, silent), null)
  assert.equal(plugin.currentConfig({ port: 18081 }).forageRadius, 1)
})

test('没有备份、模组 config.json 里也都是默认值：什么都不做', () => {
  reset()
  rmSync(process.env.DSH_STARDEW_BACKUP, { force: true })
  assert.equal(plugin.restoreFromBackup({}, {}, silent, () => ({ forageRadius: 1, port: 18080 })), null)
})

test('没有备份（从老版本更新上来）：从游戏模组的 config.json 读回上次推过去的设置', async () => {
  reset()
  rmSync(process.env.DSH_STARDEW_BACKUP, { force: true })
  const { ctx, updates } = fakeCtx()
  const restored = plugin.restoreFromBackup(ctx, {}, silent,
    () => ({ forageRadius: 4, port: 18081, extraInstructions: '', companionMode: undefined, guideMining: '我的采矿攻略' }), fast)
  assert.deepEqual(restored, { forageRadius: 4, port: 18081, guideMining: '我的采矿攻略' })
  await tick()
  assert.deepEqual(updates, [['bwf', restored]])
  assert.equal(plugin.currentConfig({}).port, 18081)
})

test('模组 config.json 的字段对应', () => {
  const dir2 = mkdtempSync(join(tmpdir(), 'bwf-game-'))
  writeFileSync(join(dir2, 'StardewModdingAPI.exe'), '')
  mkdirSync(join(dir2, 'Mods', 'BlindWhaleFarmer'), { recursive: true })
  writeFileSync(join(dir2, 'Mods', 'BlindWhaleFarmer', 'config.json'), '﻿' + JSON.stringify({
    SessionDays: 2, McpFreezeTime: false, McpIdleSeconds: 300, ForageRadius: 3, CompanionMode: 'solo',
    CoSleepDefault: 'random', PassbyEmoteEnabled: false, PassbyEmoteChance: 0.5, PassbyEmoteCooldownMinutes: 10,
    IdleFollowEnabled: false, IdleFollowIntervalSeconds: 240, IdleFollowDurationSeconds: 45,
    ExtraInstructions: '钓鱼', GuideFile: 'guide/攻略.md', McpPort: 18085, GuideOverrides: { 钓鱼: '我的钓鱼攻略' }, ApiKey: 'secret',
  }))
  const s = plugin.modConfigSettings(dir2)
  assert.equal(s.sessionDays, 2)
  assert.equal(s.freezeTime, false)
  assert.equal(s.idleSeconds, 300)
  assert.equal(s.forageRadius, 3)
  assert.equal(s.companionMode, 'solo')
  assert.equal(s.coSleepDefault, 'random')
  assert.equal(s.passbyEmoteEnabled, false)
  assert.equal(s.passbyEmoteChance, 0.5)
  assert.equal(s.passbyEmoteCooldownMinutes, 10)
  assert.equal(s.idleFollowEnabled, false)
  assert.equal(s.idleFollowIntervalSeconds, 240)
  assert.equal(s.idleFollowDurationSeconds, 45)
  assert.equal(s.port, 18085)
  assert.equal(s.guideFishing, '我的钓鱼攻略')
  assert.equal(Object.values(s).includes('secret'), false)
  rmSync(dir2, { recursive: true, force: true })
})

test.after(() => rmSync(dir, { recursive: true, force: true }))
