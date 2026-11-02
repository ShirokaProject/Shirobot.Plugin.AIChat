# ShiroBot AIChat Plugin

ShiroBot 的 OpenAI-compatible AI 聊天插件。

## 功能

- 多 provider、多模型，支持从 `/models` 自动拉取模型列表
- `single` / `multi` / `shared` 三种对话模式，群聊共享上下文
- 图片、文件、引用消息和合并转发内容输入
- 对话历史持久化，长对话自动压缩成历史摘要
- 工具调用：Tavily 搜索、网页读取、定时消息、记忆本、其他插件提供的工具（例如 MemePlugin 的表情包）、自定义 MCP 服务（有风险，默认关闭）
- 可选：Markdown / 流式回复、长回复折叠、回答 / 聊天 / 自动三种回复模式、指定群聊主动插话

## 安装

将 Release 构建的插件 DLL 放入宿主的插件目录。需要宿主 `1.0.3` 及以上。

```sh
dotnet publish ShiroBot.AiChatPlugin.csproj -c Release
```

发布目录只包含 `ShiroBot.AiChatPlugin.dll`。

## 插件工具

其他插件可以按名称提供 `chat.tool.<工具名>` 服务，AIChat 每轮对话列出当前可用的工具交给模型；
AIChat 不引用提供方的任何类型，提供方可以随时卸载、更新，不影响 AIChat。`[tool_use.plugin_tools]`
可关闭全部插件工具、允许主动插话时使用，或用 `disabled` 去掉某个工具。

提供方注册时附带描述：

| 字段 | 说明 |
| --- | --- |
| `description` | 必填，交给模型的工具说明 |
| `parameters` | 参数的 JSON Schema，缺省为无参数 |
| `prompt` | 追加到系统提示的使用说明 |
| `max_calls_per_turn` | 每轮最多调用次数，1–5，默认 1 |

调用时收到 `{ "arguments": 模型给的参数, "context": 会话信息 }`，`context` 含 `platform`、`instance_id`、
`channel_type`、`channel_id`、`message_id`、`sender_id`、`is_group`、`proactive`。返回
`{ "content": 交给模型的结果 }`；需要在文字回复之后再做的事（例如发图）放在
`"after_reply": { "service": 服务名, "arguments": … }`。AIChat 发出文字后调用该服务，额外传
`reply: { text_sent, mention, quote }`：模型没写文字时 `text_sent` 为 false，由这个动作按 AIChat 的设置
@/引用触发消息；它返回的 `history` 文本会写入对话历史。

## 本地开发

```sh
sh scripts/dev.sh --prepare  # 构建插件并准备开发宿主，不启动
sh scripts/dev.sh            # 构建并启动开发宿主
```

## 许可证

[GPL-3.0-only](LICENSE)
