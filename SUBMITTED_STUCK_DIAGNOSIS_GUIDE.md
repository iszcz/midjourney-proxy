# SUBMITTED任务卡住问题诊断指南

## 问题现象

- 任务提交后卡在SUBMITTED状态
- 长时间没有响应（超过1-2分钟）
- 后续任务全部阻塞，无法执行
- 删除卡住的任务后，后续任务才能正常运行

## 已实施的诊断增强

### 1. 任务等待循环诊断日志

**位置**: `DiscordInstance.cs` ExecuteTaskAsync方法

**功能**: 每30秒输出一次任务等待状态

**日志示例**:
```
[WARN] 任务等待中 [账号Display], TaskId: 1762251508984083, Status: SUBMITTED, Nonce: 198565286..., MessageId: null, Elapsed: 30s
[WARN] 任务等待中 [账号Display], TaskId: 1762251508984083, Status: SUBMITTED, Nonce: 198565286..., MessageId: null, Elapsed: 60s
[ERROR] 任务执行超时 [账号Display], TaskId: 1762251508984083, Timeout: 5min, Status: SUBMITTED, Nonce: 198565286..., MessageId: null, InteractionMetadataId: null
```

**解读**:
- ✅ 如果 `MessageId` 一直为null → 说明没有收到Discord的消息
- ✅ 如果 `Elapsed` 持续增长 → 说明任务确实卡住了
- ✅ 如果看到"任务不在运行列表中" → 说明任务被意外移除

### 2. 消息匹配过程日志

**位置**: `BotMessageHandler.cs` 和 `UserMessageHandler.cs` 的 FindAndFinishImageTask方法

**功能**: 记录每次消息匹配的详细过程

**日志示例**:
```
# 开始匹配
[INFO] BOT 开始匹配任务, MessageId: 1234567890, Action: IMAGINE, FinalPrompt: A surreal landscape..., Hash: 9e50665b-edcf...

# 找到任务（通过MessageId）
[INFO] BOT 通过MessageId找到任务 (优先级1), TaskId: 1762251508984083, MessageId: 1234567890

# 成功匹配
[INFO] BOT 成功匹配任务, TaskId: 1762251508984083, MessageId: 1234567890, Status: IN_PROGRESS, Action: IMAGINE

# 未找到任务（警告）
[WARN] BOT 未找到匹配的任务, MessageId: 1234567890, Action: IMAGINE, FinalPrompt: A surreal landscape...
[WARN] 当前运行中的任务数: 2, 任务列表: 1762251508984083(SUBMITTED,Nonce:198565286), 1762251509876543(SUBMITTED,Nonce:198565287)
```

**解读**:
- ✅ 如果看到"开始匹配"但没有"成功匹配" → 说明消息匹配失败
- ✅ 查看"当前运行中的任务数" → 对比Nonce是否匹配
- ✅ 如果Discord的MessageId在列表中但未匹配 → 说明匹配逻辑有问题

### 3. Nonce匹配日志

**位置**: `BotMessageListener.cs` OnMessage方法

**功能**: 记录通过Nonce查找任务的过程

**日志示例**:
```
# 收到包含Nonce的消息
[DEBUG] 用户消息包含Nonce, INTERACTION_SUCCESS, id: 1234567890, nonce: 1985652863594876928

# 成功找到任务
[INFO] 通过Nonce找到任务, TaskId: 1762251508984083, Status: SUBMITTED, Nonce: 1985652863594876928, MessageType: INTERACTION_SUCCESS, MessageId: 1234567890

# 未找到任务（警告）
[WARN] 未通过Nonce找到任务, Nonce: 1985652863594876928, MessageType: INTERACTION_SUCCESS, MessageId: 1234567890, AccountId: 1435204384...
[WARN] 当前运行中的任务数: 2, 任务列表: 1762251508984083(Nonce:19856528), 1762251509876543(Nonce:19856529)
```

**解读**:
- ✅ 如果收到Nonce但未找到任务 → Nonce不匹配或任务已被移除
- ✅ 对比Nonce值 → 检查是否一致
- ✅ 检查MessageType → 确认消息类型是否正确

