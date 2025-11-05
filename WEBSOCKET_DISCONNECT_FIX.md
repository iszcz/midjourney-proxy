# WebSocket断开导致任务卡住问题修复

## 🔴 严重问题确认

**您的直觉完全正确！** WebSocket断开确实会导致任务永久卡在SUBMITTED状态！

## 问题分析

### IsAlive的定义

```csharp
public bool IsAlive => IsInit && Account != null
     && Account.Enable == true
     && WebSocketManager != null
     && WebSocketManager.Running == true  // ⚠️ 依赖WebSocket连接状态
     && Account.Lock == false;
```

**关键点**: `IsAlive` 依赖于 `WebSocketManager.Running`，当WebSocket断开时，`Running` 会变为 `false`，`IsAlive` 也会变为 `false`。

### 原有的问题代码

```csharp
// ExecuteTaskAsync 方法

// ✅ 提交前检查
if (!IsAlive)
{
    info.Fail("实例不可用");
    return;
}

// 设置为SUBMITTED
info.Status = TaskStatus.SUBMITTED;
SaveAndNotify(info);

// 提交到Discord
var result = await discordSubmit();

// ✅ 提交后检查
if (!IsAlive)
{
    info.Fail("实例不可用");
    return;
}

// ❌ 等待循环 - 没有检查IsAlive！
while (info.Status == TaskStatus.SUBMITTED || info.Status == TaskStatus.IN_PROGRESS)
{
    await Task.Delay(500);
    
    // ❌ 如果WebSocket在这期间断开，不会被检测到！
    // ❌ 任务会一直等待直到超时（默认5分钟）！
    
    if (sw.ElapsedMilliseconds > timeoutMin * 60 * 1000)
    {
        info.Fail($"执行超时 {timeoutMin} 分钟");
        return;
    }
}
```

### 问题场景

#### 场景1: WebSocket在等待期间断开

```
时间轴：
10:00:00 - 任务提交成功 ✓
10:00:01 - Discord收到请求 ✓
10:00:02 - 进入等待循环（等待Discord返回结果）✓
10:00:30 - WebSocket连接断开！❌
           ↓
           WebSocketManager.Running = false
           IsAlive = false
           但等待循环不知道！继续傻等...
           ↓
10:01:00 - 仍在等待（任务卡在SUBMITTED）
10:02:00 - 仍在等待（任务卡在SUBMITTED）
10:03:00 - 仍在等待（任务卡在SUBMITTED）
10:04:00 - 仍在等待（任务卡在SUBMITTED）
10:05:02 - 超时！任务失败 ❌

影响：
- 任务白白等待了5分钟
- Discord的返回消息永远收不到
- 后续提交的任务全部被阻塞
- 用户体验极差
```

#### 场景2: 多个任务同时卡住

```
情况：
- 账号A的WebSocket断开
- 但已经有5个任务在等待中
- 所有5个任务都会卡住5分钟才超时
- 这5分钟内，该账号无法接受新任务
- 系统吞吐量大幅下降
```

## ✅ 修复方案

### 核心修复：在等待循环中检查IsAlive

```csharp
while (info.Status == TaskStatus.SUBMITTED || info.Status == TaskStatus.IN_PROGRESS)
{
    SaveAndNotify(info);
    await Task.Delay(500);

    // ✅ 关键修复：每次循环都检查WebSocket连接状态
    if (!IsAlive)
    {
        _logger.Error("任务等待期间实例变为不可用 [{AccountDisplay}], TaskId: {TaskId}, Status: {Status}, WebSocketRunning: {WsRunning}, AccountEnable: {AccountEnable}, Elapsed: {Elapsed}s", 
            Account.GetDisplay(), info.Id, info.Status, WebSocketManager?.Running ?? false, Account?.Enable ?? false, sw.ElapsedMilliseconds / 1000);
        
        info.Fail($"实例不可用 - WebSocket连接已断开，任务无法继续 (等待了 {sw.ElapsedMilliseconds / 1000}秒)");
        SaveAndNotify(info);
        return;  // 立即退出，不再等待！
    }

    // 诊断日志...
}
```

### 修复效果对比

#### 修复前

```
WebSocket断开后：
10:00:30 - WebSocket断开
10:05:30 - 任务超时失败（等待了5分钟）
           后续任务被阻塞5分钟
```

#### 修复后

