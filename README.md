# ShiroBot AIChat Plugin

ShiroBot 的 OpenAI-compatible AI 聊天插件。

## 功能

- 多 provider、多模型，支持从 `/models` 自动拉取模型列表
- `single` / `multi` / `shared` 三种对话模式，群聊共享上下文
- 图片、文件、引用消息和合并转发内容输入
- 对话历史持久化，长对话自动整理为长期记忆
- 工具调用：Tavily 搜索、网页读取、定时消息、记忆本
- 可选：Markdown / 流式回复、长回复折叠、模型自选聊天/解答回复模式

## 安装

将 Release 构建的插件 DLL 放入宿主的插件目录。需要宿主 `1.0.0` 及以上。

```sh
dotnet publish ShiroBot.AiChatPlugin.csproj -c Release
```

## 本地开发

```sh
sh scripts/dev.sh --prepare  # 构建插件并准备开发宿主，不启动
sh scripts/dev.sh            # 构建并启动开发宿主
```

## 许可证

[GPL-3.0-only](LICENSE)
