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

发布目录 `bin/Release/net10.0/publish/` 中唯一需要安装的文件是 `ShiroBot.AiChatPlugin.dll`，可忽略调试用的 `.pdb`。
`ChatModels` 和 `ChatTools` 契约已嵌在同一个插件 DLL 内，无需额外的契约 DLL；不要安装 Debug 或中间输出。

## 对外契约

AIChat 已导出以下 `[PluginContract]`，其他插件可从最终的单文件 DLL 生成调用代码：

| 契约 | 服务名 | 用途 |
| --- | --- | --- |
| `ChatModels.Complete` | `aichat.model.complete` | 借用配置好的模型生成一次回复，支持图片输入 |
| `ChatModels.List` | `aichat.model.list` | 查询开放给插件的模型和默认模型 |
| `ChatTools.Tools` | `chat.tool.*` | 其他插件注册工具，由 AIChat 发现和调用 |
| `ChatTools.AfterReply` | `chat.after_reply.*` | 其他插件注册在文字回复后执行的动作 |

下面分别给出接入示例。契约通过宿主进程内插件服务交换 JSON，不是公开的 HTTP 接口，也不返回模型服务的 API 密钥。
模型调用受 `[plugin_api]` 的开关和模型白名单控制；调用方是宿主已加载的受信任插件，不使用 Dashboard 的 Bearer 密钥。

## 插件工具