```
WebSocket断开后：
10:00:30 - WebSocket断开
10:00:30 - 立即检测到IsAlive=false
10:00:30 - 任务立即失败（等待了0.5秒）✓
           后续任务不会被长时间阻塞
```

**时间节省**: 从5分钟缩短到0.5秒，快了**600倍**！

## WebSocket重连机制

### 已有的重连机制

系统已经实现了WebSocket重连机制：

```csharp
// WebSocketManager.cs

private const int CONNECT_RETRY_LIMIT = 5;  // 最多重试5次

// 重连逻辑
private void TryReconnect()
{
    // 尝试恢复会话
    if (!string.IsNullOrWhiteSpace(_sessionId) && _sequence.HasValue)
    {
        await WebSocket.ConnectAsync(new Uri(gatewayUrl), CancellationToken.None);
        await ResumeSessionAsync();  // 恢复会话
    }
    else
    {
        TryNewConnect();  // 新连接
    }
}

// 失败处理
private void HandleFailure(int code, string reason)
{
    Running = false;  // ⚠️ 设置Running为false
    
    if (code >= 4000)
    {
        TryNewConnect();  // 尝试新连接
    }
    else if (code == 2001)
    {
        TryReconnect();  // 尝试重连
    }
}
```

### 重连过程中的任务状态

```
时间轴：
10:00:00 - WebSocket断开
           ↓ Running = false
           ↓ IsAlive = false
           
10:00:01 - 【修复前】任务继续等待（会卡5分钟）❌
           【修复后】任务立即失败 ✓
           
10:00:05 - WebSocket开始重连
10:00:10 - WebSocket重连成功
           ↓ Running = true
           ↓ IsAlive = true
           ↓ 可以接受新任务 ✓
           
10:00:15 - 用户重新提交任务 ✓
           可以正常执行
```

## 完整的任务生命周期

### 正常情况

```
1. 提交任务
   ↓
2. 检查IsAlive ✓
   ↓
3. 设置为SUBMITTED
   ↓
4. 提交到Discord
   ↓
5. 再次检查IsAlive ✓
   ↓
6. 进入等待循环
   ↓ 每0.5秒检查一次
   ↓ 【新增】检查IsAlive ✓
   ↓ 【新增】检查WebSocket状态 ✓
   ↓
7. 收到Discord消息
   ↓
8. 消息匹配成功
   ↓
9. 任务状态更新为SUCCESS
   ↓
10. 退出等待循环 ✓
```

### WebSocket断开的情况

```
1. 提交任务
   ↓
2. 检查IsAlive ✓ (WebSocket正常)
   ↓
3. 设置为SUBMITTED
   ↓
4. 提交到Discord
   ↓
5. 再次检查IsAlive ✓ (WebSocket正常)
   ↓
6. 进入等待循环
   ↓ 第1次循环：IsAlive=true ✓
   ↓ 第2次循环：IsAlive=true ✓
   ↓ 【WebSocket断开！】
   ↓ 第3次循环：IsAlive=false ❌
   ↓
7. 【修复前】继续等待5分钟 ❌
   【修复后】立即检测到并失败任务 ✓
   ↓
8. 任务失败，原因："WebSocket连接已断开"
   ↓
9. 立即返回失败结果给用户 ✓
   ↓
10. 不阻塞后续任务 ✓
```

## 诊断日志

### 正常日志

```
[INFO] 任务提交, TaskId: xxx
[INFO] 任务状态: SUBMITTED
[WARN] 任务等待中, TaskId: xxx, Status: SUBMITTED, Elapsed: 30s, WebSocketRunning: True
[INFO] BOT 开始匹配任务, MessageId: xxx
[INFO] BOT 成功匹配任务, TaskId: xxx
[INFO] 任务完成, TaskId: xxx, Status: SUCCESS
```

### WebSocket断开的日志

```
[INFO] 任务提交, TaskId: xxx
[INFO] 任务状态: SUBMITTED
[ERROR] 用户 WebSocket 连接失败, 代码 1006: WebSocket异常关闭
[ERROR] 任务等待期间实例变为不可用, TaskId: xxx, Status: SUBMITTED, WebSocketRunning: False, AccountEnable: True, Elapsed: 2s
[INFO] 任务失败, TaskId: xxx, FailReason: 实例不可用 - WebSocket连接已断开
[INFO] WebSocket开始重连...
[INFO] WebSocket重连成功
```

## 修复效果

### 修复前

