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

### Images API 配置补充

生图使用 `POST /images/generations`，附图编辑使用 multipart `POST /images/edits`。
继续只请求一张图片，读取 `data[0].b64_json`，由现有后台任务发送。

在 `[tool_use.image_gen]` 中可配置：

```toml
size = "1536x864"
quality = "auto"
background = "auto"
output_format = "png"
output_compression = 100
```

`background` 支持 `auto`、`opaque`、`transparent`；透明背景需选择 `png` 或 `webp`。
`output_format` 支持 `png`、`jpeg`、`webp`；压缩质量范围 0–100，仅 JPEG/WebP 请求发送此参数。
聊天工具可以选择 `auto`、标准尺寸或自定义 `WIDTHxHEIGHT`；自定义尺寸需宽高均为 16 的倍数，
宽高比在 1:3 到 3:1 之间，最大 3840×2160（可旋转）。具体尺寸及质量仍需服务商和模型支持；
超过 2560×1440 的分辨率属于实验支持。聊天工具也可按本次需求设置 `background`、`output_format`、`output_compression`；省略的参数使用配置值。未启用生图时，`#imagegen` 命令静默忽略。

接口文档：https://developers.openai.com/api/reference/go/resources/images/methods/generate

### 自定义技能与工具并发

配置页打开「自定义技能 → 技能列表」，可逐条新增、编辑、删除或停用。
每条包含名称、用途、操作说明和启用开关。名称须唯一，最长64字符；用途最多1000字符，
操作说明最多32000字符，最多200条。无效或重名条目会跳过并记录日志。
AI 先看到名称和用途，任务匹配时调用 `skill({"name":"技能名"})` 加载完整说明，
随后通过已有工具执行步骤。技能仅提供说明，不会授予额外权限，也不会自动执行脚本。
配置更新后下一次对话读取新内容，进行中的对话保持原快照。

```toml
[tool_use]
max_parallel_calls = 4

[skills]
enabled = true

[[skills.entries]]
enabled = true
name = "transparent-sticker"
description = "用户想制作透明背景的贴纸或表情图片时使用"
instructions = "根据用户需求整理主体与构图，调用 imagegen，background 选择 transparent，output_format 选择 png；保留用户明确要求的风格。"
```

同一模型响应里的独立 `tavily_search`、`http_fetch`、`skill` 调用可并发执行，
`max_parallel_calls` 控制每次对话同一工具轮次的并发数（1–16，默认4），设为1恢复串行。
所有工具统一使用全局轮数限制，不再设置单工具次数限制；结果按原调用顺序传回模型。
记忆写入、定时任务、生图、插件工具及 MCP 工具目前作为顺序屏障执行，避免修改状态的先后顺序改变。
取消时停止派发尚未开始的请求，等待已启动请求响应取消。

