# v1.4.12

此版本使用 **.NET 11** 和 **Runtime Async** 编译，需要运行在 .NET 11 或更新运行时上的 ShiroBot 宿主。

## QQ 戳一戳回复

- 开启后，别人在 QQ 群聊或私聊戳机器人时，AI 会在原会话回一句。群友互戳和机器人发出的戳一戳会被忽略。适配器需上报戳一戳事件。
- 默认关闭。可在 Dashboard 的「回复行为 → 被戳一戳时回复」开启，或在 `config.toml` 的 `[behaviour]` 段设置 `reply_to_nudge = true`。设置支持热重载。

## 项目结构

- 将 `AiChatPlugin.*.cs` 按职责移入子目录，根目录保留插件入口 `AiChatPlugin.cs`。
