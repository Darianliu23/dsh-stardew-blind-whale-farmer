// 由 tools/build-client.mjs 生成，不要手改：改 lib/index.js 的 Config 或生成脚本后重新运行它。
// DSH 插件页里“配置”卡片的浏览器端模块：显示并保存这个插件的全部设置。
window.__ModuleLoader__.load({
	id: "dsh-stardew-blind-whale-farmer",
	factory: (require) => {
		var module = { exports: {} };
		const React = require("react");
		const h = React.createElement;
		const PACKAGE = "dsh-stardew-blind-whale-farmer";
		/** 插件页按 "<包名>#<行 id>" 找配置卡片；cordis.patch.yml 里这一行的 id 就是包名。 */
		const ROW_KEY = PACKAGE + "#" + PACKAGE;
		const GROUPS = [["play","游玩"],["guide","攻略"],["session","会话与连接"]];
		const FIELDS = [
		  {
		    "key": "sessionDays",
		    "group": "play",
		    "label": "每次玩几个游戏日",
		    "kind": "number",
		    "min": 0,
		    "description": "每次游玩最多玩几个游戏日（睡一觉算一天），到了让她停下向你汇报。0 = 不限。每次读档重新计。"
		  },
		  {
		    "key": "sessionMinutes",
		    "group": "play",
		    "label": "每次玩多少分钟",
		    "kind": "number",
		    "min": 0,
		    "description": "每次游玩最多玩多少现实分钟（从这次第一个动作开始算），到了让她回家睡觉存档后停下。0 = 不限。改这个值会从现在重新计时。"
		  },
		  {
		    "key": "freezeTime",
		    "group": "play",
		    "label": "思考时冻结时间",
		    "kind": "boolean",
		    "description": "AI 思考时冻结游戏时间（思考不占游戏里的时间）"
		  },
		  {
		    "key": "idleSeconds",
		    "group": "play",
		    "label": "空闲多少秒算不玩了",
		    "kind": "number",
		    "min": 10,
		    "max": 3600,
		    "description": "多少秒没有新指令就当 AI 不玩了，把游戏整个暂停（按任意键或 AI 再次行动时继续）"
		  },
		  {
		    "key": "forageRadius",
		    "group": "play",
		    "label": "顺手采集范围（格）",
		    "kind": "number",
		    "min": 0,
		    "max": 10,
		    "description": "走路时顺手采集野菜、贝壳这类野生采集物的范围（格）。0 = 不顺手采，1 = 只采紧挨着路的，更大会离开路线几步去采再回来。"
		  },
		  {
		    "key": "companionMode",
		    "group": "play",
		    "label": "联机时怎么安排自己",
		    "kind": "select",
		    "options": [
		      {
		        "value": "hybrid",
		        "label": "指令做完接着自己玩"
		      },
		      {
		        "value": "command",
		        "label": "主要听指令"
		      },
		      {
		        "value": "solo",
		        "label": "自己玩"
		      }
		    ],
		    "description": "联机时 AI 怎么安排自己：“指令做完接着自己玩”和“主要听指令”会放下手里的事听你说话，“自己玩”只回一句。也可以在游戏聊天里说“你自己玩吧”“听我的”“做完自己玩”来切换（拼音也行）。单人游戏不起作用。"
		  },
		  {
		    "key": "extraInstructions",
		    "group": "play",
		    "label": "额外要求",
		    "kind": "textarea",
		    "description": "给 AI 的额外要求，比如“这次专心钓鱼，攒钱买背包”。每次读游戏状态时都会看到。"
		  },
		  {
		    "key": "guideFile",
		    "group": "guide",
		    "label": "攻略文件",
		    "kind": "string",
		    "description": "攻略文件（相对游戏 Mods/BlindWhaleFarmer 目录）。想换打法就复制一份改名后填在这里。"
		  },
		  {
		    "key": "guideMain",
		    "group": "guide",
		    "label": "攻略·攻略",
		    "kind": "textarea",
		    "description": "攻略·攻略（主攻略：游戏常识、装备、第一年路线。每次连接都会读给 AI）。直接在这里改，十几秒内就会推给游戏里的 AI；清空或改回原样就用模组自带的。"
		  },
		  {
		    "key": "guideFishing",
		    "group": "guide",
		    "label": "攻略·钓鱼",
		    "kind": "textarea",
		    "description": "攻略·钓鱼（钓鱼专题：各水域的鱼、传说鱼、鱼饵钓具。AI 去钓鱼前会读）。直接在这里改，十几秒内就会推给游戏里的 AI；清空或改回原样就用模组自带的。"
		  },
		  {
		    "key": "guideMining",
		    "group": "guide",
		    "label": "攻略·采矿",
		    "kind": "textarea",
		    "description": "攻略·采矿（采矿专题：各层的矿石怪物、楼梯、宝箱。AI 下矿前会读）。直接在这里改，十几秒内就会推给游戏里的 AI；清空或改回原样就用模组自带的。"
		  },
		  {
		    "key": "guideFestival",
		    "group": "guide",
		    "label": "攻略·节日",
		    "kind": "textarea",
		    "description": "攻略·节日（节日专题：每个节日什么时候、去哪、做什么。节日当天 AI 会读）。直接在这里改，十几秒内就会推给游戏里的 AI；清空或改回原样就用模组自带的。"
		  },
		  {
		    "key": "port",
		    "group": "session",
		    "label": "游戏端口",
		    "kind": "number",
		    "min": 1,
		    "max": 65535,
		    "description": "游戏里模组 MCP 服务器的端口（和模组 config.json 的 McpPort 一致）。改了以后重启 DSH 才生效。"
		  },
		  {
		    "key": "presetEnabled",
		    "group": "session",
		    "label": "创建“大肥鱼种白饭”会话预设",
		    "kind": "boolean",
		    "description": "开启后会多出一个“大肥鱼种白饭”会话预设，新建会话时选它就能玩星露谷；只有选了它的会话才会连上游戏、加载玩游戏的工具和说明，其他会话的上下文完全不受影响。\n⚠ 已经对话过的会话用不了：DSH 规定会话开始聊天后就不能再换预设（这是 DSH 的限制，插件改不了），所以要玩请新建一个会话、在发第一条消息前选这个预设。\n不用担心接不上：游戏进度存在存档里，AI 用 update_notes 写的长期笔记也会出现在每次读取的游戏状态最前面，新会话能接着上次的计划玩。\n开关改了以后如果预设列表里没有变化，重启一下 DSH。"
		  },
		  {
		    "key": "gatewayEnabled",
		    "group": "session",
		    "label": "在所有会话里提供 stardew 入口工具",
		    "kind": "boolean",
		    "description": "开启后，所有会话（包括已经聊过的老会话）都会多一个小工具“stardew”，不用换预设也能玩星露谷。\n它只是一个入口：常驻上下文里只有这一个工具的简短说明（一两百 token），不会把四十多个游戏工具和长说明塞进每个会话；AI 真要玩时会先调 action=\"help\" 把操作规则和动作列表读进那个会话，攻略用 action=\"get_guide\" 按需读。\n和“大肥鱼种白饭”预设的区别：预设里每个动作都是独立的工具、参数检查更严格、规则一开始就在上下文里，专门开会话玩体验更好；入口工具胜在随时随地，聊天时顺口一句“去看看农场”就能玩。\n已经开着的会话一般下一轮就能用上；如果没有出现，重启一下 DSH。"
		  },
		  {
		    "key": "autoContinue",
		    "group": "session",
		    "label": "模型回复坏了自动接着玩",
		    "kind": "boolean",
		    "description": "模型偶尔会把工具调用吐成一串坏掉的文字（比如“</｜｜DSML｜｜ invoke>”），DSH 会以为它说完了，这一轮就停了。\n开启后，在玩星露谷的会话里遇到这种情况，插件会自动替你发一句“接着玩”。只认这一种坏掉的回复，不玩游戏的会话不受影响。"
		  },
		  {
		    "key": "autoContinueMax",
		    "group": "session",
		    "label": "连续自动续玩最多几次",
		    "kind": "number",
		    "min": 1,
		    "max": 20,
		    "description": "连续自动续玩最多几次（中间只要有一次游戏动作成功就重新计数），防止模型一直坏掉时无限循环"
		  },
		  {
		    "key": "presetName",
		    "group": "session",
		    "label": "预设名字",
		    "kind": "string",
		    "description": "这个预设在会话里显示的名字。改了以后重启 DSH 才生效。"
		  },
		  {
		    "key": "basePreset",
		    "group": "session",
		    "label": "基础预设",
		    "kind": "string",
		    "description": "在哪个预设的基础上加星露谷工具（默认 standard：和普通会话一样的能力，再多一套玩游戏的工具）。改了以后重启 DSH 才生效。"
		  },
		  {
		    "key": "toolTimeoutMinutes",
		    "group": "session",
		    "label": "单个动作最长等多少分钟",
		    "kind": "number",
		    "min": 1,
		    "max": 60,
		    "description": "单个游戏动作最长等多少分钟（钓鱼、下矿一次最长要 15 分钟）。改了以后重启 DSH 才生效。"
		  }
		];

		const box = { border: "1px solid rgba(128,128,128,.35)", borderRadius: 8, padding: "12px 16px", marginBottom: 16 };
		const input = { font: "inherit", color: "inherit", background: "transparent", border: "1px solid rgba(128,128,128,.45)", borderRadius: 6, padding: "4px 8px" };
		const hint = { fontSize: 12, opacity: 0.65, margin: "4px 0 0", whiteSpace: "pre-wrap" };
		const button = { font: "inherit", color: "inherit", background: "rgba(128,128,128,.15)", border: "1px solid rgba(128,128,128,.45)", borderRadius: 6, padding: "4px 14px", cursor: "pointer" };

		function same(a, b) {
			return JSON.stringify(a) === JSON.stringify(b);
		}

		/** 用户层里写过这个字段（哪怕值和默认一样）才算改过，“恢复默认”只对它有意义。 */
		function overridden(state, key) {
			return state.user !== null && typeof state.user === "object" && Object.prototype.hasOwnProperty.call(state.user, key);
		}

		function Field({ field, value, onChange, disabled }) {
			if (field.kind === "boolean") {
				return h("input", { type: "checkbox", checked: value === true, disabled, onChange: (e) => onChange(e.target.checked) });
			}
			if (field.kind === "number") {
				return h("input", {
					type: "number", step: 1, min: field.min, max: field.max, disabled,
					style: { ...input, width: 110 },
					value: value === undefined || value === null ? "" : String(value),
					onChange: (e) => onChange(e.target.value === "" ? undefined : Number(e.target.value)),
				});
			}
			if (field.kind === "select") {
				return h("select", { disabled, value: value === undefined || value === null ? "" : value, style: input, onChange: (e) => onChange(e.target.value) },
					field.options.map((o) => h("option", { key: o.value, value: o.value }, o.label)));
			}
			if (field.kind === "textarea") {
				const text = typeof value === "string" ? value : "";
				return h("textarea", {
					disabled, value: text,
					rows: Math.min(18, Math.max(3, text.split("\n").length + 1)),
					style: { ...input, width: "100%", boxSizing: "border-box", fontFamily: "ui-monospace, Consolas, monospace", fontSize: 12 },
					onChange: (e) => onChange(e.target.value),
				});
			}
			return h("input", { type: "text", disabled, value: typeof value === "string" ? value : "", style: { ...input, width: 280 }, onChange: (e) => onChange(e.target.value) });
		}

		/** 数字超出范围、不是整数时不让保存，返回错误说明。 */
		function invalid(field, value) {
			if (field.kind !== "number") return null;
			if (typeof value !== "number" || !Number.isInteger(value)) return "要填整数";
			if (field.min !== undefined && value < field.min) return "不能小于 " + field.min;
			if (field.max !== undefined && value > field.max) return "不能大于 " + field.max;
			return null;
		}

		function ConfigCard(props) {
			const form = props.form;
			const state = form ? form.state : undefined;
			const [draft, setDraft] = React.useState({});
			const [cleared, setCleared] = React.useState({});
			const [message, setMessage] = React.useState(null);
			const [saving, setSaving] = React.useState(false);
			const revision = state ? state.revision : undefined;
			// DSH 接受新的设置（别处改的，或者刚保存的）以后丢掉本地草稿
			React.useEffect(() => {
				setDraft({});
				setCleared({});
			}, [revision]);

			if (props.view === "summary") return "游玩时长、顺手采集范围、攻略和会话预设等设置";
			if (!form) return h("p", { style: hint }, "DSH 还没把这个插件的设置交给页面：确认插件在运行，或者重启一次 DSH。");
			if (state.status === "loading") return h("p", { style: hint }, "正在读取设置…");
			if (state.status !== "ready" || !state.value) return h("p", { style: hint }, "这个插件的设置现在读不到（DSH 没有对这个页面开放它）。");

			const current = state.value;
			const valueOf = (key) => (key in draft ? draft[key] : current[key]);
			const changed = FIELDS.filter((f) => cleared[f.key] || (f.key in draft && !same(draft[f.key], current[f.key])));
			const errors = changed.map((f) => (cleared[f.key] ? null : invalid(f, draft[f.key]))).filter(Boolean);
			const disabled = saving || !state.writable;

			const save = async () => {
				const ops = changed.map((f) => (cleared[f.key] ? { op: "unset", path: [f.key] } : { op: "set", path: [f.key], value: draft[f.key] }));
				setSaving(true);
				setMessage(null);
				try {
					const ok = await form.mutate(ops, state.revision);
					setMessage(ok ? "已保存。改动会在十几秒内同步到游戏。" : "DSH 没有接受这次修改，已重新读取当前设置。");
					if (ok) {
						setDraft({});
						setCleared({});
					}
				} catch (error) {
					setMessage("保存失败：" + (error && error.message ? error.message : String(error)));
				} finally {
					setSaving(false);
				}
			};

			const row = (field) => {
				const wide = field.kind === "textarea";
				const error = field.key in draft && !cleared[field.key] ? invalid(field, draft[field.key]) : null;
				const canReset = overridden(state, field.key) && !cleared[field.key];
				return h("div", { key: field.key, style: { margin: "12px 0" } },
					h("div", { style: { display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" } },
						h("label", { style: { minWidth: 200, fontWeight: 500 } }, field.label),
						wide ? null : h(Field, { field, value: valueOf(field.key), disabled, onChange: (v) => {
							setDraft({ ...draft, [field.key]: v });
							setCleared({ ...cleared, [field.key]: false });
						} }),
						canReset ? h("button", { type: "button", style: { ...button, padding: "2px 8px", fontSize: 12 }, disabled, onClick: () => setCleared({ ...cleared, [field.key]: true }) }, "恢复默认") : null,
						cleared[field.key] ? h("span", { style: hint }, "保存后恢复默认") : null),
					wide ? h("div", { style: { marginTop: 6 } }, h(Field, { field, value: valueOf(field.key), disabled, onChange: (v) => {
						setDraft({ ...draft, [field.key]: v });
						setCleared({ ...cleared, [field.key]: false });
					} })) : null,
					error ? h("p", { style: { ...hint, color: "#e5534b", opacity: 1 } }, error) : null,
					field.description ? h("p", { style: hint }, field.description) : null);
			};

			return h("div", null,
				GROUPS.map(([id, title]) => h("section", { key: id, style: box },
					h("h4", { style: { margin: "0 0 4px" } }, title),
					FIELDS.filter((f) => f.group === id).map(row))),
				h("div", { style: { display: "flex", alignItems: "center", gap: 12, position: "sticky", bottom: 0, padding: "8px 0" } },
					h("button", { type: "button", style: button, disabled: disabled || changed.length === 0 || errors.length > 0, onClick: save },
						saving ? "保存中…" : changed.length > 0 ? "保存 " + changed.length + " 项改动" : "保存"),
					changed.length > 0 && !saving ? h("button", { type: "button", style: button, onClick: () => { setDraft({}); setCleared({}); setMessage(null); } }, "撤销") : null,
					!state.writable ? h("span", { style: hint }, "DSH 的配置文件现在不能写入，只能查看。") : null,
					message ? h("span", { style: hint }, message) : null));
		}

		module.exports.name = PACKAGE + "-client";
		module.exports.inject = ["slots"];
		module.exports.apply = function apply(ctx) {
			ctx.slots.inject("plugins.row.config", () => ctx.slots.register({ name: "plugins.row.config", key: ROW_KEY }, ConfigCard));
		};
		module.exports.FIELDS = FIELDS;
		return module.exports;
	}
});