## 诊断步骤

### 步骤1: 实时监控日志

当任务卡住时，立即查看日志：

```bash
# 实时查看日志
tail -f logs/log20250805.txt | grep -E "SUBMITTED|任务等待中|未找到匹配"

# 查找特定任务的所有日志
grep "1762251508984083" logs/log20250805.txt
```

### 步骤2: 分析任务卡住的原因

根据日志判断：

#### 情况A: 没有收到Discord消息

**日志特征**:
```
[WARN] 任务等待中, TaskId: xxx, Status: SUBMITTED, MessageId: null, Elapsed: 30s
[WARN] 任务等待中, TaskId: xxx, Status: SUBMITTED, MessageId: null, Elapsed: 60s
```

**原因**: Discord没有返回消息或消息丢失

**解决方案**:
1. 检查Discord连接状态
2. 检查网络连接
3. 检查Discord账号是否正常
4. 检查是否触发了CF验证

#### 情况B: 收到消息但匹配失败

**日志特征**:
```
[INFO] BOT 开始匹配任务, MessageId: xxx, Action: IMAGINE, FinalPrompt: ...
[WARN] BOT 未找到匹配的任务, MessageId: xxx, Action: IMAGINE
[WARN] 当前运行中的任务数: 1, 任务列表: 1762251508984083(SUBMITTED,Nonce:198565286)
```

**原因**: 
1. Nonce不匹配
2. Prompt格式化后不匹配
3. 任务Action类型不匹配
4. BotType不匹配

**解决方案**:
1. 检查Nonce值是否一致
2. 检查Prompt格式化逻辑
3. 确认Action类型正确
4. 确认BotType正确

#### 情况C: Nonce匹配失败

**日志特征**:
```
[DEBUG] 用户消息包含Nonce, INTERACTION_SUCCESS, id: xxx, nonce: 198565286...
[WARN] 未通过Nonce找到任务, Nonce: 198565286..., MessageType: INTERACTION_SUCCESS
[WARN] 当前运行中的任务数: 1, 任务列表: 1762251508984083(Nonce:198565287)
```

**原因**: Nonce值不匹配（对比日志中的Nonce值）

**解决方案**:
1. 检查Nonce生成逻辑
2. 检查Nonce在传输过程中是否被修改
3. 检查是否有多个实例使用相同的Nonce

#### 情况D: 任务被意外移除

**日志特征**:
```
[WARN] 任务等待中, TaskId: xxx, Status: SUBMITTED, Elapsed: 30s
[ERROR] 任务不在运行列表中, TaskId: xxx, 这可能导致任务永远无法完成
```

**原因**: 任务从运行列表(_runningTasks)中被意外移除

**解决方案**:
1. 检查是否有其他地方调用了RemoveRunningTask
2. 检查是否有异常导致任务被清理
3. 检查并发控制逻辑

### 步骤3: 查询数据库

```sql
-- 查找所有卡住的任务
SELECT 
    id,
    status,
    action,
    prompt,
    nonce,
    message_id,
    interaction_metadata_id,
    submit_time,
    start_time,
    TIMESTAMPDIFF(MINUTE, FROM_UNIXTIME(submit_time/1000), NOW()) as stuck_minutes,
    properties
FROM tasks
WHERE status = 'SUBMITTED'
    AND submit_time > UNIX_TIMESTAMP(DATE_SUB(NOW(), INTERVAL 1 HOUR)) * 1000
ORDER BY submit_time DESC;

-- 查找超过5分钟还在SUBMITTED的任务
SELECT 
    id,
    status,
    action,
    LEFT(prompt, 100) as prompt_preview,
    nonce,
    TIMESTAMPDIFF(MINUTE, FROM_UNIXTIME(submit_time/1000), NOW()) as stuck_minutes
FROM tasks
WHERE status = 'SUBMITTED'
    AND TIMESTAMPDIFF(MINUTE, FROM_UNIXTIME(submit_time/1000), NOW()) > 5
ORDER BY submit_time DESC;
```

