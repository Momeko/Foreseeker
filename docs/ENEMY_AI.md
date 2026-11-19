# 敌方 AI 行为逻辑

本文记录我们对《暗黑地牢 2》敌方 AI 的逆向结论。结论来自 `IronCrown.dll` 反编译（`Assets.Code.Actor.ActorController.ActorControllerRandom`）与实机日志验证。

一句话版本：**敌人先按技能权重随机挑一个技能，再在该技能的合法目标里等概率挑一个目标。两次随机都来自 `ACTOR_CONTROLLER` 流。**

---

## 1. 控制器

普通敌人使用 `ActorControllerRandom`（继承 `ActorControllerBase`），没有仇恨表、没有智能评分，行为完全由随机数决定。

日志中可见：

```
PREDICT: controller=ActorControllerRandom
```

## 2. 两步随机

一次敌方行动消耗 `ACTOR_CONTROLLER` 流的**两个**随机值。

### 2.1 选技能（加权）

```csharp
WeightedBag<string> bag = new WeightedBag<string>();
foreach (SkillTargetEntry entry in GetValidSkillTargetEntries())
{
    ActorDataSkill skill = Library.GetLibraryElement(entry.m_SkillId);
    bag.AddValueWeight(entry.m_SkillId, skill.m_RandomSelectChance);
}
string skillId = bag.GetRandomValue(RandomIdentifier.ACTOR_CONTROLLER);
```

- 权重是技能自己的 `m_RandomSelectChance`，不是均匀分布；
- 高权重技能（例如 boss 的终极技）出现频率明显更高；
- 权重为 0 的技能不会被选中。

### 2.2 选目标（均匀）

```csharp
foreach (SkillTargetEntry entry in m_SkillTargetEntries)
{
    if (entry.m_SkillId == selectedSkillId && entry.m_ValidTargetActorGuids.Count > 0)
    {
        targetGuid = ListUtils.GetRandomValue(entry.m_ValidTargetActorGuids,
                                              RandomIdentifier.ACTOR_CONTROLLER);
        break;
    }
}
```

在该技能的所有合法目标里**等概率**选一个，没有偏好、没有仇恨。

## 3. 无技能可用时跳过

```csharp
bool CalculateIsSkip() => GetValidSkillTargetEntries().IsEmpty();
```

条目为空时这一回合“跳过”（等同于 pass 技能，自身压力 +2）。

## 4. 合法条目如何生成

由 `CalculateValidSkillTargetEntries(skills, triggerEvent)` 在该敌人的回合开始时计算，受以下限制：

| 限制 | 来源 |
|---|---|
| 技能能否从当前站位释放 | 技能的 `launch_ranks` |
| 技能能打哪些站位 | 技能的 `target_ranks` |
| 目标是否可被选中 | 潜行、隐身、尸体等条件 |
| 冷却、条件是否满足 | 技能冷却与 `conditions` |

**不是**在这里生效的：

- **嘲讽（taunt）**：强制目标是在执行阶段重定向的，条目枚举阶段仍然列出全部合法目标；
- **守护（guard）**：同样在执行阶段把伤害转给守护者。

> 这一条曾导致求解器误判：前瞻里给坦克挂嘲讽，预演结果仍然瞄准原目标。现在由 `SimState.FindTaunterGuid` 手动重定向。

## 5. 与玩家操作完全独立

`ACTOR_CONTROLLER` 流不会被玩家的任何操作消耗。因此：

```
敌方下一步行动 = (ACTOR_CONTROLLER 的下一个值, 再下一个值)
```

只要在敌人行动前 peek 这两个值，就能**精确预测**它选择哪个技能、攻击哪个目标。这是求解器“还手预测”的基础。

## 6. 结算时序

```
敌方回合开始
  ├─ 枚举合法 (技能, 目标) 条目
  └─ 预计算全部条目的伤害/暴击（消耗 SKILL_CALCULATION 流）
敌方行动
  ├─ ACTOR_CONTROLLER 抽技能
  ├─ ACTOR_CONTROLLER 抽目标
  └─ 应用结果（嘲讽/守护重定向、抗性判定、DOT、token 等）
```

每一条预计算结果消耗：

- 1 次暴击判定；
- 1 次伤害区间抽取；
- 多段攻击共享同一组结果。

## 7. Boss 的差异

- 仍然使用 `ActorControllerRandom`，但技能池包含 `god_*`、`*_ult_*` 等特殊技能；
- 同一回合多次行动的 boss，会在 `QueryTurnOrder` 的剩余队列里出现同一个 guid 多次；
- 特殊技能可能召唤单位、切换相位，但这些**不改变**“选技能/选目标”的规则，只是结果更复杂。

## 8. 对求解器的三条直接结论

| 结论 | 用途 |
|---|---|
| 敌方选择只取决于两个随机值 | 可以做精确的还手预测 |
| 权重是 `m_RandomSelectChance` | 可以解释“为什么它总放大招” |
| 条目枚举不含嘲讽 | 前瞻必须手动把伤害重定向到嘲讽者 |

## 9. 已知风险

调用 `CalculateValidSkillTargetEntries(skills, false)` 会**清空**目标条目列表，导致 boss 无法选目标而卡住回合。所有调用必须：

- 传 `triggerEvent = true`；
- 并用 `Probe.SuppressValidSkillsEvent` 抑制事件，避免 UI 丢技能栏。

## 10. 尚未确认

- `m_RandomSelectChance` 是否会被状态（血量、相位、回合数）动态修改；
- 是否存在 `ActorControllerRandom` 之外的控制器（例如某些 boss 的脚本控制器）；
- `QueryTurnOrder` 的剩余队列是否严格等于“本回合剩余行动”。

## 验证方式

- 反编译：`analysis/il_ac/Assets.Code.Actor.ActorController.ActorControllerRandom.il.txt`
- 实机：`BepInEx/LogOutput.log` 中的 `PREDICT:` 行，以及 `plugins/foretest/` 下的 `S DECIDE` / `S ACTUAL` 记录。
