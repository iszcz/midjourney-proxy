# SUBMITTED状态任务卡住问题诊断和修复

## 问题描述

任务提交后卡在SUBMITTED状态，长时间没有响应，导致后续任务全部阻塞。只有手动删除卡住的任务后，后续任务才能正常运行。

## 根本原因

在 `DiscordInstance.cs` 的 `ExecuteTaskAsync` 方法中，任务提交后会进入等待循环：

```csharp
while (info.Status == TaskStatus.SUBMITTED || info.Status == TaskStatus.IN_PROGRESS)
{
    // 每500ms检查一次任务状态
    await Task.Delay(500);
    
    // 超时检查（默认5分钟）
    if (sw.ElapsedMilliseconds > timeoutMin * 60 * 1000)
    {
        info.Fail($"执行超时 {timeoutMin} 分钟");
        return;
    }
}
```

**如果消息监听器没有正确处理Discord返回的消息更新任务状态，任务就会一直卡住直到超时！**

## 可能的原因

### 1. 消息匹配失败（已部分修复）

之前的部分匹配逻辑（`EndsWith`/`StartsWith`）会导致错误匹配，我们已经修复为精确匹配。

### 2. Nonce匹配失败

Discord返回的消息中的nonce字段可能没有正确关联到任务：

```csharp
// 任务创建时设置nonce
task.Nonce = SnowFlake.NextId();
task.SetProperty(Constants.TASK_PROPERTY_NONCE, nonce);

// 消息监听器通过nonce查找任务
var task = instance.GetRunningTaskByNonce(nonce);
```

**可能问题**：
- Discord返回的消息中没有nonce字段
- Nonce值被意外修改
- Nonce在传输过程中丢失

### 3. InteractionMetadataId未正确关联

```csharp
// BotMessageListener.cs Line 1295-1298
if (messageType == MessageType.INTERACTION_SUCCESS)
{
    task.InteractionMetadataId = id;
}
```

**可能问题**：
- `INTERACTION_SUCCESS` 消息没有被正确处理
- InteractionMetadataId 设置时机不对
- 任务已经被从运行列表中移除

### 4. 消息处理异常被静默忽略

```csharp
// BotMessageListener.cs Line 184-228
private async Task HandleCommandAsync(SocketMessage arg)
{
    try
    {
        // 处理消息...
    }
    catch (Exception ex)
    {
        Log.Error(ex, "处理 bot 消息异常");
    }
}
```

**可能问题**：
- 异常被捕获但任务状态没有更新
- 某些边界情况没有正确处理
- 任务从running列表中意外移除

### 5. MessageId未正确设置

任务需要通过MessageId来匹配Discord返回的结果：

```csharp
// UserMessageHandler.cs Line 1323-1335
if (messageType == MessageType.CREATE)
{
    task.MessageId = id;
    
    // 设置 prompt 完整词
    if (!string.IsNullOrWhiteSpace(contentStr) && contentStr.Contains("(Waiting to start)"))
    {
        if (string.IsNullOrWhiteSpace(task.PromptFull))
        {
            task.PromptFull = ConvertUtils.GetFullPrompt(contentStr);
        }
    }
}
```

**可能问题**：
- `CREATE` 消息类型判断失败
- MessageId设置时机不对
- 消息被重复处理导致ID被覆盖

## 诊断步骤

### 1. 检查日志

查找卡住的任务ID，搜索相关日志：

```bash
# 搜索任务提交日志
grep "task submitted" log.txt | grep "任务ID"

# 搜索消息处理日志
grep "BOT Received" log.txt | grep "任务ID相关的MessageId"

# 搜索任务状态更新日志
grep "任务ID" log.txt | grep "Status"
```

### 2. 检查任务信息

查看数据库中卡住的任务记录：

```sql
SELECT 
    id,
    status,
    progress,
    nonce,
    message_id,
    interaction_metadata_id,
    prompt,
    submit_time,
    start_time,
    TIMESTAMPDIFF(MINUTE, FROM_UNIXTIME(submit_time/1000), NOW()) as stuck_minutes
FROM tasks
WHERE status = 'SUBMITTED'
ORDER BY submit_time DESC;
```

