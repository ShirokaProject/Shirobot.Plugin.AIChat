# ShiroBot AIChat Plugin

ShiroBot 的 OpenAI-compatible AI 聊天插件。

## 功能

- 多 provider、多模型，支持从 `/models` 自动拉取模型列表
- `single` / `multi` / `shared` 三种对话模式，群聊共享上下文
- 图片、文件、引用消息和合并转发内容输入
- 对话历史持久化，长对话自动压缩成历史摘要
- 工具调用：Tavily 搜索、网页读取、定时消息、记忆本、表情包（需安装 MemePlugin）、自定义 MCP 服务（有风险，默认关闭）
- 可选：Markdown / 流式回复、长回复折叠、回答 / 聊天 / 自动三种回复模式、指定群聊主动插话

## 安装

将 Release 构建的插件 DLL 放入宿主的插件目录。需要宿主 `1.0.0` 及以上。

```sh
dotnet publish ShiroBot.AiChatPlugin.csproj -c Release
```

发布目录只包含 `ShiroBot.AiChatPlugin.dll`，不再携带 `ShiroBot.Meme.Contracts.dll`。
表情功能是可选的：安装 [Shirobot.Plugin.Meme](../Shirobot.Plugin.Meme) 后，宿主会把
Meme 的共享契约加载到默认 AssemblyLoadContext；AIChat 检测到该契约和已注册的
`IMemeService` 时自动启用 `send_meme`，未安装或未启用时自动跳过。

Meme 契约只随 MemePlugin 安装，不需要复制到 `plugins/AiChatPlugin/`。

## 本地开发

```sh
sh scripts/dev.sh --prepare  # 构建插件并准备开发宿主，不启动
sh scripts/dev.sh            # 构建并启动开发宿主
```

## 许可证

[GPL-3.0-only](LICENSE)
