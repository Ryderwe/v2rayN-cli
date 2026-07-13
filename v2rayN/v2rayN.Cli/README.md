# v2rayN-cli

`v2rayN-cli` 是基于 v2rayN `ServiceLib` 的 Linux/macOS 无图形界面客户端。它直接复用 v2rayN 的节点解析、订阅更新、SQLite 数据库、核心配置生成和核心进程管理能力，因此支持 v2rayN 已支持的分享链接格式和订阅内容。

## 全屏终端界面

在支持 ANSI 的交互式终端中运行：

```bash
./v2rayN-cli ui
```

界面包含节点、订阅、配置和日志四个页面。常用快捷键：

- `Tab`、`←`、`→` 或数字 `1-4`：切换页面
- `↑`、`↓`、`j`、`k`：移动选择
- `Enter`：选择节点、更新订阅或编辑配置
- `/`：搜索当前页面
- `a`：添加节点或订阅
- `d`：删除当前节点或订阅
- `Space`：选择节点或切换订阅启用状态
- `s`：启动或停止代理
- `Shift+R`：使用当前节点重启代理
- `l`：在简体中文和英文之间切换，并保存语言设置
- `q` 或 `Esc`：退出界面

每个页面顶部都会显示当前可用操作，例如“添加/导入节点”“添加/导入订阅”“更新”“启用/禁用”等，不需要记忆快捷键。

## 构建发布包

需要 .NET 10 SDK、`curl`、`unzip` 和 `tar`：

```bash
./package-cli.sh linux-x64
./package-cli.sh linux-arm64
./package-cli.sh osx-x64
./package-cli.sh osx-arm64
```

产物位于 `release-cli/`。发布脚本默认下载对应平台的 v2rayN 核心包并放入 `bin/`。只构建 CLI 时可执行：

```bash
WITH_CORES=0 ./package-cli.sh osx-arm64
```

## 快速开始

导入单节点并后台运行：

```bash
./v2rayN-cli nodes add 'vless://...'
./v2rayN-cli nodes list
./v2rayN-cli start
./v2rayN-cli status
```

添加并更新订阅：

```bash
./v2rayN-cli subs add 'https://example.com/subscription' --name my-sub --update
./v2rayN-cli nodes list --sub my-sub
./v2rayN-cli nodes select <节点ID前缀>
./v2rayN-cli restart
```

前台运行适合容器、launchd、systemd、supervisord 等进程管理器：

```bash
./v2rayN-cli run
```

默认使用一个 mixed 入口，同时接受 SOCKS5 和 HTTP 代理：`127.0.0.1:10808`。

修改监听配置：

```bash
./v2rayN-cli config show
./v2rayN-cli config set inbound.mixed-port 10880
./v2rayN-cli config set inbound.allow-lan true
./v2rayN-cli restart
```

## 主要命令

```text
ui
nodes list|add|show|select|remove|export
subs list|add|update|enable|disable|remove
config show|get|set|export
cores list
run|start|stop|restart|status
logs --lines 100
logs --follow
paths
version
```

节点和订阅均可使用完整 ID、唯一 ID 前缀或完整名称选择。`nodes add` 支持直接传入一个或多个分享链接、`--file` 文件，以及标准输入。

## 数据目录

默认数据目录与 v2rayN 桌面版一致：

- Linux: `$XDG_DATA_HOME/v2rayN`，未设置时通常为 `~/.local/share/v2rayN`
- macOS: `~/Library/Application Support/v2rayN`

可为服务器实例指定独立目录：

```bash
./v2rayN-cli --data-dir /var/lib/v2rayn-cli status
```

也可使用 `--portable` 将配置放在程序目录。`--data-dir` 应在每次调用时保持一致，或设置环境变量 `V2RAYN_DATA_DIR`。

## 服务管理示例

systemd 的 `ExecStart` 推荐使用前台 `run`：

```ini
[Unit]
Description=v2rayN CLI
After=network-online.target

[Service]
Type=simple
User=v2rayn
ExecStart=/opt/v2rayN-cli/v2rayN-cli --data-dir /var/lib/v2rayn-cli run
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

macOS 可在 launchd 的 `ProgramArguments` 中使用相同的 `--data-dir ... run` 形式。

启用 TUN 通常需要 root/CAP_NET_ADMIN 权限，并依赖所选核心和系统网络配置；普通 SOCKS5/HTTP 代理不需要管理员权限。
