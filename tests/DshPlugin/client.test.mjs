// 插件页“配置”卡片（lib/client.js）的离线测试：按 DSH 的 __ModuleLoader__ 格式加载，
// 用最小的 React 替身渲染出元素树来检查，不需要浏览器。
import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'
import vm from 'node:vm'
import { buildClient } from '../../tools/build-client.mjs'
import { Config, name } from '../../lib/index.js'

const clientFile = new URL('../../lib/client.js', import.meta.url)
const pkg = JSON.parse(readFileSync(new URL('../../package.json', import.meta.url), 'utf8'))

/** 最小的 React 替身：createElement 生成普通对象；useState 每次都返回初始值（只渲染一次够用）。 */
function fakeReact() {
  return {
    createElement: (type, props, ...children) => ({ type, props: props ?? {}, children: children.flat(Infinity) }),
    useState: (init) => [init, () => {}],
    useEffect: () => {},
  }
}

function loadClient() {
  let loaded
  const sandbox = { window: { __ModuleLoader__: { load: (m) => { loaded = m } } } }
  vm.runInNewContext(readFileSync(clientFile, 'utf8'), sandbox, { filename: 'client.js' })
  assert.equal(loaded.id, name)
  return loaded.factory((spec) => {
    assert.equal(spec, 'react')
    return fakeReact()
  })
}

/** 把元素树（含函数组件）展开成所有文字。 */
function textOf(node) {
  if (node === null || node === undefined || node === false) return ''
  if (typeof node === 'string' || typeof node === 'number') return String(node)
  if (Array.isArray(node)) return node.map(textOf).join('')
  if (typeof node.type === 'function') return textOf(node.type(node.props))
  return textOf(node.children)
}

function findAll(node, pred, out = []) {
  if (node === null || typeof node !== 'object') return out
  if (Array.isArray(node)) {
    for (const child of node) findAll(child, pred, out)
    return out
  }
  if (typeof node.type === 'function') return findAll(node.type(node.props), pred, out)
  if (pred(node)) out.push(node)
  return findAll(node.children, pred, out)
}

test('lib/client.js 是最新生成的（改了 Config 记得运行 npm run build:client）', () => {
  assert.equal(readFileSync(clientFile, 'utf8'), buildClient())
})

test('package.json 声明了浏览器端模块，DSH 才会加载卡片', () => {
  assert.equal(pkg.exports['./client'], './lib/client.js')
  assert.equal(pkg.dsh.client.platform, 'web')
  assert.ok(pkg.files.includes('lib'))
})

test('卡片覆盖 Config 的全部设置项', () => {
  const client = loadClient()
  assert.deepEqual(Array.from(client.FIELDS, (f) => f.key), Object.keys(Config.dict))
})

test('在插件页的 plugins.row.config 插槽按 包名#行id 登记', () => {
  const client = loadClient()
  assert.deepEqual(Array.from(client.inject), ['slots'])
  const registered = []
  client.apply({
    slots: {
      inject: (slot, fn) => {
        assert.equal(slot, 'plugins.row.config')
        return fn()
      },
      register: (options, component) => {
        registered.push({ options, component })
        return () => {}
      },
    },
  })
  assert.equal(registered.length, 1)
  assert.deepEqual({ ...registered[0].options }, { name: 'plugins.row.config', key: `${name}#${name}` })
})

function renderCard(props) {
  const client = loadClient()
  let card
  client.apply({ slots: { inject: (_, fn) => fn(), register: (_, c) => { card = c } } })
  return card(props)
}

const readyState = {
  status: 'ready',
  value: Object.fromEntries(Object.entries(Config.dict).map(([k, s]) => [k, s.meta?.default])),
  base: {},
  user: { forageRadius: 3 },
  revision: 7,
  writable: true,
}

test('页面视图：分组、标题、当前值、改过的项可以恢复默认', () => {
  const tree = renderCard({ view: 'page', form: { state: { ...readyState, value: { ...readyState.value, forageRadius: 3 } }, mutate: async () => true } })
  const text = textOf(tree)
  for (const title of ['游玩', '攻略', '会话与连接', '顺手采集范围（格）', '攻略·钓鱼', '每次玩几个游戏日']) assert.ok(text.includes(title), title)
  const radius = findAll(tree, (n) => n.type === 'input' && n.props.type === 'number' && n.props.max === 10)
  assert.equal(radius.length, 1)
  assert.equal(radius[0].props.value, '3')
  // 只有用户层里写过的 forageRadius 有“恢复默认”
  assert.equal(findAll(tree, (n) => n.type === 'button' && textOf(n) === '恢复默认').length, 1)
  // 没有改动时保存按钮是灰的
  const save = findAll(tree, (n) => n.type === 'button' && textOf(n).startsWith('保存'))
  assert.equal(save[0].props.disabled, true)
})

test('摘要视图和读不到设置时给出说明，不报错', () => {
  assert.match(textOf(renderCard({ view: 'summary' })), /顺手采集范围/)
  assert.match(textOf(renderCard({ view: 'page' })), /重启一次 DSH/)
  assert.match(textOf(renderCard({ view: 'page', form: { state: { ...readyState, status: 'loading', value: undefined }, mutate: async () => true } })), /正在读取/)
  assert.match(textOf(renderCard({ view: 'page', form: { state: { ...readyState, status: 'unavailable', value: undefined }, mutate: async () => true } })), /读不到/)
})