| 场景 | 等待时间 | 结果 | 影响 |
|------|---------|------|------|
| WebSocket断开 | 5分钟（超时） | 任务失败 | 后续任务被阻塞5分钟 |
| 多个任务等待 | 每个5分钟 | 全部失败 | 系统几乎不可用 |
| 重连期间提交 | 无法提交 | 队列满 | 用户无法使用 |

### 修复后

| 场景 | 等待时间 | 结果 | 影响 |
|------|---------|------|------|
| WebSocket断开 | 0.5秒（立即） | 任务快速失败 | ✅ 不阻塞后续任务 |
| 多个任务等待 | 每个0.5秒 | 快速清理 | ✅ 系统快速恢复 |
| 重连期间提交 | 拒绝新任务 | 明确错误 | ✅ 用户知道原因 |

**性能提升**: 
- 从等待5分钟 → 等待0.5秒
- 快了**600倍**
- 系统可用性大幅提升

## WebSocket重连机制

### 自动重连

系统已经实现了自动重连机制：

1. **最大重试次数**: 5次
2. **重连类型**: 
   - 会话恢复（如果有SessionId和Sequence）
   - 新连接（如果会话已过期）
3. **重连触发**: 
   - WebSocket异常断开
   - 心跳超时
   - 网络错误

### 重连流程

```
WebSocket断开
   ↓
Running = false
IsAlive = false
   ↓
【新修复】正在等待的任务立即失败 ✓
   ↓
尝试重连（最多5次）
   ↓
重连成功
   ↓
Running = true
IsAlive = true
   ↓
可以接受新任务 ✓
```

## 增强的日志输出

### 1. 等待循环中的WebSocket状态

```
[WARN] 任务等待中, TaskId: xxx, Status: SUBMITTED, Elapsed: 30s, WebSocketRunning: True
```

**关键字段**: `WebSocketRunning` - 直接显示WebSocket连接状态

### 2. WebSocket断开时的错误日志

```
[ERROR] 任务等待期间实例变为不可用, TaskId: xxx, Status: SUBMITTED, WebSocketRunning: False, AccountEnable: True, Elapsed: 2s
```

**关键信息**:
- `WebSocketRunning: False` - WebSocket已断开
- `AccountEnable: True` - 账号本身是启用的
- `Elapsed: 2s` - 只等待了2秒就检测到了

### 3. 超时时的WebSocket状态

```
[ERROR] 任务执行超时, TaskId: xxx, Timeout: 5min, Status: SUBMITTED, WebSocketRunning: False
```

**用途**: 即使超时了，也能知道是因为WebSocket断开还是其他原因

## 修复的优势

### 1. 快速失败（Fail Fast）

- ❌ 修复前: WebSocket断开后仍等待5分钟
- ✅ 修复后: WebSocket断开后0.5秒内失败

### 2. 明确的错误信息

- ❌ 修复前: "执行超时 5 分钟"（不知道原因）
- ✅ 修复后: "WebSocket连接已断开，任务无法继续"（原因明确）

### 3. 不阻塞后续任务

- ❌ 修复前: 后续任务被阻塞5分钟
- ✅ 修复后: 后续任务几乎不受影响

### 4. 易于诊断

- ❌ 修复前: 难以判断是超时还是断连
- ✅ 修复后: 日志明确显示WebSocket状态

## 使用场景

### 场景1: 网络不稳定

```
情况：公司网络不稳定，WebSocket频繁断开重连

修复前：
- 每次断开，所有正在等待的任务都会卡5分钟
- 如果每小时断开1次，每次有10个任务在等待
- 每小时浪费: 10任务 × 5分钟 = 50分钟

修复后：
- 每次断开，任务立即失败（0.5秒）
- 每小时浪费: 10任务 × 0.5秒 = 5秒
- 节省时间: 99.83%
```

### 场景2: Discord服务异常

```
情况：Discord服务端异常，强制关闭WebSocket连接

修复前：
- 所有任务卡住5分钟
- 用户不知道发生了什么
- 系统看起来"死机"了

修复后：
- 任务立即失败
- 错误信息明确："WebSocket连接已断开"
- WebSocket自动重连
- 重连后可以重新提交任务
```

### 场景3: 服务器重启

```
情况：服务器重启或升级

修复前：
- 重启前提交的任务会卡5分钟
- 用户需要等待很长时间

修复后：
- 任务立即失败
- 用户可以在服务器重启后立即重新提交
```

