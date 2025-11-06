# 任务问题修复总结

## 修复的问题

### 1. 任务混淆问题（已修复）✅

**问题**: 不同用户提交的任务，结果会互相混淆
- 用户A提交"风景"任务，收到"书籍"的图片
- 用户B提交"书籍"任务，收到"风景"的图片

**根本原因**: 
- 优先级4和5使用了危险的部分匹配（`EndsWith`/`StartsWith`）
- 优先级7的模糊匹配无唯一性检查

**修复内容**:
1. ✅ 移除优先级4和5的部分匹配，只保留精确匹配
2. ✅ 优化优先级7的模糊匹配，增加唯一性检查
3. ✅ 缩短时间窗口从5分钟到2分钟
4. ✅ 多候选时拒绝匹配，记录错误日志

**修改的文件**:
- `src/Midjourney.Infrastructure/Handle/BotMessageHandler.cs`
- `src/Midjourney.Infrastructure/Handle/UserMessageHandler.cs`

**文档**:
- `TASK_MIX_FIX.md` - 完整修复说明
- `TASK_MIX_CASE_ANALYSIS.md` - 案例分析

### 2. 任务卡在SUBMITTED状态问题（已修复+诊断增强）✅

**问题**: 任务提交后卡在SUBMITTED状态，长时间无响应，阻塞后续任务

**根本原因**: 
- **WebSocket断开后等待循环未检测**（最严重！）
- Discord消息监听器没有正确处理返回的消息
- 消息匹配失败导致任务状态无法更新
- 缺少诊断机制，无法追踪问题

**修复内容**:
1. ✅ **在等待循环中添加IsAlive检查**（关键修复！）
2. ✅ WebSocket断开时任务立即失败（从5分钟缩短到0.5秒）
3. ✅ 在任务等待循环中添加诊断日志（每30秒）
4. ✅ 在消息匹配过程中添加详细日志
5. ✅ 在Nonce匹配时添加详细日志
6. ✅ 在未找到任务时输出运行中任务列表
7. ✅ 在超时时记录完整的诊断信息（包括WebSocket状态）

**修复效果**:
- 从等待5分钟 → 0.5秒检测到，**快了600倍**
- WebSocket断开不再阻塞后续任务
- 明确的错误信息："WebSocket连接已断开"
- 详细的WebSocket状态日志

**修改的文件**:
- `src/Midjourney.Infrastructure/Services/DiscordInstance.cs`
- `src/Midjourney.Infrastructure/Handle/BotMessageHandler.cs`
- `src/Midjourney.Infrastructure/Handle/UserMessageHandler.cs`
- `src/Midjourney.Infrastructure/BotMessageListener.cs`

**文档**:
- `SUBMITTED_TASK_STUCK_FIX.md` - 问题分析和修复方案
- `SUBMITTED_STUCK_DIAGNOSIS_GUIDE.md` - 诊断指南
- `WEBSOCKET_DISCONNECT_FIX.md` - WebSocket断开问题详细说明

### 3. 视频任务BatchSize参数支持（已添加）✅

**新增功能**: 支持视频任务的 `--bs` 参数

**修改内容**:
1. ✅ 在 `SubmitVideoDTO` 中添加 `BatchSize` 属性
2. ✅ 在 Video 接口中处理 `--bs` 参数

**修改的文件**:
- `src/Midjourney.Infrastructure/Dto/SubmitVideoDTO.cs`
- `src/Midjourney.API/Controllers/SubmitController.cs`

**使用示例**:
```json
{
  "prompt": "A beautiful landscape",
  "motion": "medium",
  "batchSize": 2
}
```

生成的最终prompt:
```
A beautiful landscape --video 1 --motion medium --bs 2
```

## 新增的诊断日志

### 1. 任务等待日志

**触发时机**: 任务处于SUBMITTED或IN_PROGRESS状态，每30秒输出一次

**日志格式**:
```
[WARN] 任务等待中 [账号Display], TaskId: xxx, Status: SUBMITTED, Nonce: xxx, MessageId: xxx, Elapsed: 30s
```

**关键信息**:
- `Status`: 当前任务状态
- `Nonce`: 任务的随机标识
- `MessageId`: Discord消息ID（如果有）
- `Elapsed`: 已等待时间（秒）

### 2. 消息匹配日志

**触发时机**: 收到Discord消息，开始匹配任务

