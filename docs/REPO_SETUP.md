# 建仓推送清单（凭据到位即执行）

前置：老板提供「有建仓权限的 PAT」或「自建两空仓地址」（私仓 + 公仓）。全程约 10 分钟。

1. 配置凭据：`git config --global credential.helper store` 后首次推送输入；或 `git remote set-url` 用 PAT 形式 URL。凭据只进 credential store，不写进仓库文件。
2. 建两空仓（若老板给 PAT 让自建）：`TaoDBM`（私仓，全量历史）+ `TaoDBM`（公仓，同历史或仅二期树，按老板口径）。
3. 挂远端：`git remote add origin <私仓地址>`；二期树分支 `main` 与 `phase2/a1-streaming` 均推送：`git push -u origin main phase2/a1-streaming`。
4. 启用流水线：把 `docs/ci/windows-build.yml` 移入 `.github/workflows/`（本仓库 Write 工具限制，先暂存于此），commit + push，确认 Actions 首跑全绿。
5. 核验：GitHub 网页看提交数与分支；Actions 跑 `windows-build`（Windows 真环境全量编译 + LogicTest）。
6. 公仓同步：若公仓独立，`git push <公仓地址> main` 并核对提交数一致。
7. 回报老板：私仓/公仓地址、Actions 首跑结论、DEVICE_CHECKLIST（见下）随包可测。

## 当前本地提交基线（推送前核对 `git log --oneline | head`）

- 1a3f1ac 六工程拆分 → 377265c MVVM 基建 → 2d78185 BOM 回归 → 0ea0cc1/9792934 仓库净化 → cf177a5 A1-1 → ccafa6c A1-2 → f1a1e6c A2-1 → d968254 A3-1 → 75c2f52 A3-2 →（本轮 TypeCheck 修复）
- 测试基线：LogicTest 849/849 全绿；TypeCheck（App 层沙箱类型闸）0 错 0 警。