其他插件可以向 AIChat 提供工具，AIChat 每轮把当前所有工具交给模型。AIChat 不知道也不关心是哪个插件提供的：
工具插件装上就能用，卸载或更新后 AIChat 自动跟着变化，双方都不需要声明依赖。机制见宿主文档
[插件之间互相调用](https://docs.shiroka.org/plugin/interop)。需要宿主 1.0.3。

`[tool_use.plugin_tools]` 可关闭全部插件工具、允许主动插话时使用，或用 `disabled` 去掉某个工具。

### 写一个工具

AIChat 定义了这个扩展点，所以由它提供契约：把 `ShiroBot.AiChatPlugin.dll` 放进工具插件的 `contracts` 目录，
构建时会生成 `ShiroBot.AiChatPlugin.Contracts` 里的 `ChatTools` 等类型，成品里不包含 AIChat 的 DLL。

```csharp
using System.ComponentModel;
using ShiroBot.AiChatPlugin.Contracts;
using ShiroBot.SDK.Plugin;

// 工具自己的参数，[Description] 会成为模型看到的参数说明
[PluginJson]
public sealed record WeatherArgs([property: Description("城市名，例如 上海")] string City);

[BotPlugin("weather", Name = "天气")]
public sealed class WeatherPlugin : PluginBase
{
    protected override Task LoadAsync()
    {
        ChatTools.Tools.Provide(Context, "query_weather",
            async (call, ct) =>
            {
                var args = call.Arguments.As<WeatherArgs>()!;
                return new ChatToolResult(await QueryAsync(args.City, ct));
            },
            new ChatToolInfo("查询城市当前天气。用户问天气时使用。", PluginJsonSchema.For<WeatherArgs>()));
        return Task.CompletedTask;
    }
}
```

参数格式由 `WeatherArgs` 生成，不需要手写 JSON Schema。

### 契约里的类型

| 类型 | 说明 |
| --- | --- |
| `ChatToolInfo(Description, Parameters, Prompt, MaxCallsPerTurn)` | 工具说明（交给模型，AIChat 会加上 `[提供方插件 ID]`）、参数格式、追加到系统提示的使用说明、每轮最多调用次数（1–5，默认 1） |
| `ChatToolCall(Arguments, Context)` | `Arguments` 是模型给的参数，用 `As<T>()` 读取；`Context` 是触发回复的会话：平台、实例、会话类型和 ID、消息 ID、发送者、是否群聊、是否主动插话 |
| `ChatToolResult(Content, AfterReply)` | `Content` 交给模型；`AfterReply` 见下文 |

工具名只能用字母、数字、下划线和连字符，最长 64 个字符；和内置工具、MCP 工具或其他插件工具重名时跳过。
没有合适结果时，在 `Content` 里让模型改用文字回复，不要抛异常；提供方不在或出错时，AIChat 也会让模型改用文字。

工具说明和使用说明每轮原样交给模型，并排在系统提示中稳定的部分，不会破坏上游的前缀缓存。不要把冷却、剩余次数这类
会变的状态写进说明，放进调用结果。

### 回复后的动作

发图片、文件这类跟在回复后面的内容，不要在工具里直接发：那时模型还没写出回复，顺序会乱，模型也可能最后不回复。
提供一个回复后动作，在工具结果里指向它，AIChat 发出文字后再调用：

```csharp
ChatTools.AfterReply.Provide(Context, "send_meme", async (call, ct) =>
{
    var args = call.Arguments.As<SendMemeArgs>()!;
    // call.Reply.TextSent 为 false 时没有发出文字，由这个动作代替回复，并按 Mention / Quote @、引用
    var sent = await SendAsync(args.Id, call.Context, call.Reply, ct);
    return new ChatAfterReplyResult(sent, History: "[表情]");
}, new ChatAfterReplyInfo("发送选中的表情"));

// 工具里只选图，然后要求回复后发送
return new ChatToolResult("已选好表情，回复一句短评即可。",
    new ChatAfterReply("send_meme", PluginJson.ToNode(new SendMemeArgs(selection.Id))!.AsObject()));
```

| 字段 | 说明 |
| --- | --- |
| `Reply.TextSent` | AIChat 是否已经发出文字回复 |
| `Reply.Mention` / `Reply.Quote` | 没有发出文字时，这个动作是否应该 @ 发送者、引用触发消息（按 AIChat 的设置） |
| 返回的 `History` | 没有发出文字时，代替空回复写入对话历史，例如 `[表情：猫猫]` |

MemePlugin 的发表情就是这样实现的。

## 对其他插件开放模型

其他插件可以借用这里配置的模型，不需要自己配置接口地址和密钥，例如 MemePlugin 的 AI 标注。同样通过契约调用：
把 `ShiroBot.AiChatPlugin.dll` 放进对方插件的 `contracts` 目录。

```csharp
using ShiroBot.AiChatPlugin.Contracts;

var reply = await ChatModels.Complete.TryCallAsync(Context, new ChatModelRequest(
    [new ChatModelMessage("user", "这张图里有什么？", [new ChatModelImage("image/png", Convert.ToBase64String(png))])],
    NeedsVision: true), ct);
// reply 为 null：AIChat 没有加载；reply.Text 是模型的回复，reply.Model 是实际使用的模型
```

| 契约 | 说明 |
| --- | --- |
| `ChatModels.Complete` | 调用一次模型。`Model` 不填时用开放给插件的默认模型；`NeedsVision` 为 true 时改用支持识图的模型 |
| `ChatModels.List` | 列出开放给其他插件的模型和默认模型 |

请求和聊天共用并发上限、超时重试和备用模型。`[plugin_api]` 可以关闭这项能力（`enabled`）、指定其他插件的默认模型
（`default_model`）和限定可用的模型（`models`）。

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

验证项目只把最终 DLL 作为 `PluginContract` 构建输入，不引用 AIChat 项目或程序集，检查模型/工具/回复后动作契约可提取、
消息可往返、生成的类型仅存在于调用方内部，以及发布目录只有一个插件 DLL。CI 发布前执行同样的检查。

## 许可证

[GPL-3.0-only](LICENSE)

## 图片生成

配置 `[tool_use.image_gen]`，复用 `[[providers]]` 的 OpenAI 兼容地址和密钥：

```toml
[tool_use.image_gen]
enabled = true
model = "openai-gpt-image-2.5-sunburst" # 已注册模型或 provider-model
size = "1024x1024"
quality = "auto"
timeout_seconds = 600
```

用户可发送 `#imagegen 提示词`，也可聊天要求画图，让 AI 调用 `imagegen` 工具。
工具受全局 `tool_use.enabled` 控制；直接命令只需生图开关开启。主动插话和定时生成不提供此工具。
等待时优先给原消息添加 🎨 表情回应，QQ 旧接口尝试平台表情；不支持或失败时发送“生成图片中...”。结束后撤下等待表情。
每次生成一张，并发串行执行；使用独立长超时，不自动重试生图请求，同轮聊天重试也复用结果，避免重复生成。
撤回取消开关开启时，尚未开始最终回复的请求可随原消息撤回取消；卸载时取消全部待处理请求。
调用 `POST /images/generations`，要求返回 `data[0].b64_json`（GPT Image 默认格式）；不会把 Base64 或接口密钥交给聊天模型。
协议参考：[OpenAI Images API](https://developers.openai.com/api/reference/resources/images/methods/generate)。

AI 生图工具可传可选 `size`（auto、1024x1024、1536x1024、1024x1536）和 `quality`（auto、low、medium、high、xhigh、max），根据用户描述选择横竖比例与质量；省略时沿用配置，单次选择不会修改全局配置。`#imagegen` 直接命令使用配置默认值。

等待文字在成功、失败或取消后尝试撤回（需要适配器支持删除自己的消息）；撤回失败只记诊断，不影响图片发送。HTTP 401/403 会明确提示鉴权或分组生图权限问题。支持附图编辑：有附图时将原图提交到 `/images/edits`。


附图并 @AI 要求修改，或附图发送 `#imagegen 修改要求`，会自动使用 OpenAI 兼容的 multipart `/images/edits`；无附图使用 `/images/generations`。
支持当前消息及引用消息中的 PNG、JPEG、WebP，最多 4 张，不会取更早聊天历史的图片。原图读取失败时明确提示，不静默退成文生图。
引用内容会标注原作者昵称、用户 ID 和消息 ID，并标明引用结束后恢复当前用户发言，避免把别人的原话当成发起人的话。