### 3. 检查运行中的任务

在代码中添加诊断输出：

```csharp
// 在 DiscordInstance.cs 的等待循环中
while (info.Status == TaskStatus.SUBMITTED || info.Status == TaskStatus.IN_PROGRESS)
{
    // 添加诊断日志
    if (sw.ElapsedMilliseconds % 30000 == 0)  // 每30秒输出一次
    {
        _logger.Warning("任务等待中, TaskId: {TaskId}, Status: {Status}, Nonce: {Nonce}, MessageId: {MessageId}, Elapsed: {Elapsed}s", 
            info.Id, info.Status, info.Nonce, info.MessageId, sw.ElapsedMilliseconds / 1000);
    }
    
    SaveAndNotify(info);
    await Task.Delay(500);
    
    if (sw.ElapsedMilliseconds > timeoutMin * 60 * 1000)
    {
        _logger.Error("任务超时, TaskId: {TaskId}, Timeout: {Timeout}min, Nonce: {Nonce}, MessageId: {MessageId}", 
            info.Id, timeoutMin, info.Nonce, info.MessageId);
        info.Fail($"执行超时 {timeoutMin} 分钟");
        SaveAndNotify(info);
        return;
    }
}
```

## 修复方案

### 方案1：增加更详细的日志（推荐）

在关键位置添加日志，帮助诊断问题：

#### 1.1 在消息处理时记录匹配过程

```csharp
// BotMessageHandler.cs FindAndFinishImageTask 方法
protected void FindAndFinishImageTask(DiscordInstance instance, TaskAction action, string finalPrompt, SocketMessage message)
{
    var msgId = GetMessageId(message);
    
    // 记录消息基本信息
    Log.Information("BOT 开始匹配任务, MessageId: {MessageId}, Action: {Action}, FinalPrompt: {Prompt}", 
        msgId, action, finalPrompt?.Substring(0, Math.Min(100, finalPrompt?.Length ?? 0)));
    
    TaskInfo task = null;
    
    // 优先级1: MessageId匹配
    task = instance.FindRunningTask(c => c.MessageId == msgId).FirstOrDefault();
    if (task != null)
    {
        Log.Information("BOT 通过MessageId找到任务, TaskId: {TaskId}, MessageId: {MessageId}", 
            task.Id, msgId);
        // ...处理
        return;
    }
    
    // 优先级2: InteractionMetadataId匹配
    if (message is SocketUserMessage umsg && umsg.InteractionMetadata?.Id != null)
    {
        var metaId = umsg.InteractionMetadata.Id.ToString();
        task = instance.FindRunningTask(c => c.InteractionMetadataId == metaId).FirstOrDefault();
        
        if (task != null)
        {
            Log.Information("BOT 通过InteractionMetadataId找到任务, TaskId: {TaskId}, MetaId: {MetaId}", 
                task.Id, metaId);
            // ...处理
            return;
        }
    }
    
    // ... 其他匹配逻辑
    
    if (task == null)
    {
        Log.Warning("BOT 未找到匹配的任务, MessageId: {MessageId}, Action: {Action}, FinalPrompt: {Prompt}", 
            msgId, action, finalPrompt?.Substring(0, Math.Min(100, finalPrompt?.Length ?? 0)));
        
        // 输出当前所有运行中的任务
        var runningTasks = instance.GetRunningTasks();
        Log.Warning("当前运行中的任务数: {Count}, 任务列表: {Tasks}", 
            runningTasks.Count(), 
            string.Join(", ", runningTasks.Select(t => $"{t.Id}({t.Status})")));
    }
}
```

#### 1.2 在Nonce匹配时添加日志

