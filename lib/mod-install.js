// 把插件包里带的星露谷模组（release/BlindWhaleFarmer）装进游戏的 Mods 文件夹。
//
// 为什么插件要管模组：从 DSH 插件市场装的人只拿到这个仓库，以前还得另外去 GitHub Releases 下载模组 zip，
// 市场里点“更新”也只更新插件。现在编译好的模组跟着仓库走，插件每分钟看一眼：游戏里没装、或者比包里的旧，
// 就复制过去。和 install.ps1 一样：config.json 已经有了就不碰（里面有玩家自己的设置）。
// 游戏开着时 DLL 被占用，复制会失败，等游戏关了下一轮再装；联机用的 Mods-AI 里装过的也一起更新。

import { execFileSync } from 'node:child_process'
import { copyFileSync, existsSync, mkdirSync, readFileSync, readdirSync } from 'node:fs'
import { homedir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'

export const BUNDLED_MOD_DIR = join(dirname(fileURLToPath(import.meta.url)), '..', 'release', 'BlindWhaleFarmer')
const MOD_FOLDER = 'BlindWhaleFarmer'

/** manifest.json 里的 Version；读不到返回 null。 */
export function manifestVersion(dir) {
  try {
    const text = readFileSync(join(dir, 'manifest.json'), 'utf8').replace(/^\uFEFF/, '')
    const version = JSON.parse(text).Version
    return typeof version === 'string' ? version.trim() : null
  } catch {
    return null
  }
}

/** 比较 1.2.3 这样的版本号：a 更新返回正数，一样返回 0。 */
export function compareVersions(a, b) {
  const pa = String(a).split(/[.-]/).map((x) => parseInt(x, 10) || 0)
  const pb = String(b).split(/[.-]/).map((x) => parseInt(x, 10) || 0)
  for (let i = 0; i < Math.max(pa.length, pb.length); i++) {
    const d = (pa[i] ?? 0) - (pb[i] ?? 0)
    if (d !== 0) return d
  }
  return 0
}

/** 有没有装 SMAPI（Windows 是 StardewModdingAPI.exe，Mac/Linux 是 StardewModdingAPI）。 */
export function hasSmapi(gameDir) {
  return existsSync(join(gameDir, 'StardewModdingAPI.exe')) || existsSync(join(gameDir, 'StardewModdingAPI'))
}

/** Steam 的安装目录：Windows 读注册表，其他系统用默认位置。 */
function steamRoots() {
  const roots = []
  if (process.platform === 'win32') {
    try {
      const out = execFileSync('reg', ['query', 'HKCU\\Software\\Valve\\Steam', '/v', 'SteamPath'], { encoding: 'utf8', timeout: 5000, windowsHide: true })
      const m = /SteamPath\s+REG_\w+\s+(.+)/.exec(out)
      if (m) roots.push(m[1].trim().replace(/\//g, '\\'))
    } catch {}
    roots.push('C:\\Program Files (x86)\\Steam')
  } else if (process.platform === 'darwin') {
    roots.push(join(homedir(), 'Library', 'Application Support', 'Steam'))
  } else {
    roots.push(join(homedir(), '.steam', 'steam'), join(homedir(), '.local', 'share', 'Steam'))
  }
  return roots
}

/** 所有可能的游戏目录：每个 Steam 库（libraryfolders.vdf 里登记的，比如装在别的盘），再加 GOG 的默认位置。 */
export function gameDirCandidates() {
  const libraries = []
  for (const root of steamRoots()) {
    libraries.push(root)
    try {
      const vdf = readFileSync(join(root, 'steamapps', 'libraryfolders.vdf'), 'utf8')
      for (const m of vdf.matchAll(/"path"\s+"([^"]+)"/g)) libraries.push(m[1].replace(/\\\\/g, '\\'))
    } catch {}
  }
  const dirs = libraries.map((lib) => process.platform === 'darwin'
    ? join(lib, 'steamapps', 'common', 'Stardew Valley', 'Contents', 'MacOS')
    : join(lib, 'steamapps', 'common', 'Stardew Valley'))
  if (process.platform === 'win32') dirs.push('C:\\Program Files (x86)\\GOG Galaxy\\Games\\Stardew Valley')
  return [...new Set(dirs)]
}

/** 找星露谷的目录：先用设置里填的，再自动找；优先装了 SMAPI 的那个。 */
export function findGameDir(override) {
  if (override && override.trim()) return override.trim()
  const found = gameDirCandidates().filter((dir) => existsSync(join(dir, 'Stardew Valley.dll')) || existsSync(join(dir, 'StardewValley.dll')) || hasSmapi(dir))
  return found.find(hasSmapi) ?? found[0] ?? null
}

/**
 * 装一次：返回 { state, message }。state：
 *   installed（这次装了或更新了）、current（已经是最新）、busy（游戏开着，文件被占用）、
 *   no-game、no-smapi、no-bundle（包里没带编译好的模组，比如从源码装又没编译）、error。
 */
export function installMod({ gameDir: override, bundleDir = BUNDLED_MOD_DIR } = {}) {
  const bundled = manifestVersion(bundleDir)
  if (!bundled || !existsSync(join(bundleDir, `${MOD_FOLDER}.dll`)))
    return { state: 'no-bundle', message: '插件包里没有编译好的模组' }
  const gameDir = findGameDir(override)
  if (!gameDir || !existsSync(gameDir))
    return { state: 'no-game', message: '找不到星露谷的安装目录：在配置卡片的“星露谷目录”里填上游戏文件夹' }
  if (!hasSmapi(gameDir))
    return { state: 'no-smapi', message: `${gameDir} 里没有 SMAPI：先装 SMAPI（https://smapi.io），插件会自动把模组装进去` }

  // 平常的 Mods 一定装；联机时 AI 窗口用的 Mods-AI 只在装过的时候更新（coop.ps1 建的）
  const targets = [join(gameDir, 'Mods', MOD_FOLDER)]
  const aiTarget = join(gameDir, 'Mods-AI', MOD_FOLDER)
  if (existsSync(aiTarget)) targets.push(aiTarget)

  const updated = []
  for (const target of targets) {
    const installed = manifestVersion(target)
    if (installed && compareVersions(installed, bundled) >= 0) continue
    try {
      mkdirSync(join(target, 'guide'), { recursive: true })
      // DLL 先复制：游戏开着时它被占用，在这一步就失败，manifest 还是旧版本，下一轮会再试
      copyFileSync(join(bundleDir, `${MOD_FOLDER}.dll`), join(target, `${MOD_FOLDER}.dll`))
      for (const file of readdirSync(join(bundleDir, 'guide')))
        copyFileSync(join(bundleDir, 'guide', file), join(target, 'guide', file))
      if (!existsSync(join(target, 'config.json')) && existsSync(join(bundleDir, 'config.json')))
        copyFileSync(join(bundleDir, 'config.json'), join(target, 'config.json'))
      copyFileSync(join(bundleDir, 'manifest.json'), join(target, 'manifest.json'))
      updated.push(`${target}（${installed ? installed + ' → ' : ''}${bundled}）`)
    } catch (err) {
      if (['EBUSY', 'EPERM', 'EACCES'].includes(err?.code))
        return { state: 'busy', message: `模组 ${bundled} 还没装上：游戏开着（关掉后会自动装），或者没有写入游戏目录的权限` }
      return { state: 'error', message: `装模组失败：${err?.message ?? err}` }
    }
  }
  return updated.length > 0
    ? { state: 'installed', message: `已把模组装进游戏：${updated.join('；')}` }
    : { state: 'current', message: `游戏里的模组已经是 ${bundled}` }
}
