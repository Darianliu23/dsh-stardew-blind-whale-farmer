// “大肥鱼种白饭”会话预设里的一个小插件：给这个预设的会话加一个 stardew_launch_game 工具。
// 游戏没开时，预设里连游戏的 MCP 客户端连不上，会话里一个游戏工具都没有，AI 只能干等；
// 有了它，AI 自己就能把游戏开起来、读存档。只在这个预设的会话里有，别的会话看不到。
//
// 预设注册时把这一行加进去（见 index.js 的 launcherRow）：
//   { id: 'stardew-launcher', name: 'dsh-stardew-blind-whale-farmer/launcher', config: { port, gamePath, toolTimeoutMinutes } }

import { defineTool } from './index.js'
import { launchGame } from './game-control.js'

export const name = 'dsh-stardew-blind-whale-farmer-launcher'

export const LAUNCH_TOOL = 'stardew_launch_game'

export function launchTool(config, launch = launchGame) {
  const port = Number(config?.port ?? 18080)
  const timeoutMs = Math.max(5, Number(config?.toolTimeoutMinutes ?? 16)) * 60 * 1000
  return defineTool({
    name: LAUNCH_TOOL,
    description:
      '启动《星露谷物语》并读取存档（通过 SMAPI，带“大肥鱼种白饭”模组）。游戏没开、你的工具列表里没有星露谷的游戏工具（get_state 等）时用它。'
      + '会等到进了存档才返回，大约要一两分钟；游戏已经开着就直接告诉你。进游戏后半分钟内游戏工具会出现，然后先 get_state。',
    parameters: {
      save: { type: 'string', description: '存档名（农夫名字或存档文件夹名的一部分）。不传就读最近玩的那个' },
    },
    output: {
      schema: { type: 'string' },
      render: (_args, value) => [{ type: 'text', text: String(value) }],
    },
    async execute(args, exec) {
      const signal = exec?.signal ? AbortSignal.any([exec.signal, AbortSignal.timeout(timeoutMs)]) : AbortSignal.timeout(timeoutMs)
      try {
        const text = await launch({ port, gamePath: config?.gamePath ?? '', save: args?.save, signal })
        // 预设里的游戏工具是 MCP 客户端在后台重连后才注册的，刚进游戏时可能还没有
        return text.startsWith('成功') ? text + '。如果工具列表里还没有游戏工具，等半分钟再试' : text
      } catch (error) {
        return '失败：' + (error?.message ?? error)
      }
    },
  })
}

export function apply(ctx, config) {
  const tool = launchTool(config)
  ctx.effect(() => {
    let tools
    try {
      tools = ctx.get?.('tools')
    } catch {}
    if (!tools?.register) return () => {}
    let off
    try {
      off = tools.register(tool)
    } catch (error) {
      try { ctx.logger?.('warn', '[dsh-stardew-blind-whale-farmer] 添加开游戏工具失败：' + (error?.message ?? error)) } catch {}
    }
    return () => {
      try { typeof off === 'function' ? off() : off?.dispose?.() } catch {}
    }
  })
}