## 常见原因和解决方案总结

### 原因1: 任务匹配逻辑问题（已修复）

**问题**: 
- 部分匹配（`EndsWith`/`StartsWith`）导致错误匹配
- 模糊匹配无唯一性检查

**修复**: 
- ✅ 移除部分匹配，只保留精确匹配
- ✅ 模糊匹配增加唯一性检查
- ✅ 缩短时间窗口到2分钟

### 原因2: Discord消息未正确接收

**可能原因**:
- WebSocket连接断开
- 网络不稳定
- Discord服务异常
- CF验证触发

**诊断方法**:
- 查看日志中是否有"BOT Received"消息
- 检查Discord连接状态
- 查看是否有CF验证日志

**解决方案**:
- 重启Discord连接
- 检查网络配置
- 处理CF验证

### 原因3: Nonce不匹配

**可能原因**:
- Nonce生成时机不对
- Nonce被意外修改
- 多个实例使用相同Nonce

**诊断方法**:
- 对比日志中的Nonce值
- 检查Nonce设置代码

**解决方案**:
- 确保Nonce在任务创建时就设置
- 确保Nonce唯一性
- 确保Nonce正确传递给Discord

### 原因4: InteractionMetadataId未设置

**可能原因**:
- `INTERACTION_SUCCESS` 消息未处理
- 消息处理顺序问题

**诊断方法**:
- 查看日志中是否有INTERACTION_SUCCESS消息
- 检查InteractionMetadataId是否被设置

**解决方案**:
- 确保INTERACTION_SUCCESS消息被正确处理
- 确保InteractionMetadataId在正确的时机设置

## 临时应急方案

如果问题频繁发生，可以设置一个定时任务自动清理卡住的任务：

```sql
-- 手动清理超过10分钟还在SUBMITTED的任务
UPDATE tasks 
SET 
    status = 'FAILURE',
    fail_reason = '任务超时: 提交后10分钟无响应',
    finish_time = UNIX_TIMESTAMP() * 1000,
    progress = '0%'
WHERE 
    status = 'SUBMITTED'
    AND TIMESTAMPDIFF(MINUTE, FROM_UNIXTIME(submit_time/1000), NOW()) > 10;
```

## 监控建议

### 1. 实时监控

设置告警：
- 任务超过2分钟还在SUBMITTED状态 → 警告
- 任务超过5分钟还在SUBMITTED状态 → 严重告警
- 连续3个任务卡住 → 紧急告警

### 2. 日志分析

定期分析日志：
```bash
# 统计每天卡住的任务数
grep "执行超时" logs/log*.txt | wc -l

# 查找未匹配的任务
grep "未找到匹配的任务" logs/log*.txt | tail -20

# 查找Nonce匹配失败的情况
grep "未通过Nonce找到任务" logs/log*.txt | tail -20
```

### 3. 性能监控

监控关键指标：
- SUBMITTED任务的平均等待时间
- 消息匹配成功率
- Nonce匹配成功率
- 超时任务数量

## 使用新日志诊断问题的流程

### 场景：发现任务卡住

1. **查看任务ID**: `1762251508984083`

2. **搜索该任务的所有日志**:
```bash
grep "1762251508984083" logs/log20250805.txt
```

3. **分析日志输出**:

```
# 任务提交
10:18:28 [INFO] Task submitted, TaskId: 1762251508984083, Nonce: 1985652863594876928

# 任务开始等待（每30秒一次）
10:18:58 [WARN] 任务等待中, TaskId: 1762251508984083, Status: SUBMITTED, Nonce: 198565286..., MessageId: null, Elapsed: 30s
10:19:28 [WARN] 任务等待中, TaskId: 1762251508984083, Status: SUBMITTED, Nonce: 198565286..., MessageId: null, Elapsed: 60s

# Discord返回消息
10:19:11 [INFO] BOT Received, Type: Default, id: 9876543210, Content: **A surreal landscape...**

# 尝试匹配任务
10:19:11 [INFO] BOT 开始匹配任务, MessageId: 9876543210, Action: IMAGINE, FinalPrompt: A surreal landscape...

# 匹配失败！
10:19:11 [WARN] BOT 未找到匹配的任务, MessageId: 9876543210, Action: IMAGINE
10:19:11 [WARN] 当前运行中的任务数: 1, 任务列表: 1762251508984083(SUBMITTED,Nonce:198565286)
```