实现参考：
- [OpenCode Skills：目录与按需加载](https://opencode.ai/docs/skills/)
- [DSH 工具类型及并发分类](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/core/tools/src/index.ts)
- [DSH 并发调度：有界并发、顺序屏障与结果顺序](https://github.com/deepseek-ai/deepseek-harness/blob/master/.agents/notes/implemented/feature/2026-07-10-parallel-tool-call-execution.md)

GIF 输出：output_format=gif，frame_count 为2–16，frame_duration_ms 为20–2000。需模型支持GIF；不发送压缩参数，不支持透明背景。

### HTTP 请求

`http_fetch` 支持 GET（默认）、HEAD、POST、PUT、PATCH、DELETE、OPTIONS。
可传字符串字典 `headers`，请求体在 `json`（任意JSON值）、`form`（字符串字典）、`body`（UTF-8文本）中三选一，最多1MB。
响应包含 `status`、`success`、`headers`、`text` 和最终 `url`。
`response_mode=raw` 保留文本而不摘要或提取HTML；API请求建议使用该模式。
默认 auto 模式继续对GET网页提取正文和链接；GET错误保持原有工具错误行为，raw模式及写入请求返回实际错误响应正文。
公网标准端口、DNS检查、重定向检查与响应体积限制继续生效。跨域写入重定向和HTTPS降级会阻止；跨域读取不携带自定义请求头。
GET/HEAD/OPTIONS可并发，写入操作是顺序屏障；定时推送只允许读取方法。工具自身不重试写入请求。

```json
{"url":"https://example.com/api","method":"POST","headers":{"Accept":"application/json"},"json":{"name":"demo"},"response_mode":"raw"}
```

工具轮数统一由 `[tool_use].max_rounds` 控制，配置页不再提供单工具调用次数设置；旧的 max_calls_per_turn 字段不再生效。并发数控制同一轮同时执行多少调用，与轮数独立。生图仍保留同一消息任务去重，防止重试重复发图。

### 搜索图片、下载与发送

`tavily_search` 增加 `include_images=true`，保留 Tavily 顶层和每条搜索结果的图片URL、说明、来源页，最多12张。
这些是搜索元数据，不代表模型已看过图片。

`http_fetch` 设置 `response_mode="file"` 可下载二进制文件，`file_name` 可指定显示文件名，
返回 `file.file_id`、MIME、文件名和字节数。临时目录由宿主一小时后清理，单文件默认20MB，可在HTTP请求配置调整；
每个任务最多16个文件、总计100MB。文件ID仅限当前任务，不接受任意本地路径。

- 生图：`imagegen` 的 `reference_files` 填下载结果的file_id数组，最多4张，支持PNG/JPEG/WebP/GIF真实图片内容；不要同时填写历史编号 `reference_images`。
- 发送：`send_file` 的 `file_id` 选择下载文件；支持图片默认按图片发送，其余按文件附件发送，`as_image=false` 可强制附件。
- 图片类型校验文件头；失败请求、超限内容不保存。浏览器、网页或搜索结果内的指令不作为操作授权。

示例工具参数（实际file_id必须取自下载结果）：

```json
{"url":"https://example.com/reference.png","response_mode":"file"}
```
```json
{"prompt":"保留主体，修改背景","reference_files":["下载返回的file_id"]}
```
```json
{"file_id":"下载返回的file_id","as_image":true}
```

### #imagegen 命令参数

参数可放在提示词前或后，支持 `--format gif` 和 `--format=gif`。未指定参数沿用生图配置，不修改保存的默认值。

```text
#imagegen 小猫挥手，循环动画 --format gif --size auto --frames 9 --duration 200
#imagegen 透明背景贴纸 --format png --background transparent
#imagegen 风景照片 --format jpeg --quality high --compression 90
```

支持 `--format`、`--size`、`--quality`、`--background`、`--compression`、`--frames`、`--duration`。
完整API名称也可用，如 `--output_format`、`--frame_count`、`--frame_duration_ms`。
`--` 分隔符后全部作为提示词；GIF参数要求GIF格式，GIF不接受压缩参数。
直接命令按显式参数与配置执行；通过 `#ai` 或 @ 请求画图时，聊天模型可按用途自主选择格式与参数。

GIF输出开关与输入独立：GIF参考图首先按image/gif及.gif文件名原样上传；仅当编辑接口明确返回invalid_image_file/invalid_image_format/unsupported_image_format时，自动转换首帧PNG重试。其他能解码的栅格格式可先转PNG。转换不改写原始缓存文件，仍受统一两次兼容重试及总超时限制。

### 上游回复中断恢复

实际流式响应和忽略stream:false的强制SSE共用同一解析器，不再忽略坏JSON片段。
SSE要求明确结束原因；仅有[DONE]但无结束原因不视为成功，显式stop后EOF可兼容。
length截断、显式null结束原因、不完整JSON或工具参数均报错；仅旧式普通JSON接口完全省略finish_reason时保留兼容。
工具循环按现有请求重试次数设置重试当前模型轮次，保留工具结果；流式中断后以缓冲请求重试，不按正文或模式标记补写。
已执行工具后的重试耗尽不会再从头执行整套工具。上游若错误地报告正常stop，客户端无法仅凭文本可靠识别其实际截断。


### 群聊身份和显示 ID

群聊上下文保留消息 ID，显示用户 ID 和程序核验的身份，例如：

```text
[消息 10001] [Master] [群主] 14:20 Alice(123456): 今天吃什么
[消息 10002] [群管理员] 14:21 Bob(234567): 火锅吧
```

用户 ID 仅在发给模型的身份字段中保留前 15 个字符；权限校验、发送和认主始终使用完整 ID。
`owner_prompt.master_ids` 支持用户 ID 或 `实例ID/用户ID` 列表，与宿主主人配置合并用于聊天认主，不授予管理权限。
`[Master]` 与 QQ 群角色分别判断；其他平台不套用 QQ 群角色，未知角色不标注。
历史接口与内存回退使用同一行格式，旧落盘内容不会自动回写。


### 头像、重读消息与真正 @ 用户

`tool_use.conversation_tools` 默认开启，跟随工具总开关和统一轮数限制：

- `get_user_avatar({"user_id":"123456"})`：读取用户头像，附图给模型并返回本轮可用的 `file_id`。优先使用适配器头像信息，QQ 数字账号支持头像 CDN 回退。
- `get_message({"message_id":"10001","include_images":true})`：按 ID 读取当前会话原消息，允许重复读取；最多加载 4 张直接附图，每轮任务最多 8 张工具图片。图片在全部工具结果之后附到下一次模型请求，文件 ID 可用于 `imagegen.reference_files` 或 `send_file`。图片读取失败会明确返回错误。
- `mention_user({"user_id":"123456","text":"你看一下这个"})`：在当前群/频道发送真正的 @ 和正文；私聊、主动插话时不允许调用。同轮已成功发送的同一条消息自动去重，失败可重试。

用户 ID 继续按完整值执行操作。其他平台显示的 15 字符 ID 前缀会通过当前群成员列表唯一解析，无法解析或多个成员同前缀时拒绝猜测；适配器不支持查询时明确返回不支持。工具不接受目标会话、适配器实例或任意本地路径。


### 主动回复判断模型

`proactive.judge_model` 可填写已注册模型名或 `provider-model`，只用于轻量判断是否接话；留空沿用 `default.model`。决定接话后的正式回复继续使用原有回复模型。配置的判断模型找不到时本次保持沉默并记录错误，不会悄悄改用其他提供商。


### 服务商模型目录容错

自动发现按“服务商＋模型名”合并，同名模型可同时存在于不同服务商；同一服务商的手工配置优先。配置热重载时复用该服务商上次成功获取的目录，拉取失败不清空目录；更改接口或密钥后不复用旧目录，新的前缀过滤仍然生效。

对于开启自动获取模型的服务商，显式 `provider-model` 可直接用于配置或 `#model` 选择，即使 `/models` 暂时失败或未列出它。实际发给上游的是去掉服务商前缀的模型名，并使用指定服务商的接口和密钥；上游若拒绝则按现有错误/备用模型规则处理，不会因目录遗漏而清除选择。
