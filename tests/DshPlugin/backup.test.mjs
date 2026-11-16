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

test('只记玩家改过的设置', () => {
  assert.deepEqual(plugin.customizedSettings({ forageRadius: 1, port: 18081, extraInstructions: '' }), { port: 18081 })
})

test('更新后设置全是默认值：从旧版本的备份恢复，并写回 DSH', () => {
  writeBackup('0.0.1', { forageRadius: 5, extraInstructions: '专心钓鱼', bogus: 1 })
  const updates = []
  const ctx = { fiber: { update(config) { updates.push(config) } } }
  const restored = plugin.restoreFromBackup(ctx, {}, silent)
  assert.deepEqual(restored, { forageRadius: 5, extraInstructions: '专心钓鱼' })
  assert.deepEqual(updates, [{ forageRadius: 5, extraInstructions: '专心钓鱼' }])
  // 写回之前（或者写不回去时）推给游戏的也是恢复的值
  assert.equal(plugin.currentConfig({}).forageRadius, 5)
  // 备份跟着更新成新版本
  plugin.saveBackup({}, silent)
  assert.equal(readBackup().version, version)
  assert.deepEqual(readBackup().settings, { forageRadius: 5, extraInstructions: '专心钓鱼' })
})

test('写不回 DSH 时也按备份继续用', () => {
  writeBackup('0.0.1', { idleSeconds: 300 })
  const logs = []
  const restored = plugin.restoreFromBackup({}, {}, (level, msg) => logs.push(level + ' ' + msg))
  assert.deepEqual(restored, { idleSeconds: 300 })
  assert.equal(plugin.currentConfig({}).idleSeconds, 300)
  assert.match(logs.join('\n'), /重新填一次/)
})

test('同一版本、或者现在已经有改过的设置：不恢复', () => {
  writeBackup(version, { forageRadius: 5 })
  assert.equal(plugin.restoreFromBackup({}, {}, silent), null)
  assert.equal(plugin.currentConfig({}).forageRadius, 1)
  writeBackup('0.0.1', { forageRadius: 5 })
  assert.equal(plugin.restoreFromBackup({}, { port: 18081 }, silent), null)
  assert.equal(plugin.currentConfig({ port: 18081 }).forageRadius, 1)
})

test('没有备份、模组 config.json 里也都是默认值：什么都不做', () => {
  rmSync(process.env.DSH_STARDEW_BACKUP, { force: true })
  assert.equal(plugin.restoreFromBackup({}, {}, silent, () => ({ forageRadius: 1, port: 18080 })), null)
})

test('没有备份（从老版本更新上来）：从游戏模组的 config.json 读回上次推过去的设置', () => {
  rmSync(process.env.DSH_STARDEW_BACKUP, { force: true })
  const updates = []
  const restored = plugin.restoreFromBackup({ fiber: { update: (c) => updates.push(c) } }, {}, silent,
    () => ({ forageRadius: 4, port: 18081, extraInstructions: '', companionMode: undefined, guideMining: '我的采矿攻略' }))
  assert.deepEqual(restored, { forageRadius: 4, port: 18081, guideMining: '我的采矿攻略' })
  assert.deepEqual(updates, [restored])
  assert.equal(plugin.currentConfig({}).port, 18081)
})

test('模组 config.json 的字段对应', () => {
  const dir2 = mkdtempSync(join(tmpdir(), 'bwf-game-'))
  writeFileSync(join(dir2, 'StardewModdingAPI.exe'), '')
  mkdirSync(join(dir2, 'Mods', 'BlindWhaleFarmer'), { recursive: true })
  writeFileSync(join(dir2, 'Mods', 'BlindWhaleFarmer', 'config.json'), '﻿' + JSON.stringify({
    SessionDays: 2, McpFreezeTime: false, McpIdleSeconds: 300, ForageRadius: 3, CompanionMode: 'solo',
    ExtraInstructions: '钓鱼', GuideFile: 'guide/攻略.md', McpPort: 18085, GuideOverrides: { 钓鱼: '我的钓鱼攻略' }, ApiKey: 'secret',
  }))
  const s = plugin.modConfigSettings(dir2)
  assert.equal(s.sessionDays, 2)
  assert.equal(s.freezeTime, false)
  assert.equal(s.idleSeconds, 300)
  assert.equal(s.forageRadius, 3)
  assert.equal(s.companionMode, 'solo')
  assert.equal(s.port, 18085)
  assert.equal(s.guideFishing, '我的钓鱼攻略')
  assert.equal(Object.values(s).includes('secret'), false)
  rmSync(dir2, { recursive: true, force: true })
})

test.after(() => rmSync(dir, { recursive: true, force: true }))