## 测试建议

### 1. 模拟WebSocket断开

```csharp
// 在测试环境中手动断开WebSocket
webSocketManager.Dispose();

// 观察日志输出
// 应该看到：
// [ERROR] 任务等待期间实例变为不可用, WebSocketRunning: False
// [INFO] 任务失败, FailReason: WebSocket连接已断开
```

### 2. 验证重连后恢复

```
1. 提交任务
2. 手动断开WebSocket
3. 观察任务立即失败
4. 等待WebSocket重连（应该几秒内完成）
5. 重新提交任务
6. 验证任务正常执行
```

### 3. 压力测试

```
1. 启动10个并发任务
2. 在任务执行中途断开WebSocket
3. 观察所有任务是否都快速失败（而不是等待5分钟）
4. 验证WebSocket重连后系统恢复正常
```

## 相关问题

### Q1: WebSocket断开后任务能恢复吗？

**答**: 不能。一旦WebSocket断开，正在等待的任务无法收到Discord的返回消息，必须失败。

**解决方案**: 
- 用户重新提交任务
- 或实现自动重试机制

### Q2: WebSocket多久会重连？

**答**: 通常几秒到十几秒，取决于：
- 断开原因（异常、超时等）
- 网络状况
- 重连重试次数

### Q3: 重连期间能提交新任务吗？

**答**: 不能。因为 `IsAlive = false`，系统会拒绝新任务提交。

**错误信息**: "无可用的账号实例"

### Q4: 如何避免WebSocket频繁断开？

**建议**:
1. 使用稳定的网络连接
2. 正确配置代理（如果使用）
3. 监控WebSocket健康状态
4. 及时处理CF验证
5. 确保Discord账号状态正常

## 监控建议

### 1. WebSocket状态监控

```bash
# 监控WebSocket断开事件
grep "WebSocket 连接失败" logs/log*.txt

# 监控重连事件
grep "WebSocket 连接已建立" logs/log*.txt

# 统计今天的断开次数
grep "WebSocket 连接失败" logs/log$(date +%Y%m%d).txt | wc -l
```

### 2. 任务失败原因统计

```bash
# 统计因WebSocket断开而失败的任务
grep "WebSocket连接已断开" logs/log*.txt | wc -l

# 查看最近的断连失败
grep "WebSocket连接已断开" logs/log*.txt | tail -20
```

### 3. 设置告警

建议设置以下告警：

- WebSocket断开次数 > 5次/小时 → 警告
- WebSocket断开次数 > 20次/小时 → 严重告警
- 因WebSocket断开失败的任务 > 10个/小时 → 警告

## 配置建议

### 1. 调整超时时间

如果网络不稳定，可以适当增加超时时间：

```json
{
  "timeoutMinutes": 10  // 从5分钟增加到10分钟
}
```

**注意**: 虽然修复后WebSocket断开会立即失败，但其他原因的超时仍然有效。

### 2. 启用自动登录

确保WebSocket断开后能自动恢复：

```json
{
  "enableAutoLogin": true
}
```

### 3. 配置健康检查

定期检查WebSocket状态：

```csharp
// 建议添加到DiscordInstance
private void StartWebSocketHealthCheck()
{
    Task.Run(async () =>
    {
        while (!_isDispose)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            
            if (Account.Enable && !IsAlive)
            {
                _logger.Warning("账号启用但实例不可用, ChannelId: {ChannelId}, WebSocketRunning: {Running}", 
                    Account.ChannelId, WebSocketManager?.Running ?? false);
            }
        }
    });
}
```

## 总结

### 问题

✅ **您的判断完全正确**：WebSocket断开**确实会**导致任务卡在SUBMITTED状态！

原因：
- 等待循环中没有检查WebSocket连接状态
- 断开后任务会一直等待直到超时（5分钟）
- 阻塞后续所有任务

### 修复

✅ **已完成修复**：在等待循环中添加 `IsAlive` 检查

效果：
- WebSocket断开后0.5秒内检测到
- 任务立即失败，不再等待5分钟
- 不阻塞后续任务
- 明确的错误信息
- 详细的诊断日志

### 建议

1. ✅ 监控WebSocket连接状态
2. ✅ 关注日志中的断连事件
3. ✅ 优化网络和代理配置
4. ✅ 必要时增加超时时间

现在系统能够**快速检测WebSocket断开并立即处理**，不会再因为连接问题导致任务长时间卡住！

