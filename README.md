# Foreseeker

《Darkest Dungeon II》的战斗求解器（BepInEx 5 插件，开发中）。

目标：在克隆的战斗沙盘上，用游戏自己的结算代码推演「这一步 / 这一轮 / 这场战斗」的准确走向，再按资源代价搜索最优出招。

当前状态：**沙盘地基已验证**，求解器 HUD 已可用。

## 已验证的关键事实

1. **战斗随机数是确定性的、可存档回放的**。`Assets.Code.Math.RandomContainer` 为每个
   `RandomIdentifier` 维护独立的 `UnityEngine.Random.State`，读档恢复后同一存档行为完全一致。
2. **敌方 AI 只吃 `ACTOR_CONTROLLER`**，玩家做什么都不影响它 → 敌方意图可以提前知道。
3. **每个行动者的整回合结果在回合开始就全部掷完**（`start_turn` 状态的预计算，
   早于 `in_turn_select`）。玩家选哪个技能、换几次技能、悬停多久，都**不会**重掷
   `SKILL_CALCULATION`。
4. **预计算可以在副本里完整重放**：回滚 RNG 到回合开始的快照，用临时
   `SkillCalculation` 实例重跑全部 `(技能, 目标)`，伤害/暴击与游戏自己算的**逐条一致**
   （多段技能也一致），且战斗状态哈希不变。

详细数据与推导见 [docs/NOTES.md](docs/NOTES.md)。

## 当前功能

| 窗口 / 键 | 内容 |
|---|---|
| 左上（F2 / F7） | 我方选中技能的精确结果 |
| 右下（F10） | 敌方本轮行动预测 |
| 右侧（F5） | 求解器候选排序：当前行动者每个 `(技能, 目标)` 的精确伤害/暴击与评分 |
| F3 | 重放实验（自动在每次 `in_turn_select` 触发，用于验证沙盘） |
| F4 | 快照 / 回滚自测 |
| F11 | 应用一手 → 校验 → 回滚自测 |
| F8 | 主窗口复位 |

> 不要用 F6：游戏自己拿它做 Debug 上报。

求解器 v1 的评分（即时收益，不含敌方反击）：

```
打敌人:  +5 × (伤害 / 敌人最大血)   击杀额外 +30
打队友:  -5 × (伤害 / 队友最大血)   击杀 -1000（红字）
token:   按 TokenDefinition.IsPositive / IsNegative 判定，每层 1.5，按持有方阵营翻转
DOT:     每层 1.0，对持有方为负（挂敌人得分）
治疗:    +5 × (治疗量 / 最大血)
```

效果数据取自游戏本回合已预计算的 `m_AppliedEffectsOutputContainer`，只统计"实际会挂上"的效果
（被抵抗的不在其中）。

资源权重（后续版本使用）：英雄死亡 = 硬约束剪枝；进死门 = 3；压力 = 1/点（8 点后加速）；
血量 = 5 × (损失 / 满血)。

## 构建

```
src\build.cmd
```

会编译并部署到 `BepInEx\plugins\Foreseeker.dll`。游戏运行时 dll 被占用，脚本会先改名为 `.prev`，
需要重启游戏才会加载新版本。

依赖：.NET SDK 的 `csc`（Roslyn），游戏自带的 `Managed\*.dll`、`BepInEx\core\BepInEx.dll`、
`0Harmony.dll`。

## 日志

BepInEx 日志 `BepInEx\LogOutput.log` 里：

| 前缀 | 含义 |
|---|---|
| `P STATE` / `P ROUND` / `P PRECOMPUTE` | 战斗阶段与预计算时机 |
| `P ADDPOT ... skcost= allcost=` | 单条结果的 RNG 消耗 |
| `P SELECT ... skdraws=` | 玩家选技能时的抽取（应为 0） |
| `SCDRAW` | `SKILL_CALCULATION` 每次抽取 |
| `R REPLAY ...` | 重放实验：`matched/mismatched/rng/state` |
| `UPD` | 驱动心跳（每 5 秒） |
| `LIFECYCLE` | 组件生命周期诊断 |

读日志：

```
powershell -NoProfile -ExecutionPolicy Bypass -File tools\read-probe.ps1
```

## 结构

```
src/
  Plugin.cs        BepInEx 插件主体、预测、HUD
  SaveReader.cs    存档 / 本地化 / CSV 数据读取
  RandomTrace.cs   RNG 钩子（逐条记录所有流）
  Probe.cs         实验探针：阶段标记、预计算成本、回合开始 RNG 快照
  SolverProbe.cs   预计算重放实验（验证沙盘）
  Solver.cs        求解器候选枚举与评分
  ForeseekerHost.cs   自建驱动对象（BepInEx 宿主会被游戏销毁）
tools/
  read-probe.ps1   日志读取与统计
docs/
  NOTES.md         研究记录：随机流、预计算、重放验证
```

## 路线

1. ~~验证预计算重放可行性~~ ✅
2. ~~候选枚举 + 精确数值 + 即时评分 HUD~~ ✅
3. 把效果（token / DOT / buff）折算进评分
4. 在我方结算后的状态上重跑下一个行动者的预计算 → 敌方反击进入同一次评估
5. 整轮滚动评估 → 束搜索
6. 一键执行当前回合（每步执行后对拍，失配即停）

## 许可

MIT，见 [LICENSE](LICENSE)。