4. **问题诊断**:

从上面的日志可以看出：
- ✅ 任务已提交，Nonce: 1985652863594876928
- ✅ Discord返回了消息，MessageId: 9876543210
- ❌ 但是消息匹配失败了！
- ❌ Nonce值对比：任务的Nonce是198565286...，但这只是前10位

5. **深入检查**:

```bash
# 查看是否有Nonce匹配的日志
grep "1985652863594876928" logs/log20250805.txt

# 查看是否有INTERACTION_SUCCESS消息
grep "INTERACTION_SUCCESS" logs/log20250805.txt | grep "1762251508984083"
```

## 常见解决方案

### 解决方案1: 优先级1和2匹配失败

**问题**: MessageId 和 InteractionMetadataId 都没有正确设置

**原因**:
- `INTERACTION_SUCCESS` 消息没有被处理
- `CREATE` 消息中没有设置MessageId

**解决**:
- 检查消息类型判断逻辑
- 确保在正确的时机设置这些ID

### 解决方案2: Prompt匹配失败

**问题**: 所有基于Prompt的匹配都失败

**原因**:
- Discord返回的finalPrompt与提交的PromptEn不一致
- 格式化逻辑导致差异
- 提交时Prompt为空

**解决**:
- 确保提交时设置了Prompt
- 检查格式化逻辑
- 使用更可靠的匹配方式（MessageId优先）

### 解决方案3: 提高MessageId设置的可靠性

**建议修改** (如果上述日志仍然发现问题):

在 `BotMessageListener.cs` 的 OnMessage 方法中，确保CREATE消息一定会设置MessageId：

```csharp
// 只有 CREATE 才会设置消息 id
if (messageType == MessageType.CREATE)
{
    task.MessageId = id;
    
    _logger.Information("设置任务MessageId, TaskId: {TaskId}, MessageId: {MessageId}, Nonce: {Nonce}", 
        task.Id, id, task.Nonce);
    
    // 设置 prompt 完整词
    if (!string.IsNullOrWhiteSpace(contentStr) && contentStr.Contains("(Waiting to start)"))
    {
        if (string.IsNullOrWhiteSpace(task.PromptFull))
        {
            task.PromptFull = ConvertUtils.GetFullPrompt(contentStr);
            
            _logger.Information("设置任务PromptFull, TaskId: {TaskId}, PromptFull: {PromptFull}", 
                task.Id, task.PromptFull?.Substring(0, Math.Min(100, task.PromptFull.Length)));
        }
    }
}
```

## 预防措施

1. **确保Nonce正确设置**:
   - 任务创建时立即设置Nonce
   - 同时设置到task.Nonce和Properties中
   - 记录日志确认Nonce值

2. **确保MessageId正确关联**:
   - 在INTERACTION_SUCCESS消息中设置InteractionMetadataId
   - 在CREATE消息中设置MessageId
   - 两个ID都应该被正确设置

3. **优化超时设置**:
   - 适当增加超时时间（如从5分钟增加到10分钟）
   - 但不要设置太长，避免长时间阻塞

4. **定期清理**:
   - 设置定时任务清理超时的SUBMITTED任务
   - 避免任务永久阻塞

## 总结

通过新增的详细日志，现在可以：

1. ✅ **实时追踪任务等待状态**（每30秒）
2. ✅ **详细记录消息匹配过程**（开始、找到、成功、失败）
3. ✅ **记录Nonce匹配情况**（成功或失败）
4. ✅ **输出运行中的任务列表**（帮助对比和诊断）
5. ✅ **记录超时详细信息**（Status、Nonce、MessageId等）

这些日志将帮助您快速定位任务卡住的根本原因，从而采取针对性的解决措施。

