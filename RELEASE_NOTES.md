# v1.4.7

此版本仍使用 **.NET 11** 和 **Runtime Async** 编译，需要运行在 .NET 11 或更新运行时上的 ShiroBot 宿主。

## 上下文缓存与历史压缩

- 同一会话相邻请求的 system prompt、工具定义和历史原文保持逐字节一致，提高上游前缀缓存命中率：不再按请求裁剪最早的历史、不再截短较早的文件内容，工具按名称排序，工具轮数用完时改用 `tool_choice: none`，MCP 服务短暂掉线时保留上次的工具列表。
- 压缩后只保留最近约 16K tokens / 32 轮原文（按 `history.max_tokens` 推算），不再保留一半预算的原文。
- 历史超过预算 75% 时在回复发出后压缩，只有超过 100% 时才在回复前压缩，正常情况下用户不用等待压缩。
- 长历史分块压缩，每块完成后立即保存；压缩模型报上下文超长时自动缩小分块。压缩没能完成时按会话退避重试（1、5、15、60 分钟），期间直接用现有历史回复，对用户静默。
- 历史压缩也会重试并使用备用模型。

## 记忆本

- 历史压缩时自动把被压缩对话中的长期信息（称呼、身份、偏好、约定等）补记进记忆本，只新增和修改，不删除。
- 记忆本快照放进可缓存的前缀，只在新会话开始或压缩时刷新；之后的增改删附在当前消息末尾。
- `memory_note` 工具描述改为中文，并写明何时必须调用、何时主动调用。

## 群聊主动回复

- 判断模型决定接话后，正式回复直接开口，不再按活跃度再判断一次，修复活跃度选 medium 时也很少回话的问题。
- medium 去掉“十几条消息里接一两条”的限制，开放话题可以自然接话，频率由冷却控制。
- 判断时拿不准按活跃度倾向决定，判断输出上限提高到 512 tokens，减少推理模型被截断。
- 活跃度新增 `off`：不主动插话，只在被提到时判断。

## 生图

- 生图和编辑请求在超时、网络错误和临时 HTTP 错误时按 `retry_on_timeout_max_attempts` 重试。

## 配置精简

只需配置 `history.max_tokens`，压缩后保留的原文和分块大小都按它推算。以下配置项已移除，旧配置文件中保留它们不会报错，但会被忽略：

- `default.retry_on_timeout_delay_ms`、`history.max_turns`、`history.persist`
- `resources.cache_dir`、`resources.download_timeout_seconds`
- `behaviour.answer_fold_above_chars`、`behaviour.warn_on_no_vision`、`behaviour.hint_on_empty_input`
- `owner_prompt.enabled`、`tool_use.tavily.enabled`
- `proactive.include_bot_name`、`proactive.interject`、`proactive.judge_context_messages`、`proactive.quiet_min_seconds`、`proactive.quiet_max_seconds`

**升级时请检查：**

- `behaviour.fold_above_chars` 现在适用于所有回复模式和主动回复（新安装默认 400）。旧模板中它是 `0`，升级后所有模式都不再折叠；原来 auto/chat 模式的解答会在 400 字时折叠，如需保留请设为 `400`。
- `owner_prompt.template` 非空即生效；之前填写了模板但关闭了开关的，升级后会开始追加。
- `tool_use.tavily.api_key` 非空即启用搜索；之前填写了密钥但关闭了开关的，升级后会开始提供搜索工具。
- 原来设置 `proactive.interject = false` 的，请改为 `proactive.eagerness = "off"`。
- 对话历史始终保存到磁盘。原来 `history.persist = false` 实际上会让多轮对话没有任何历史。
- 模型不支持识图时始终剥离图片，并告诉模型已忽略图片。
