# ShiroBot AIChat Plugin

ShiroBot 的 OpenAI-compatible AI 聊天插件，支持多 provider、多模型、群聊共享上下文、图片/文件输入、上下文持久化和低内存图片发送。

## 功能

- OpenAI-compatible `chat/completions` 接口
- 多 provider：每个 provider 独立配置 `base_url` 和 `api_key`
- 多模型：支持 `provider-model` 形式展示和切换
- 自动从 provider 的 `/models` 接口拉取模型列表
- `single` / `multi` / `shared` 三种对话模式
- 支持图片、文件文本、引用消息、合并转发内容收集
- 图片以 base64 data URL 发送，并使用磁盘 `.b64` 缓存和流式 HTTP body 降低内存占用
- 群聊 shared 模式下按发言人标识传递给 AI，适合群聊上下文
- `#aihelp` / `#model` 群聊回复会在 30 秒后自动撤回

## 安装

将插件放入 ShiroBot 工作区，目录结构建议如下：

```text
ShiroBot/
  ShiroBot.SDK/
  ShiroBot.Model/
  ShiroBot.AiChatPlugin/
```

Debug 构建时插件会复制到宿主输出目录：

```powershell
dotnet build .\ShiroBot.AiChatPlugin\ShiroBot.AiChatPlugin.csproj -c Debug
```

Release 发布：

```powershell
dotnet publish .\ShiroBot.AiChatPlugin\ShiroBot.AiChatPlugin.csproj -c Release
```

## 配置

首次加载插件时会自动生成带注释的 `config.toml` 模板。最小配置示例：

Dashboard 配置表单支持中文分组、字段名称和说明；提示词使用多行输入，API 密钥默认隐藏，数值限制和对话模式、搜索深度选项也会显示在表单中。TOML 配置键保持原有英文名称。

```toml
[default]
model = "deepseek-chat"
fallback_model = "" # 文本请求失败时的备用模型，留空禁用
default_prompt = "你是一个乐于助人的助手。"
timeout_seconds = 120
max_concurrency = 4
default_chat_mode = "multi"

[history]
max_turns = 20
max_tokens = 16000
persist = true
compact_prompt = "请用简洁的中文总结以下对话的关键信息和结论，保留重要的事实、决定和上下文。"

[resources]
max_image_bytes = 8388608
max_inline_text_bytes = 524288
cache_dir = "data/resource_cache"
cache_max_age_days = 7
download_timeout_seconds = 60

[behaviour]
auto_reply_direct_messages = false
cancel_on_recall = false
use_markdown = false
use_streaming = false
fold_above_chars = 1000 # 回复超过 1000 字符时折叠；0 禁用
reply_with_quote = true
reply_with_mention = false
warn_on_no_vision = true
hint_on_empty_input = true

[permissions]
clear_admin_only = true
admin_only_models = []

[tool_use]
enabled = true
max_rounds = 3

[tool_use.tavily]
enabled = true
api_key = "" # 留空时不暴露搜索工具
max_results = 5
search_depth = "basic"

[[providers]]
name = "deepseek"
base_url = "https://api.deepseek.com/v1"
api_key = "sk-xxx"
fetch_models = true
model_filter = ["deepseek-chat", "deepseek-reasoner"]

[[models]]
name = "deepseek-chat"
provider = "deepseek"
supports_vision = false
display_name = "DeepSeek Chat"
```

### Provider

```toml
[[providers]]
name = "openai"
base_url = "https://api.openai.com/v1"
api_key = "sk-xxx"
fetch_models = false
```

- `name`：provider 名称，供 `[[models]]` 引用
- `base_url`：OpenAI-compatible API 地址
- `api_key`：API key
- `fetch_models`：是否启动时自动调用 `/models` 拉取模型
- `model_filter`：可选，按前缀过滤自动拉取的模型

### Model

```toml
[[models]]
name = "gpt-4o-mini"
provider = "openai"
supports_vision = true
display_name = "GPT-4o Mini"
```

模型列表显示为：

```text
openai-gpt-4o-mini (GPT-4o Mini) [vision]
```

切换模型时使用列表显示的完整名称：

```text
#model openai-gpt-4o-mini
```

如果只输入关键字但没有精确命中，插件会列出匹配的候选模型。

### 长回复、失败回退与 compact