```csharp
// BotMessageListener.cs OnMessage方法中
if (data.TryGetProperty("nonce", out JsonElement noneElement))
{
    var nonce = noneElement.GetString();
    
    Log.Debug("用户消息包含Nonce, MessageId: {MessageId}, Nonce: {Nonce}", 
        id, nonce);
    
    if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(nonce))
    {
        var task = _discordInstance.GetRunningTaskByNonce(nonce);
        if (task != null)
        {
            Log.Information("通过Nonce找到任务, TaskId: {TaskId}, Nonce: {Nonce}, MessageId: {MessageId}", 
                task.Id, nonce, id);
            // ...处理
        }
        else
        {
            Log.Warning("未通过Nonce找到任务, Nonce: {Nonce}, MessageId: {MessageId}", 
                nonce, id);
        }
    }
}
```

### 方案2：添加自动恢复机制

#### 2.1 在等待循环中添加主动检查

```csharp
// DiscordInstance.cs ExecuteTaskAsync 方法
while (info.Status == TaskStatus.SUBMITTED || info.Status == TaskStatus.IN_PROGRESS)
{
    SaveAndNotify(info);
    await Task.Delay(500);
    
    // 每30秒输出诊断日志
    if (sw.ElapsedMilliseconds > 0 && sw.ElapsedMilliseconds % 30000 < 500)
    {
        _logger.Warning("任务长时间处于 {Status} 状态, TaskId: {TaskId}, Nonce: {Nonce}, MessageId: {MessageId}, Elapsed: {Elapsed}s", 
            info.Status, info.Id, info.Nonce, info.MessageId, sw.ElapsedMilliseconds / 1000);
        
        // 检查是否在running列表中
        var isInRunning = _runningTasks.ContainsKey(info.Id);
        _logger.Warning("任务是否在运行列表中: {IsInRunning}, TaskId: {TaskId}", 
            isInRunning, info.Id);
    }
    
    if (sw.ElapsedMilliseconds > timeoutMin * 60 * 1000)
    {
        _logger.Error("任务执行超时, TaskId: {TaskId}, Timeout: {Timeout}min, Status: {Status}, Nonce: {Nonce}, MessageId: {MessageId}", 
            info.Id, timeoutMin, info.Status, info.Nonce, info.MessageId);
        
        info.Fail($"执行超时 {timeoutMin} 分钟 - Status: {info.Status}, Nonce: {info.Nonce}");
        SaveAndNotify(info);
        return;
    }
}
```

#### 2.2 添加任务健康检查定时器

```csharp
// 在 DiscordInstance 构造函数中启动健康检查定时器
private void StartTaskHealthCheck()
{
    Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(1));
                
                var stuckTasks = _runningTasks.Values
                    .Where(t => 
                        (t.Status == TaskStatus.SUBMITTED || t.Status == TaskStatus.IN_PROGRESS) &&
                        t.StartTime > 0 &&
                        DateTimeOffset.Now.ToUnixTimeMilliseconds() - t.StartTime > 5 * 60 * 1000) // 超过5分钟
                    .ToList();
                
                if (stuckTasks.Any())
                {
                    _logger.Warning("发现 {Count} 个可能卡住的任务, AccountId: {AccountId}", 
                        stuckTasks.Count, Account.ChannelId);
                    
                    foreach (var task in stuckTasks)
                    {
                        _logger.Warning("卡住的任务详情, TaskId: {TaskId}, Status: {Status}, Nonce: {Nonce}, MessageId: {MessageId}, StuckMinutes: {Minutes}", 
                            task.Id, 
                            task.Status, 
                            task.Nonce, 
                            task.MessageId,
                            (DateTimeOffset.Now.ToUnixTimeMilliseconds() - task.StartTime) / 60000);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "任务健康检查异常, AccountId: {AccountId}", Account.ChannelId);
            }
        }
    });
}
```

### 方案3：优化消息处理流程（重要）

#### 3.1 确保所有消息类型都被正确处理

