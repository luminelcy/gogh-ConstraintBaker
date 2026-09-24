# ConstraintBaker

修 gogh（Focus with Your Avatar）**家具堆叠过深导致帧数崩塌**的常驻 mod。

把房间里堆叠深链（每层一个 active `ParentConstraint`）中**源静止**的约束烘焙掉
（`enabled = false`，位姿由 Transform 层级携带），只保留**马达源**（旋转君等会自己转的
物品）的约束。静止深链的约束求值开销 ≈ O(N²)/帧，砍掉后实测 95ms → 5~6ms
（StackPerfProbe 的 F8 是本 mod 的原型，原理与实测数据见该仓库的《项目文档》）。

## 为什么能关

约束算的是纯函数：`node.local = F(source 相对 node.parent 的位姿)`。

- 挂点（source）和约束节点的父级（`RoomItemsParent`）是**同一物品根下的刚性子节点**；
- 拖动、编辑旋转、被上层马达带着整塔转——都是刚体运动，**不改这个相对位姿**，烘焙不失效；
- 唯一会改它的是"物品自己内部的动画转自己的挂点"= 马达 → 这类约束保留不关。

## 判据（全部朝"保留"的保守方向失效）

| 条件 | 动作 |
|---|---|
| 节点名不是 `*_Constraint` | 不碰 |
| `sourceCount != 1` / 找不到 source / 找不到物品根 | 不碰 |
| source 所属物品"自己的部分"有 `Animator` 或 `RoomItemAnimatorRotationBehaviour` | **保留**（马达源） |
| 其余 | **烘焙**（关掉） |

"自己的部分"不包含 `RoomItemsParent` 以下的堆叠子物品——子物品是负载，不是挂点运动的原因。
所以"马达 + 上百层静止塔"只留马达正上方那一个约束，塔身靠层级跟转。

## 运行行为

- 进房间场景立即烘焙一轮，之后每 **3s** 重扫（补烘新放置的，也清掉销毁掉的）；
- 每 **5s** 看门狗核对已烘焙节点的源相对位姿快照：漂移 = 判据漏掉的运动源
  → 自动恢复该约束并**永久保留**（不来回振荡），日志 `挂点在动，恢复约束: …`；
- **F8 = 总开关**：关 = 恢复全部被烘焙的约束，开 = 立即重新烘焙；
- 换场景自动清状态重来。

日志（`MelonLoader/Latest.log`）：

```
[ConstraintBaker] 已加载：自动烘焙静态源 ParentConstraint，F8 = 总开关
[ConstraintBaker] 烘焙 121 / 保留 2
[ConstraintBaker] 挂点在动，恢复约束: Floor_00_Constraint   ← 只在看门狗触发时出现
```

## 部署

构建产物 `ConstraintBaker/bin/Release/net6.0/ConstraintBaker.dll` 放进
`<游戏目录>/Mods/`，重启游戏生效。与 StackPerfProbe（诊断工具）可同开。

```bash
dotnet build ConstraintBaker/ConstraintBaker.csproj -c Release
```

引用的是 `MelonLoader/Il2CppAssemblies/` 下的 **interop 版** UnityEngine。

## 抗更新

零游戏程序集依赖（只用引擎 API + 类名字符串匹配马达）。游戏更新后失效方向都是
"多保留约束"（丢收益不错位）；唯一的错位路径（无 Animator 的运动源被误烘）由看门狗兜底。
更新后自查：日志看 `烘焙 N / 保留 M` 比例是否突变 + 旋转君负载是否照常跟转。