- `[behaviour].fold_above_chars`：AI 正文超过阈值时，在 QQ 群聊和私聊中以合并转发显示完整内容。卡片预览正文前 120 个 Unicode 字符，底部显示“点击查看全部”。按 Unicode 字符计数，等于阈值不折叠，默认 `0` 禁用。折叠消息不附加引用；其他平台或合并转发发送失败时按普通文本回复。
- `[default].fallback_model`：使用已注册模型名或 `provider-model`。文本请求先完成原有超时、网络错误和空响应重试，仍失败（包括上游 HTTP 错误）时使用备用模型；备用模型也按相同规则重试，不再继续回退。不修改用户或共享会话的模型选择。当前请求含图片时不使用此回退。
- `[history].compact_prompt`：历史轮数达到现有压缩阈值时，作为摘要请求的 system prompt。支持 TOML 多行字符串；留空使用内置提示词，不受用户 `#prompt` 影响。

### Markdown 与流式回复

`[behaviour].reply_with_mention` 默认为 `false`，Dashboard 中显示为“@发送者回复”。开启后群聊普通文本、折叠卡片和官方 Markdown 回复都会提及原消息发送者，可以与引用回复同时开启；私聊及私聊流式回复不添加 @。

插件使用 NuGet `ShiroBot.SDK 0.9.7`（包含 QQ Model 合同），要求宿主支持 ShiroBot API `0.9.2` 和 QQ Model 包 `0.9.7`。

Markdown 和流式能力通过当前适配器的扩展接口探测，不按平台名称判断。Markdown 需要适配器实现 `IQOfficialMessageApi` 并通过 `CanSendMarkdown`；流式需要实现 `IQOfficialDirectMessageApi` 且当前场景可创建流。

- `[behaviour].use_markdown`：默认 `false`。开启后通过 `IQOfficialMessageApi.CanSendMarkdown` 探测当前目标，支持时发送 QQ 官方 Markdown；不支持或发送失败时回退普通文本。关闭时，在本次对话的完整 system prompt 末尾追加 `不要使用markdown`，包含个人和共享提示词。内置群聊提示词不再写死格式限制，旧默认提示词会自动迁移，自定义提示词保留。
- `[behaviour].use_streaming`：默认 `false`。开启后，在 QQ 官方私聊中探测 `IQOfficialDirectMessageApi`，使用上游 SSE 增量生成及官方流式发送；支持图片请求和工具调用。工具参数按索引拼接，不会作为正文发送。流式帧会合并快速到达的内容，结束时补齐完整回复。
- 两个开关可独立使用：都开启且能力可用时使用 Markdown 流式；仅开启流式时使用文本流式。当前 SDK 的流式接口仅支持 QQ 官方私聊，群聊及其他平台回退完整回复，Markdown 开关仍独立生效。
- 上游不支持 SSE 时会重建非流式请求，并继续使用现有重试和备用模型规则。官方流式开始、更新或结束失败时，完整答案通过普通文本发送。已发送的部分流会尽力结束，新的重试或备用模型回答不会接在旧前缀后。
- Markdown / 流式发送成功后不会额外发送普通回复或折叠卡片；普通回复仍按 `fold_above_chars` 和引用回复设置发送。

### 工具调用

`[tool_use]` 管理全局工具开关及工具调用轮数，`max_rounds` 限制在 `1–8`，达到上限后模型需要输出最终回答。每种工具使用独立的子配置，例如 `[tool_use.tavily]`。Tavily 需要同时启用并填写 API key，每次请求最多实际搜索一次。

仅支持 `[tool_use.tavily]`，旧 `[tavily]` 已移除。关闭全局工具开关、关闭 Tavily 或留空 API key 都不会向模型发送该工具及搜索提示词。当前消息含图片时继续走图片请求路径，不启用工具调用。

后续工具实现 `Tools/IChatTool.cs`，提供工具定义、提示词、执行方法和单次请求调用上限，在 `ChatToolRegistry.Create` 注册并添加独立配置即可复用统一的调用循环。

验证新增行为：

```sh
dotnet run --project Tests/Verification/Verification.csproj -c Release
```

## 命令

```text
#ai <内容>      与 AI 对话
@机器人 <内容>  群聊中通过 @ 触发
#aihelp         显示帮助

#aimode         查看当前对话模式
#aimode single  单次对话，不保留上下文
#aimode multi   多轮对话，按用户独立记录
#aimode shared  群聊共享，同群所有人共享上下文

#prompt         查看当前 prompt
#prompt <文本>  设置 prompt
#prompt -       重置 prompt

#model          查看可用模型
#model <name>   切换模型
#model -        恢复默认模型
#refresh        重新拉取 provider 模型列表

#clear          清空当前会话历史
```

## 对话模式

- `single`：单次无记录，每次请求只带当前消息
- `multi`：默认模式，按用户独立保存上下文
- `shared`：群聊共享上下文，同群内所有人共享同一个会话历史

