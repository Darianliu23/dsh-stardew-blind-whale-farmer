// 生成 lib/client.js：DSH 插件页里“配置”卡片的浏览器端模块。
//
// DSH 不会照着插件的 Config 自动画设置表单：插件要自己带一个浏览器端模块（package.json 的
// dsh.client + exports["./client"]），在插件页的 plugins.row.config 插槽里登记一张卡片，
// 名字旁边才会出现“配置 ›”。卡片读写的是 DSH 交给它的 form（这一行的设置命名空间）。
//
// 字段直接从 lib/index.js 的 Config 读出来（类型、范围、说明），这里只补短标题和分组，
// 所以 Config 加了字段忘记重新生成时，tests/DshPlugin/client.test.mjs 会报错。
//
// 用法：node tools/build-client.mjs

import { writeFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { Config, GUIDE_FIELDS, name as packageName } from '../lib/index.js'

/** 卡片上的短标题和分组（和 docs/dsh-plugin.md 的设置表一致）。 */
const LABELS = {
  sessionDays: ['play', '每次玩几个游戏日'],
  sessionMinutes: ['play', '每次玩多少分钟'],
  freezeTime: ['play', '思考时冻结时间'],
  idleSeconds: ['play', '空闲多少秒算不玩了'],
  forageRadius: ['play', '顺手采集范围（格）'],
  extraInstructions: ['play', '额外要求'],
  guideFile: ['guide', '攻略文件'],
  ...Object.fromEntries(GUIDE_FIELDS.map(([key, file]) => [key, ['guide', `攻略·${file}`]])),
  presetEnabled: ['session', '创建“大肥鱼种白饭”会话预设'],
  gatewayEnabled: ['session', '在所有会话里提供 stardew 入口工具'],
  autoContinue: ['session', '模型回复坏了自动接着玩'],
  autoContinueMax: ['session', '连续自动续玩最多几次'],
  presetName: ['session', '预设名字'],
  basePreset: ['session', '基础预设'],
  port: ['session', '游戏端口'],
  toolTimeoutMinutes: ['session', '单个动作最长等多少分钟'],
}

const GROUPS = [
  ['play', '游玩'],
  ['guide', '攻略'],
  ['session', '会话与连接'],
]

export function buildFields() {
  const dict = Config?.dict
  if (!dict) throw new Error('lib/index.js 的 Config 不是 schemastery 对象（没装 @deepseek-ai/schemastery？）')
  return Object.entries(dict).map(([key, schema]) => {
    const label = LABELS[key]
    if (!label) throw new Error(`Config.${key} 没有短标题：在 tools/build-client.mjs 的 LABELS 里补上`)
    const meta = schema.meta ?? {}
    const kind = schema.type === 'boolean' ? 'boolean'
      : schema.type === 'number' ? 'number'
      : meta.role === 'textarea' ? 'textarea' : 'string'
    return {
      key,
      group: label[0],
      label: label[1],
      kind,
      ...meta.min !== undefined ? { min: meta.min } : {},
      ...meta.max !== undefined ? { max: meta.max } : {},
      description: String(meta.description ?? ''),
    }
  })
}

export function buildClient() {
  const fields = buildFields()
  return TEMPLATE
    .replaceAll('__PACKAGE__', JSON.stringify(packageName))
    .replace('__FIELDS__', JSON.stringify(fields, null, 2).replaceAll('\n', '\n\t\t'))
    .replace('__GROUPS__', JSON.stringify(GROUPS))
}

const TEMPLATE = `// 由 tools/build-client.mjs 生成，不要手改：改 lib/index.js 的 Config 或生成脚本后重新运行它。
// DSH 插件页里“配置”卡片的浏览器端模块：显示并保存这个插件的全部设置。
window.__ModuleLoader__.load({
\tid: __PACKAGE__,
\tfactory: (require) => {
\t\tvar module = { exports: {} };
\t\tconst React = require("react");
\t\tconst h = React.createElement;
\t\tconst PACKAGE = __PACKAGE__;
\t\t/** 插件页按 "<包名>#<行 id>" 找配置卡片；cordis.patch.yml 里这一行的 id 就是包名。 */
\t\tconst ROW_KEY = PACKAGE + "#" + PACKAGE;
\t\tconst GROUPS = __GROUPS__;
\t\tconst FIELDS = __FIELDS__;

\t\tconst box = { border: "1px solid rgba(128,128,128,.35)", borderRadius: 8, padding: "12px 16px", marginBottom: 16 };
\t\tconst input = { font: "inherit", color: "inherit", background: "transparent", border: "1px solid rgba(128,128,128,.45)", borderRadius: 6, padding: "4px 8px" };
\t\tconst hint = { fontSize: 12, opacity: 0.65, margin: "4px 0 0", whiteSpace: "pre-wrap" };
\t\tconst button = { font: "inherit", color: "inherit", background: "rgba(128,128,128,.15)", border: "1px solid rgba(128,128,128,.45)", borderRadius: 6, padding: "4px 14px", cursor: "pointer" };

\t\tfunction same(a, b) {
\t\t\treturn JSON.stringify(a) === JSON.stringify(b);
\t\t}

\t\t/** 用户层里写过这个字段（哪怕值和默认一样）才算改过，“恢复默认”只对它有意义。 */
\t\tfunction overridden(state, key) {
\t\t\treturn state.user !== null && typeof state.user === "object" && Object.prototype.hasOwnProperty.call(state.user, key);
\t\t}

\t\tfunction Field({ field, value, onChange, disabled }) {
\t\t\tif (field.kind === "boolean") {
\t\t\t\treturn h("input", { type: "checkbox", checked: value === true, disabled, onChange: (e) => onChange(e.target.checked) });
\t\t\t}
\t\t\tif (field.kind === "number") {
\t\t\t\treturn h("input", {
\t\t\t\t\ttype: "number", step: 1, min: field.min, max: field.max, disabled,
\t\t\t\t\tstyle: { ...input, width: 110 },
\t\t\t\t\tvalue: value === undefined || value === null ? "" : String(value),
\t\t\t\t\tonChange: (e) => onChange(e.target.value === "" ? undefined : Number(e.target.value)),
\t\t\t\t});
\t\t\t}
\t\t\tif (field.kind === "textarea") {
\t\t\t\tconst text = typeof value === "string" ? value : "";
\t\t\t\treturn h("textarea", {
\t\t\t\t\tdisabled, value: text,
\t\t\t\t\trows: Math.min(18, Math.max(3, text.split("\\n").length + 1)),
\t\t\t\t\tstyle: { ...input, width: "100%", boxSizing: "border-box", fontFamily: "ui-monospace, Consolas, monospace", fontSize: 12 },
\t\t\t\t\tonChange: (e) => onChange(e.target.value),
\t\t\t\t});
\t\t\t}
\t\t\treturn h("input", { type: "text", disabled, value: typeof value === "string" ? value : "", style: { ...input, width: 280 }, onChange: (e) => onChange(e.target.value) });
\t\t}

\t\t/** 数字超出范围、不是整数时不让保存，返回错误说明。 */
\t\tfunction invalid(field, value) {
\t\t\tif (field.kind !== "number") return null;
\t\t\tif (typeof value !== "number" || !Number.isInteger(value)) return "要填整数";
\t\t\tif (field.min !== undefined && value < field.min) return "不能小于 " + field.min;
\t\t\tif (field.max !== undefined && value > field.max) return "不能大于 " + field.max;
\t\t\treturn null;
\t\t}

\t\tfunction ConfigCard(props) {
\t\t\tconst form = props.form;
\t\t\tconst state = form ? form.state : undefined;
\t\t\tconst [draft, setDraft] = React.useState({});
\t\t\tconst [cleared, setCleared] = React.useState({});
\t\t\tconst [message, setMessage] = React.useState(null);
\t\t\tconst [saving, setSaving] = React.useState(false);
\t\t\tconst revision = state ? state.revision : undefined;
\t\t\t// DSH 接受新的设置（别处改的，或者刚保存的）以后丢掉本地草稿
\t\t\tReact.useEffect(() => {
\t\t\t\tsetDraft({});
\t\t\t\tsetCleared({});
\t\t\t}, [revision]);

\t\t\tif (props.view === "summary") return "游玩时长、顺手采集范围、攻略和会话预设等设置";
\t\t\tif (!form) return h("p", { style: hint }, "DSH 还没把这个插件的设置交给页面：确认插件在运行，或者重启一次 DSH。");
\t\t\tif (state.status === "loading") return h("p", { style: hint }, "正在读取设置…");
\t\t\tif (state.status !== "ready" || !state.value) return h("p", { style: hint }, "这个插件的设置现在读不到（DSH 没有对这个页面开放它）。");

\t\t\tconst current = state.value;
\t\t\tconst valueOf = (key) => (key in draft ? draft[key] : current[key]);
\t\t\tconst changed = FIELDS.filter((f) => cleared[f.key] || (f.key in draft && !same(draft[f.key], current[f.key])));
\t\t\tconst errors = changed.map((f) => (cleared[f.key] ? null : invalid(f, draft[f.key]))).filter(Boolean);
\t\t\tconst disabled = saving || !state.writable;

\t\t\tconst save = async () => {
\t\t\t\tconst ops = changed.map((f) => (cleared[f.key] ? { op: "unset", path: [f.key] } : { op: "set", path: [f.key], value: draft[f.key] }));
\t\t\t\tsetSaving(true);
\t\t\t\tsetMessage(null);
\t\t\t\ttry {
\t\t\t\t\tconst ok = await form.mutate(ops, state.revision);
\t\t\t\t\tsetMessage(ok ? "已保存。改动会在十几秒内同步到游戏。" : "DSH 没有接受这次修改，已重新读取当前设置。");
\t\t\t\t\tif (ok) {
\t\t\t\t\t\tsetDraft({});
\t\t\t\t\t\tsetCleared({});
\t\t\t\t\t}
\t\t\t\t} catch (error) {
\t\t\t\t\tsetMessage("保存失败：" + (error && error.message ? error.message : String(error)));
\t\t\t\t} finally {
\t\t\t\t\tsetSaving(false);
\t\t\t\t}
\t\t\t};

\t\t\tconst row = (field) => {
\t\t\t\tconst wide = field.kind === "textarea";
\t\t\t\tconst error = field.key in draft && !cleared[field.key] ? invalid(field, draft[field.key]) : null;
\t\t\t\tconst canReset = overridden(state, field.key) && !cleared[field.key];
\t\t\t\treturn h("div", { key: field.key, style: { margin: "12px 0" } },
\t\t\t\t\th("div", { style: { display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" } },
\t\t\t\t\t\th("label", { style: { minWidth: 200, fontWeight: 500 } }, field.label),
\t\t\t\t\t\twide ? null : h(Field, { field, value: valueOf(field.key), disabled, onChange: (v) => {
\t\t\t\t\t\t\tsetDraft({ ...draft, [field.key]: v });
\t\t\t\t\t\t\tsetCleared({ ...cleared, [field.key]: false });
\t\t\t\t\t\t} }),
\t\t\t\t\t\tcanReset ? h("button", { type: "button", style: { ...button, padding: "2px 8px", fontSize: 12 }, disabled, onClick: () => setCleared({ ...cleared, [field.key]: true }) }, "恢复默认") : null,
\t\t\t\t\t\tcleared[field.key] ? h("span", { style: hint }, "保存后恢复默认") : null),
\t\t\t\t\twide ? h("div", { style: { marginTop: 6 } }, h(Field, { field, value: valueOf(field.key), disabled, onChange: (v) => {
\t\t\t\t\t\tsetDraft({ ...draft, [field.key]: v });
\t\t\t\t\t\tsetCleared({ ...cleared, [field.key]: false });
\t\t\t\t\t} })) : null,
\t\t\t\t\terror ? h("p", { style: { ...hint, color: "#e5534b", opacity: 1 } }, error) : null,
\t\t\t\t\tfield.description ? h("p", { style: hint }, field.description) : null);
\t\t\t};

\t\t\treturn h("div", null,
\t\t\t\tGROUPS.map(([id, title]) => h("section", { key: id, style: box },
\t\t\t\t\th("h4", { style: { margin: "0 0 4px" } }, title),
\t\t\t\t\tFIELDS.filter((f) => f.group === id).map(row))),
\t\t\t\th("div", { style: { display: "flex", alignItems: "center", gap: 12, position: "sticky", bottom: 0, padding: "8px 0" } },
\t\t\t\t\th("button", { type: "button", style: button, disabled: disabled || changed.length === 0 || errors.length > 0, onClick: save },
\t\t\t\t\t\tsaving ? "保存中…" : changed.length > 0 ? "保存 " + changed.length + " 项改动" : "保存"),
\t\t\t\t\tchanged.length > 0 && !saving ? h("button", { type: "button", style: button, onClick: () => { setDraft({}); setCleared({}); setMessage(null); } }, "撤销") : null,
\t\t\t\t\t!state.writable ? h("span", { style: hint }, "DSH 的配置文件现在不能写入，只能查看。") : null,
\t\t\t\t\tmessage ? h("span", { style: hint }, message) : null));
\t\t}

\t\tmodule.exports.name = PACKAGE + "-client";
\t\tmodule.exports.inject = ["slots"];
\t\tmodule.exports.apply = function apply(ctx) {
\t\t\tctx.slots.inject("plugins.row.config", () => ctx.slots.register({ name: "plugins.row.config", key: ROW_KEY }, ConfigCard));
\t\t};
\t\tmodule.exports.FIELDS = FIELDS;
\t\treturn module.exports;
\t}
});
`

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  const out = fileURLToPath(new URL('../lib/client.js', import.meta.url))
  writeFileSync(out, buildClient())
  console.log(`写入 ${out}（${buildFields().length} 个设置项）`)
}