**日志格式**:
```
# 开始匹配
[INFO] BOT 开始匹配任务, MessageId: xxx, Action: IMAGINE, FinalPrompt: A surreal landscape..., Hash: 9e50665b...

# 找到任务
[INFO] BOT 通过MessageId找到任务 (优先级1), TaskId: xxx, MessageId: xxx

# 成功匹配
[INFO] BOT 成功匹配任务, TaskId: xxx, MessageId: xxx, Status: IN_PROGRESS, Action: IMAGINE

# 未找到任务
[WARN] BOT 未找到匹配的任务, MessageId: xxx, Action: IMAGINE, FinalPrompt: ...
[WARN] 当前运行中的任务数: 2, 任务列表: xxx(SUBMITTED,Nonce:xxx), yyy(SUBMITTED,Nonce:yyy)
```

### 3. Nonce匹配日志

**触发时机**: 收到包含Nonce的消息

**日志格式**:
```
# 收到Nonce
[DEBUG] 用户消息包含Nonce, INTERACTION_SUCCESS, id: xxx, nonce: 198565286...

# 成功找到
[INFO] 通过Nonce找到任务, TaskId: xxx, Status: SUBMITTED, Nonce: xxx, MessageType: INTERACTION_SUCCESS, MessageId: xxx

# 未找到
[WARN] 未通过Nonce找到任务, Nonce: xxx, MessageType: INTERACTION_SUCCESS, MessageId: xxx, AccountId: xxx
[WARN] 当前运行中的任务数: 1, 任务列表: xxx(Nonce:xxx)
```

### 4. 超时日志

**触发时机**: 任务执行超时

**日志格式**:
```
[ERROR] 任务执行超时 [账号Display], TaskId: xxx, Timeout: 5min, Status: SUBMITTED, Nonce: xxx, MessageId: xxx, InteractionMetadataId: xxx
```

**关键信息**:
- 完整的任务状态信息
- 所有关键的匹配标识
- 超时时长

## 如何使用日志诊断问题

### 快速诊断命令

```bash
# 实时监控任务状态
tail -f logs/log$(date +%Y%m%d).txt | grep -E "任务等待中|未找到匹配|执行超时"

# 查找特定任务的完整日志
grep "任务ID" logs/log*.txt | less

# 统计今天未找到匹配的次数
grep "未找到匹配的任务" logs/log$(date +%Y%m%d).txt | wc -l

# 查找所有超时的任务
grep "执行超时" logs/log$(date +%Y%m%d).txt
```

### 诊断流程

1. **发现任务卡住** → 记录任务ID
2. **搜索任务日志** → `grep "任务ID" logs/*.txt`
3. **查看等待日志** → 确认任务状态和等待时间
4. **查看消息日志** → 确认Discord是否返回消息
5. **查看匹配日志** → 确认消息是否被正确匹配
6. **分析Nonce** → 对比Nonce值是否一致
7. **定位问题** → 根据日志确定根本原因

## 监控告警建议

### 告警级别

1. **警告** (1-2分钟):
   - 任务超过1分钟还在SUBMITTED
   - 消息匹配失败次数超过阈值

2. **严重** (2-5分钟):
   - 任务超过2分钟还在SUBMITTED
   - 连续3个任务匹配失败

3. **紧急** (超过5分钟):
   - 任务执行超时
   - 所有任务都被阻塞

### 监控指标

- SUBMITTED任务的平均等待时间
- 消息匹配成功率
- Nonce匹配成功率
- 任务超时数量
- 任务完成率

## 下一步建议

如果通过日志诊断发现问题仍然频繁发生，建议：

1. **增强MessageId设置**: 确保在所有可能的位置设置MessageId
2. **优化Nonce生成**: 确保Nonce的唯一性和一致性
3. **添加自动恢复**: 当检测到任务卡住时自动重试
4. **增加健康检查**: 定时检查并清理卡住的任务
5. **优化队列管理**: 改进队列的并发控制和超时处理

## 相关文档

- `TASK_MIX_FIX.md` - 任务混淆修复说明
- `TASK_MIX_CASE_ANALYSIS.md` - 案例深度分析
- `SUBMITTED_TASK_STUCK_FIX.md` - SUBMITTED卡住问题分析
- `SUBMITTED_STUCK_DIAGNOSIS_GUIDE.md` - 诊断使用指南
- `FIXES_SUMMARY.md` - 本文档（修复总结）