在 `shared` 模式下：

- AI 会收到说话者标识，例如 `[群名片/123456] 今天吃什么？`
- `#prompt`、`#model`、`#clear` 影响共享会话，仅 owner/admin 可修改
- `#aimode` 是用户个人设置，所有人都可以切换自己的模式

## 数据与缓存

插件会在插件目录下写入：

```text
data/
  user_settings.json          用户偏好
  conversations/*.jsonl       对话历史
  conversations/*.meta.json   shared 模式共享 prompt/model
  resource_cache/*            图片/资源缓存
```

图片只保留 `.b64` 缓存文件，发送时从磁盘流式写入 HTTP 请求体，避免把完整 base64 图片和完整 JSON body 同时加载到内存。历史上下文不会重复附带上一轮图片内容。

`[behaviour].cancel_on_recall` 默认为 `false`，配置页显示“撤回时取消请求”。开启后，适配器上报原消息撤回事件时，若尚未开始发送回复，立即取消排队、资源处理、模型及工具请求，不再重试或切换备用模型，不发送错误提示，也不保存本轮历史。已经开始发送流式回复的请求继续完成。撤回按适配器实例、平台、会话和消息 ID 匹配。

`[behaviour].auto_reply_direct_messages` 默认为 `false`，配置页显示“私聊直接回复”。开启后私聊普通文本、图片和文件无需 `#ai` 即可触发聊天，沿用使用权限、模型和历史设置；原有管理命令优先处理，以 `#` 或 `/` 开头的其他命令不自动回复。

官方 Markdown 回复中，@发送者单独占一行，避免破坏正文首行的标题、列表和代码围栏。


