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

```toml
[default]
model = "deepseek-chat"
default_prompt = "你是一个乐于助人的助手。"
timeout_seconds = 120
max_concurrency = 4
default_chat_mode = "multi"

[history]
max_turns = 20
persist = true

[resources]
max_image_bytes = 8388608
max_inline_text_bytes = 524288
cache_dir = "data/resource_cache"
download_timeout_seconds = 60

[behaviour]
reply_with_quote = true
warn_on_no_vision = true
hint_on_empty_input = true

[permissions]
allow_users = []
deny_users = []
admin_only_models = []

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

切换模型时支持两种写法：

```text
#model gpt-4o-mini
#model openai-gpt-4o-mini
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

图片会生成 `.b64` 缓存文件，发送时从磁盘流式写入 HTTP 请求体，避免把完整 base64 图片和完整 JSON body 同时加载到内存。
