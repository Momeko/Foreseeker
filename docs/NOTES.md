# 研究记录

本文件记录「求解器为什么可以做成确定性沙盘」的证据与数据，全部来自实机日志。

## 1. 随机流模型

`Assets.Code.Math.RandomContainer` 为每个 `RandomIdentifier` 维护独立的
`UnityEngine.Random.State`，切换流时把状态灌进 `UnityEngine.Random` 再取回：

```csharp
LoadStateForIdentifier(id)  ->  UnityEngine.Random.state = m_randomMap[id]
draw                        ->  UnityEngine.Random.value / Range
ReturnToUnidentifiedState() ->  m_randomMap[id] = UnityEngine.Random.state
```

因此：

- 每条流互不影响；
- 状态随 `run.json` 的 `random.m_randomMap` 存档 → 读档后行为完全可复现；
- 快照 = `RandomContainer.SaveToJson()`，回滚 = `RandomContainer.LoadFromJson()`。

| 流 | 谁在用 | 玩家操作影响吗 |
|---|---|---|
| `ACTOR_CONTROLLER` | 敌方 AI（技能 + 目标） | 不影响 |
| `SKILL_CALCULATION` | 技能预计算（暴击 / 伤害区间） | 不影响（见 §2） |
| `EFFECT` / `RESIST` / `TOKEN` / `DOT` | 效果结算与预计算 | 结算阶段会 |
| `COMBAT` | 回合顺序掷骰 | 不影响 |
| `LOCALIZATION` / `ANIMATION` / `AESTHETIC` | UI / 表现 | 无战斗影响 |

## 2. 预计算时机（P1 结论）

对同一场战斗统计 10 次 `P PRECOMPUTE`：**10 次全部发生在 `start_turn`，即进入
`in_turn_select` 之前**。

进入 `in_turn_select` 之后到 `in_turn_calculate` 之间（玩家选技能、换技能的时间窗），
`SKILL_CALCULATION` 的抽取次数为 **0**，窗口内只出现 `LOCALIZATION`：

```
selects=4 skilldraws=0 | LOCALIZATION=40
selects=2 skilldraws=0 | LOCALIZATION=440
selects=1 skilldraws=0 | LOCALIZATION=9
```

结论：**每个行动者整回合的（技能 × 目标）结果，在回合开始就已经全部掷完**。玩家怎么点
都不会重掷，只影响真正结算（apply）阶段消耗的效果类流。

## 3. 单条结果的 RNG 成本

`P ADDPOT ... skcost= allcost=` 记录每条 `(技能, 目标)` 的消耗：

| 类型 | `SKILL_CALCULATION` | 说明 |
|---|---|---|
| 普通单目标伤害 | 2 | 1 次暴击 + 1 次伤害区间（日志里 `CheckValue` 会额外打一条 `GetValue`，是同一个值的两层） |
| 多目标 / 多段 | 2 × 目标数 | 整组一次掷，结果登记到每个目标 |
| 位移技能（`gr_move`） | 0 | 抽的是 `AFFINITY` / `ACT_OUT` |
| 自我 buff（`take_aim`） | 1 | 只有暴击判定 |

一个行动者回合的 `SKILL_CALCULATION` 消耗实测 6–27 次，含其它流的总消耗 13–110 次。
结论：**不要自己写公式复算**，必须调用游戏的 `CalculateSkillResults`。

## 4. 预计算重放验证（P2 结论）

做法：

1. 在 `ActorControllerBase.CalculateValidSkillTargetEntries(..., triggerEvent=true)` 的
   prefix 里快照 RNG（此时尚未掷任何值）；
2. 用 `new SkillCalculation(teams, turnOrder, tracking)` 建临时实例，
   按 `m_IsMultiHit` 复刻「整组一次掷 / 单目标逐个掷」的调用顺序；
3. 与游戏自己的 `m_PotentialResults` 逐条比对 `HealthDamage` / `IsCrit`；
4. 恢复 RNG，并比对战斗状态哈希。

9 次实测（双方、含多段技能）：

```
actor=6422 replayed=14 multiHit=1 matched=14 mismatched=0 rng=46  state=UNCHANGED
actor=6424 replayed=15 multiHit=0 matched=15 mismatched=0 rng=110 state=UNCHANGED
actor=6433 replayed=7  multiHit=0 matched=7  mismatched=0 rng=68  state=UNCHANGED
actor=6271 replayed=9  multiHit=1 matched=9  mismatched=0 rng=34  state=UNCHANGED
actor=6425 replayed=14 multiHit=0 matched=14 mismatched=0 rng=74  state=UNCHANGED
actor=6432 replayed=2  multiHit=0 matched=2  mismatched=0 rng=14  state=UNCHANGED
actor=6431 replayed=2  multiHit=0 matched=2  mismatched=0 rng=13  state=UNCHANGED
actor=6271 replayed=5  multiHit=0 matched=5  mismatched=0 rng=23  state=UNCHANGED
actor=6422 replayed=14 multiHit=1 matched=14 mismatched=0 rng=75  state=UNCHANGED
```

`state=UNCHANGED` 的哈希覆盖：HP、压力、站位、位移偏移、存活、死门计数、已选技能、
token / DOT / buff 数量。结论：**原地重放是安全的**，求解器不需要克隆整棵战斗对象图。

## 5. 宿主生命周期坑

BepInEx 把插件组件挂在共享的 `BepInEx_Manager` 上，游戏在启动收尾时会销毁它：

```
LIFECYCLE Awake object=BepInEx_Manager parent=<root>
LIFECYCLE OnEnable
[Chainloader startup complete]
LIFECYCLE OnDisable
LIFECYCLE OnDestroy
```

`DontDestroyOnLoad` 在对象是根物体时调用也无效。Harmony 钩子不受影响（静态挂载），所以
日志看起来"插件还活着"，实际 Update / OnGUI 全停。

解决办法：`ForeseekerHost.cs` 自建 `ForeseekerHost` 物体 + `Runner` 组件驱动 Update/OnGUI，
并在场景加载、战斗状态变化时自动重建。

## 6. 资源权重

| 项 | 代价 |
|---|---|
| 英雄死亡 | 硬约束（剪枝）；全死时按「死得最少 → 最晚 → 剩余资源最多」退化 |
| 进死门 | 3 / 每回合计 |
| 压力 | 1 / 点，8 点后加速（满压触发 overstress 抽签） |
| 血量 | 5 × (损失 / 满血) |
| 敌方死亡 | 正收益（v1 给 +30 的固定奖励） |