HTTP 网页读取：`[tool_use.http_fetch]` 默认 `enabled = true`，支持 HTTP/HTTPS 网页和纯文本直链。配置页显示“HTTP 页面读取”。仅允许标准端口的公网地址，禁止 URL 内嵌凭据、本机/内网/链路本地/保留地址；每次跳转重新检查 URL，实际连接时解析 DNS 并直接连接核验后的公网 IP，拒绝混合公网和内网的解析结果。此工具关闭 Cookie 和代理，始终公网直连，不执行 JavaScript、命令、脚本或下载文件。地址范围参考 [IANA IPv4](https://www.iana.org/assignments/iana-ipv4-special-registry/) 与 [IANA IPv6](https://www.iana.org/assignments/iana-ipv6-special-registry/) 特殊用途注册表。

```toml
[tool_use.http_fetch]
enabled = true
max_calls_per_turn = 3
timeout_seconds = 20
max_bytes = 524288
max_text_chars = 12000
summarize_long_text = true
summary_input_chars = 40000

[tool_use.timer_tool]
enabled = true
max_pending_per_user = 20
max_days_ahead = 30
```

网页正文超过 `max_text_chars` 时，默认先通过独立的默认模型请求摘要，再将摘要作为工具结果返回原来的主模型。摘要请求不携带对话历史、自定义提示词和工具定义，原文标记为不可信资料；输入上限由 `summary_input_chars` 控制，摘要失败则返回截断正文。下载超限直接返回工具错误，返回正文或摘要均受字符上限约束。网页内容不能通过插件执行任何命令；模型仍应把正文及摘要当作外部资料而非用户指令。

定时消息：开启 `[tool_use.timer_tool].enabled` 和全局工具开关后，模型可以使用 `timer_tool(at, text)` 创建一次性预约。时间必须是带时区偏移的 ISO 8601 时间，且在未来和预约范围内。模型请求未完成、失败或因撤回取消时，不保存该次尝试的预约；最终回复完成生成后提交到 `data/timers.json`，重启后恢复。默认每个用户在同一会话最多保留 20 条任务。

到期后选中原适配器实例，使用默认模型根据预约内容生成简短回复，并在原群聊中 @ 请求者，不受普通回复 @ 开关影响；私聊直接推送。模型生成失败时推送原预约文本。发送失败每分钟重试，最多 3 次，失败任务保留供查看；关闭工具暂停已有任务，重新开启后处理到期任务。宿主需持续运行，平台需支持主动消息发送；重启恢复会补发过期的待推送任务。发送与磁盘更新之间发生异常退出时可能重复推送。

群聊 `#timer` 查看当前群的待推送消息、预约时间和失败状态；私聊也可查看自己的会话任务。沿用插件使用权限，列表按适配器实例和会话隔离。


安全抓取检测到域名全部解析为 `198.18.0.0/15`（代理常用 Fake-IP）时，会使用 [Cloudflare DoH](https://developers.cloudflare.com/1.1.1.1/encryption/dns-over-https/make-api-requests/dns-json/) 重新查询 A/AAAA。DoH 连接固定公网地址且保持 TLS 证书验证；查询结果仍经过公网地址检查，然后直接连接核验后的 IP。不会放行 Fake-IP、内网解析或不安全跳转。普通内网地址不会触发此回退。请求使用常见浏览器 UA 和 Accept，但不执行 JavaScript 或模拟浏览器指纹。

所有工具调用都会在 BotLog 中记录 `ToolUse` 日志：工具名、调用 ID、模型、轮次，以及开始、完成、失败、取消或拒绝状态。完成时显示耗时和结果字符数，不输出完整参数、正文或异常消息。网页长文本摘要另有开始/完成日志；定时任务保存及到期推送也会记录。

HTTP 抓取次数在“工具调用 → HTTP 页面读取 → 每次回复抓取次数上限”配置，对应 `tool_use.http_fetch.max_calls_per_turn`，默认 3，范围 1–10。工具调用分组顶部的 `tool_use.max_rounds` 是所有工具共用的轮数，默认 3；“先读取首页，再依据结果读取详情页”应将轮数设为 3，同时保留 HTTP 抓取次数为 3。两者共同限制调用，与 Tavily 的配置独立。

工具轮次会保留模型返回的 `reasoning_content` 并原样回传，支持普通 JSON 和 SSE 增量响应，以兼容 DeepSeek 思考模式的工具调用要求。该字段仅在本次工具请求链中使用，不发送给用户，不输出日志，也不保存到聊天历史。[DeepSeek 官方说明](https://api-docs.deepseek.com/zh-cn/guides/thinking_mode/)

HTTP 抓取结果同时返回 `links`（最多 60 条带标题的公网 HTTP/HTTPS 链接），保留详情页/下一页的真实地址，相对链接基于最终页面 URL 转为绝对地址；模型应优先沿这些链接继续读取，避免猜测路径。HTTP 请求日志显示不含查询参数与片段的目标路径，HTTP 失败结果及日志包含状态码。达到工具轮数上限且模型仍请求工具时，明确拒绝这些调用并补一次无工具的收尾请求，根据已有资料说明结果与缺失内容，不执行额外抓取。


网页结果还可返回 `images`，包含最多 30 个公网图片 URL 和 `alt` 替代文字，支持相对路径、`data-src`/`data-original` 懒加载与首个 `srcset` 候选，过滤脚本、嵌入数据和内网地址。`tool_use.http_fetch.extract_images` 默认 `true`，配置页显示“提取网页图片链接”。这些是图片元数据，不等于视觉识别；工具不会自动下载图片。

工具可以在同一次回复中交叉使用，例如 Tavily 搜索返回 URL 后继续 HTTP 抓取，或读取首页后继续详情页。仍受各工具的调用次数和全局 `max_rounds` 限制。

`tool_use.timer_tool.use_tools_on_delivery` 默认 `true`，配置页显示“定时推送时允许使用查询工具”。开启后到期请求也可以串联已启用的 Tavily、HTTP 抓取工具；只暴露标记为只读查询的工具，禁止再次创建定时任务。仍使用默认模型和全局工具轮数，不携带对话历史。


使用范围统一由宿主“路由”管理；插件不再配置 `permissions.allow_users` / `deny_users`。“操作权限”只保留管理员清空会话和模型切换限制。`permissions.clear_admin_only` 默认 `true`，群聊/私聊 `#clear` 都需宿主机器人所有者或管理员身份；关闭后可清空个人会话，共享会话仍仅管理员允许。

所有工具默认开启，全局 `tool_use.max_rounds = 3`。HTTP 和 Tavily 每次回复调用上限默认 3；timer_tool 也最多调用 3 次。Tavily API 密钥为空时不注册搜索工具。定时任务默认允许使用已启用的只读查询工具。

主人提示词默认关闭，只提供一个文本框。开启后将用户填写的文字原样追加到聊天和定时推送提示词，留空时不追加，不解释占位符，不影响管理权限，也不进入网页摘要请求。

```toml
[owner_prompt]
enabled = false
template = ""
```

`permissions.admin_only_models` 填写受限的模型名称，而不是管理员 ID；AIChat 的管理员检查统一使用 SDK `Context.IsAdmin(message.Sender.Id)`，不维护管理员名单。宿主 `owner_list` 中的账号自动具有 admin 权限，不必重复加入 `admin_list`。配置页显示“需要管理员权限的模型”。

## 许可证

本项目采用 GNU General Public License v3.0（SPDX：`GPL-3.0-only`），完整条款见 [LICENSE](LICENSE)。
