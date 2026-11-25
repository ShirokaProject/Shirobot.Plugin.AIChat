# ShiroBot AIChat Plugin

ShiroBot 的 OpenAI-compatible AI 聊天插件。

## 功能

- **模型**：多服务商、多模型，自动拉取模型列表，主模型失败或被上游审核拒绝时切换备用模型等
- **对话**：`single` / `multi` / `shared` 三种模式，群聊共享上下文，主动插话，历史持久化与长对话自动压缩等
- **消息理解**：图片、表情包、文件、引用、合并转发、小程序与分享卡片等
- **工具**：Tavily 搜索、网页读取与 HTTP 请求、文件下载与发送、生成文本文件、生图与改图、定时提醒、记忆本、查看头像与重读消息、@ 群成员等
- **扩展**：自定义技能（Dashboard 管理）、其他插件提供的工具、MCP 服务（有风险，默认关闭）等
- **其他**：前缀缓存优化、用量统计、代理、工具并发执行等

各项配置的说明见插件首次加载时生成的 `config.toml` 注释，每个版本的变化见 [Releases](../../releases)。

## 安装

在 ShiroBot 的插件市场安装，或从 [Releases](../../releases) 下载 `ShiroBot.AiChatPlugin.dll` 放入宿主的插件目录。

## 对外契约

AIChat 已导出以下 `[PluginContract]`，其他插件可从最终的单文件 DLL 生成调用代码：

| 契约 | 服务名 | 用途 |
| --- | --- | --- |
| `ChatModels.Complete` | `aichat.model.complete` | 借用配置好的模型生成一次回复，支持图片输入 |
| `ChatModels.List` | `aichat.model.list` | 查询开放给插件的模型和默认模型 |
| `ChatTools.Tools` | `chat.tool.*` | 其他插件注册工具，由 AIChat 发现和调用 |
| `ChatTools.AfterReply` | `chat.after_reply.*` | 其他插件注册在文字回复后执行的动作 |

## 本地开发

使用 `global.json` 指定的 .NET 11 SDK。`Directory.Build.props` 显式开启 `UseRuntimeAsync` 和编译器 `runtime-async=on`；发布检查会验证最终 DLL 的目标框架和 Runtime Async 方法标记。开发启动脚本及 Rider 启动配置允许 framework-dependent 宿主使用已安装的更新主版本运行时。

```sh
sh scripts/dev.sh --prepare  # 构建插件并准备开发宿主，不启动
sh scripts/dev.sh            # 构建并启动开发宿主
```

## 发布验证

```sh
dotnet publish ShiroBot.AiChatPlugin.csproj -c Release
dotnet run --project Tests/ContractConsumer -c Release -- bin/Release/net11.0/publish/ShiroBot.AiChatPlugin.dll
```

## 许可证

[GPL-3.0-only](LICENSE)
