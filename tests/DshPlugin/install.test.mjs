// 自动装模组：用临时文件夹模拟插件包里的模组和游戏目录
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { existsSync, mkdirSync, mkdtempSync, readFileSync, writeFileSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { compareVersions, installMod, manifestVersion } from '../../lib/mod-install.js'

function makeBundle(version) {
  const dir = mkdtempSync(join(tmpdir(), 'bwf-bundle-'))
  mkdirSync(join(dir, 'guide'))
  writeFileSync(join(dir, 'BlindWhaleFarmer.dll'), 'dll ' + version)
  writeFileSync(join(dir, 'manifest.json'), '﻿' + JSON.stringify({ Version: version }))
  writeFileSync(join(dir, 'config.json'), '{"McpPort": 18080}')
  writeFileSync(join(dir, 'guide', '攻略.md'), 'guide ' + version)
  return dir
}

function makeGame({ smapi = true } = {}) {
  const dir = mkdtempSync(join(tmpdir(), 'bwf-game-'))
  if (smapi) writeFileSync(join(dir, 'StardewModdingAPI.exe'), '')
  return dir
}

test('版本号比较', () => {
  assert.ok(compareVersions('1.1.4', '1.1.3') > 0)
  assert.ok(compareVersions('1.2.0', '1.10.0') < 0)
  assert.equal(compareVersions('1.1.4', '1.1.4'), 0)
})

test('游戏里没装模组时装上，带上攻略和默认 config.json', () => {
  const bundleDir = makeBundle('1.1.4')
  const gameDir = makeGame()
  const result = installMod({ gameDir, bundleDir })
  assert.equal(result.state, 'installed')
  const target = join(gameDir, 'Mods', 'BlindWhaleFarmer')
  assert.equal(manifestVersion(target), '1.1.4')
  assert.equal(readFileSync(join(target, 'BlindWhaleFarmer.dll'), 'utf8'), 'dll 1.1.4')
  assert.equal(readFileSync(join(target, 'guide', '攻略.md'), 'utf8'), 'guide 1.1.4')
  assert.ok(existsSync(join(target, 'config.json')))
  assert.equal(installMod({ gameDir, bundleDir }).state, 'current')
})

test('更新旧版时不覆盖玩家的 config.json，装过的 Mods-AI 也一起更新', () => {
  const gameDir = makeGame()
  installMod({ gameDir, bundleDir: makeBundle('1.1.3') })
  const target = join(gameDir, 'Mods', 'BlindWhaleFarmer')
  writeFileSync(join(target, 'config.json'), '{"ApiKey": "mine"}')
  const ai = join(gameDir, 'Mods-AI', 'BlindWhaleFarmer')
  mkdirSync(ai, { recursive: true })
  writeFileSync(join(ai, 'manifest.json'), JSON.stringify({ Version: '1.1.3' }))

  const result = installMod({ gameDir, bundleDir: makeBundle('1.1.4') })
  assert.equal(result.state, 'installed')
  assert.equal(manifestVersion(target), '1.1.4')
  assert.equal(manifestVersion(ai), '1.1.4')
  assert.equal(readFileSync(join(target, 'config.json'), 'utf8'), '{"ApiKey": "mine"}')
})

test('游戏里的模组比插件带的新时不降级', () => {
  const gameDir = makeGame()
  installMod({ gameDir, bundleDir: makeBundle('1.2.0') })
  assert.equal(installMod({ gameDir, bundleDir: makeBundle('1.1.4') }).state, 'current')
  assert.equal(manifestVersion(join(gameDir, 'Mods', 'BlindWhaleFarmer')), '1.2.0')
})

test('没装 SMAPI、包里没有模组时不动游戏目录', () => {
  const gameDir = makeGame({ smapi: false })
  assert.equal(installMod({ gameDir, bundleDir: makeBundle('1.1.4') }).state, 'no-smapi')
  assert.ok(!existsSync(join(gameDir, 'Mods')))
  const empty = mkdtempSync(join(tmpdir(), 'bwf-empty-'))
  assert.equal(installMod({ gameDir: makeGame(), bundleDir: empty }).state, 'no-bundle')
})
