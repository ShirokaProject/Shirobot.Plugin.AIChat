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

## 许可证

[GPL-3.0-only](LICENSE)
