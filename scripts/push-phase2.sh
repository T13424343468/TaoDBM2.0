#!/usr/bin/env bash
# TaoDBM 二期推送脚本（老板解锁凭据后一条命令执行，配套 docs/建仓推送清单.md）
# 用法1（SSH 钥匙有 write 权限）: scripts/push-phase2.sh ssh <SSH别名> [远端分支，默认 phase2]
# 用法2（PAT）: GITHUB_PAT=ghp_xxx scripts/push-phase2.sh pat <GitHub用户名> [远端分支]
# 说明：只推当前 HEAD 到 phase2 分支，不触碰公仓既有 main；dry-run 验证可用 git push --dry-run 先行。
set -euo pipefail

REPO="T13424343468/TaoDBM"
SSH_BIN="/root/workspace/.local/sshbin/ssh"
SSH_CFG="/root/workspace/.ssh/config"
SSH_KNOWN="/root/workspace/.ssh/known_hosts"
BRANCH="${3:-phase2}"

case "${1:-}" in
  ssh)
    ALIAS="${2:?缺少 SSH 别名，如 github-taoplay}"
    GIT_SSH_COMMAND="$SSH_BIN -F $SSH_CFG -o UserKnownHostsFile=$SSH_KNOWN -o StrictHostKeyChecking=yes" \
      git push "git@$ALIAS:$REPO.git" "HEAD:refs/heads/$BRANCH"
    ;;
  pat)
    : "${GITHUB_PAT:?缺少 GITHUB_PAT 环境变量}"
    USER_NAME="${2:?缺少 GitHub 用户名}"
    git push "https://$USER_NAME:$GITHUB_PAT@github.com/$REPO.git" "HEAD:refs/heads/$BRANCH"
    ;;
  *)
    echo "用法: $0 ssh <SSH别名> [远端分支] | GITHUB_PAT=xx $0 pat <用户名> [远端分支]" >&2
    exit 2
    ;;
esac

echo "已推送: $(git log --oneline -1) -> $REPO@$BRANCH"