```csharp
// BotMessageListener.cs HandleCommandAsync
private async Task HandleCommandAsync(SocketMessage arg)
{
    try
    {
        var msg = arg as SocketUserMessage;
        if (msg == null)
            return;

        var msgId = msg.Id.ToString();
        var metaId = msg.InteractionMetadata?.Id?.ToString();
        var hasContent = !string.IsNullOrWhiteSpace(msg.Content);
        
        _logger.Information($"BOT Received, Type: {msg.Type}, MsgId: {msgId}, MetaId: {metaId}, HasContent: {hasContent}, Content: {msg.Content?.Substring(0, Math.Min(100, msg.Content.Length))}");

        // 检查是否有对应的running task
        var taskByMsgId = _discordInstance.FindRunningTask(c => c.MessageId == msgId).FirstOrDefault();
        var taskByMetaId = !string.IsNullOrWhiteSpace(metaId) 
            ? _discordInstance.FindRunningTask(c => c.InteractionMetadataId == metaId).FirstOrDefault()
            : null;
        
        if (taskByMsgId != null)
        {
            _logger.Information("通过MessageId找到对应任务, TaskId: {TaskId}, MsgId: {MsgId}", 
                taskByMsgId.Id, msgId);
        }
        
        if (taskByMetaId != null)
        {
            _logger.Information("通过MetaId找到对应任务, TaskId: {TaskId}, MetaId: {MetaId}", 
                taskByMetaId.Id, metaId);
        }
        
        if (taskByMsgId == null && taskByMetaId == null && hasContent)
        {
            _logger.Debug("未找到对应任务, MsgId: {MsgId}, MetaId: {MetaId}, Content: {Content}", 
                msgId, metaId, msg.Content?.Substring(0, Math.Min(50, msg.Content.Length)));
        }

        // ... 原有的处理逻辑
    }
    catch (Exception ex)
    {
        Log.Error(ex, "处理 bot 消息异常, MessageId: {MessageId}", arg?.Id);
    }
}
```

## 临时解决方案（紧急情况）

如果问题频繁发生，可以添加一个定时清理机制：

```csharp
// 在某个全局服务中添加
public class StuckTaskCleaner
{
    public void StartCleaning()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromMinutes(10));
                
                try
                {
                    // 查找超过10分钟还在SUBMITTED状态的任务
                    var stuckTasks = DbHelper.Instance.TaskStore
                        .Where(t => 
                            t.Status == TaskStatus.SUBMITTED &&
                            t.SubmitTime > 0 &&
                            DateTimeOffset.Now.ToUnixTimeMilliseconds() - t.SubmitTime > 10 * 60 * 1000)
                        .ToList();
                    
                    foreach (var task in stuckTasks)
                    {
                        Log.Warning("发现卡住的任务, 自动标记为失败, TaskId: {TaskId}, SubmitTime: {SubmitTime}", 
                            task.Id, task.SubmitTime);
                        
                        task.Status = TaskStatus.FAILURE;
                        task.FailReason = "任务超时: 提交后10分钟无响应";
                        task.FinishTime = DateTimeOffset.Now.ToUnixTimeMilliseconds();
                        
                        DbHelper.Instance.TaskStore.Update(task);
                    }
                    
                    if (stuckTasks.Any())
                    {
                        Log.Warning("自动清理了 {Count} 个卡住的任务", stuckTasks.Count);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "清理卡住任务异常");
                }
            }
        });
    }
}
```

## 建议的实施顺序

1. **立即实施**: 方案1（增加详细日志）- 帮助诊断问题根源
2. **短期实施**: 方案2（自动恢复机制）- 减少问题影响
3. **中期实施**: 方案3（优化消息处理）- 从根本上解决问题
4. **应急措施**: 临时清理机制 - 防止系统完全阻塞

## 预防措施

1. **监控告警**: 设置任务超时告警，及时发现问题
2. **日志分析**: 定期分析日志，找出pattern
3. **压力测试**: 模拟高并发场景，提前发现问题
4. **代码审查**: 重点审查消息匹配和状态更新逻辑

## 相关问题

如果还有其他症状，可能是相关问题：

1. **任务状态从IN_PROGRESS卡住**: 检查进度更新逻辑
2. **任务完成但回调未触发**: 检查回调通知机制
3. **消息重复处理**: 检查消息去重逻辑
4. **内存泄漏**: 检查任务清理机制

## 总结

任务卡在SUBMITTED状态的根本原因是**消息监听器没有正确处理Discord返回的消息**，导致任务状态没有更新。通过增加详细的日志、添加自动恢复机制和优化消息处理流程，可以有效解决这个问题。

