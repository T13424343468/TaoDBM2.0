# TaoDBM 二期逻辑测试报告（2026-11-15 16:30 刷新）

- 源码树：/root/workspace/TaoDBM-phase2，HEAD 719b1b9（A6 热点盐化 + 在线改表扇出引擎）
- 运行时：.NET SDK 10.0.404（Linux 沙箱）
- 构建闸：dotnet build -warnaserror → 0 错误 0 警告
- 类型闸：TypeCheck（App 层沙箱编译）→ 0 错误 0 警告

## 全量结果

```
单元: 185 / 185
集成: 713 / 713
系统: 66 / 66
暴力: 50 / 50
万链: 6 / 6

通过 1020 / 1020
```

## 范围与限制

- 覆盖：22 个引擎/模块单测 + 集成/系统/暴力/万链五层回归。
- 本轮新增：HotspotSalting 热点盐化（盐化方案/FNV 写路由/读扇出，2–1024 槽边界校验）、OnlineDdl 在线改表扇出（pt-osc 式步骤：建影子表→变更→主键分段回填→追平→单语句原子换名→A5-2 对账后清理），合计 23 项断言。
- Windows 真机/云测：受限未跑；XAML 编译与运行行为待真机验证（兜底口径：云测延后补验）。
- 提交链：全部本地；远端推送待公仓添加可写 deploy key（见 Docs/REPO_SETUP.md 与 Scripts/PushPhase2.sh）。
