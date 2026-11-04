# ShiroBot AIChat Plugin

ShiroBot 的 OpenAI-compatible AI 聊天插件。

## 功能

- 多 provider、多模型，支持从 `/models` 自动拉取模型列表
- `single` / `multi` / `shared` 三种对话模式，群聊共享上下文
- 图片、文件、引用消息和合并转发内容输入
- 对话历史持久化，长对话自动压缩成历史摘要
- 工具调用：Tavily 搜索、网页读取、定时消息、记忆本、其他插件提供的工具（例如 MemePlugin 的表情包）、自定义 MCP 服务（有风险，默认关闭）

## 安装

将 Release 构建的插件 DLL 放入宿主的插件目录 或 通过插件市场安装。

## 对外契约

AIChat 已导出以下 `[PluginContract]`，其他插件可从最终的单文件 DLL 生成调用代码：

| 契约 | 服务名 | 用途 |
| --- | --- | --- |
| `ChatModels.Complete` | `aichat.model.complete` | 借用配置好的模型生成一次回复，支持图片输入 |
| `ChatModels.List` | `aichat.model.list` | 查询开放给插件的模型和默认模型 |
| `ChatTools.Tools` | `chat.tool.*` | 其他插件注册工具，由 AIChat 发现和调用 |
| `ChatTools.AfterReply` | `chat.after_reply.*` | 其他插件注册在文字回复后执行的动作 |

## 本地开发

```sh
sh scripts/dev.sh --prepare  # 构建插件并准备开发宿主，不启动
sh scripts/dev.sh            # 构建并启动开发宿主
```

## 发布验证

```sh
dotnet publish ShiroBot.AiChatPlugin.csproj -c Release
dotnet run --project Tests/ContractConsumer -c Release -- bin/Release/net10.0/publish/ShiroBot.AiChatPlugin.dll
```

## 许可证

[GPL-3.0-only](LICENSE)

## 历史图片

默认 `history.image_memory_turns = 0`，只发送本条和引用消息的图片。
`history.max_images_per_request = 4` 限制每次携带的图片数量，优先本条，再取最近历史图。
历史图片引用仍可用于明确选图或编辑；视觉内容统一放在请求尾部，淘汰图片时不改动前面的历史文字或 system prompt，以保留可复用的缓存前缀。
已有配置若设置了图片记忆轮数，需自行改为 `0` 才会停止自动携带历史图片。
