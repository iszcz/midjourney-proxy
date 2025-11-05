# 任务混淆问题修复说明

## 问题描述

在使用Midjourney代理时，当用户只传图片而没有传提示词时，可能发生任务串乱的问题：
- 用户A提交的任务结果可能返回给用户B
- 用户B提交的任务结果可能返回给用户A
- 任务之间的结果出现混淆和错配

## 根本原因分析

### 问题定位

问题出现在任务匹配逻辑中，具体位置：
- `src/Midjourney.Infrastructure/Handle/BotMessageHandler.cs`
- `src/Midjourney.Infrastructure/Handle/UserMessageHandler.cs`

这两个文件中的 `FindAndFinishImageTask` 方法使用了**7个优先级**来匹配Discord返回的结果和用户提交的任务。

### 匹配优先级

1. ✅ **优先级1**: 通过MessageId匹配（最可靠）
2. ✅ **优先级2**: 通过InteractionMetadataId匹配
3. ✅ **优先级3**: 通过PromptFull匹配（完整提示词）
4. ⚠️ **优先级4**: 通过FormatPrompt匹配（**存在问题**）
5. ⚠️ **优先级5**: 通过FormatPromptParam匹配（**存在问题**）
6. ✅ **优先级6**: 特殊任务类型匹配（SHOW任务）
7. ⚠️ **优先级7**: 模糊匹配（**问题所在**）

### 问题核心

#### 问题1：优先级4和5的部分匹配导致误匹配（更严重！）

**即使有提示词，也会发生混淆！**

原代码使用了危险的 `EndsWith` 和 `StartsWith` 进行部分匹配：

```csharp
// ❌ 原有的危险代码（优先级4和5）
&& (c.PromptEn.FormatPrompt() == prompt 
    || c.PromptEn.FormatPrompt().EndsWith(prompt)     // 部分匹配！
    || prompt.StartsWith(c.PromptEn.FormatPrompt()))  // 部分匹配！
```

**实际案例**：
- **TaskA** 提交：`"A surreal landscape with red rocky cliffs..."`
- **TaskB** 提交：`"A children's book cover, featuring a child..."`

当Discord返回的结果经过格式化后，可能因为参数相似（如都包含 `--v 7 --ar`）或文本片段重叠，导致部分匹配成功，错误地将TaskB的结果分配给TaskA。

**为什么会发生**：
1. `FormatPrompt()` 和 `FormatPromptParam()` 会对提示词进行格式化处理
2. 格式化后可能提取相同的参数片段
3. `EndsWith` 和 `StartsWith` 会匹配这些片段
4. 导致完全不同的提示词被错误匹配

#### 问题2：优先级7的模糊匹配导致无提示词任务混淆

当用户**只传图片没有传提示词**时：

1. 前6个优先级都可能匹配失败
2. 系统进入**优先级7的模糊匹配**
3. 模糊匹配**仅根据**：
   - `action`（任务类型：IMAGINE、UPSCALE等）
   - `botType`（机器人类型：MJ/NIJI）
   - `StartTime`（5分钟内的任务）
4. 然后**简单地取最早开始的任务**

```csharp
// 原有的危险代码
task = instance.FindRunningTask(c => 
    (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
    (c.BotType == botType || c.RealBotType == botType) && 
    c.Action == action &&
    c.StartTime >= cutoffTime)
    .OrderBy(c => c.StartTime).FirstOrDefault();  // ❌ 无条件取第一个！
```

### 问题场景示例

```
时间线：
10:00:00 - 用户A提交图片任务（无提示词）→ TaskA
10:00:05 - 用户B提交图片任务（无提示词）→ TaskB  
10:01:00 - Discord返回TaskB的结果
          ❌ 匹配逻辑：找到最早的任务 = TaskA
          ❌ 结果：TaskB的图片被分配给TaskA
10:01:30 - Discord返回TaskA的结果
          ❌ 匹配逻辑：TaskA已完成，找到下一个 = TaskB
          ❌ 结果：TaskA的图片被分配给TaskB

最终：用户A和用户B的任务结果完全错乱！
```

## 修复方案

### 修复1：移除优先级4和5的危险部分匹配（关键修复！）

**彻底移除 `EndsWith` 和 `StartsWith`，只保留精确匹配**：

```csharp
// ✅ 新的安全代码（优先级4和5）
var candidateTasks = instance
    .FindRunningTask(c =>
        条件匹配 &&
        c.PromptEn.FormatPrompt() == prompt)  // 仅精确匹配！
    .OrderBy(c => c.StartTime)
    .ToList();

if (candidateTasks.Count > 0)
{
    task = candidateTasks.First();
    if (candidateTasks.Count > 1)
    {
        Log.Warning("FormatPrompt匹配发现多个相同提示词的任务...");
    }
}
```

**修复效果**：
- ✅ 只有**完全相同**的提示词才会匹配
- ✅ 不同的提示词绝对不会误匹配
- ✅ 即使提示词包含相似内容，也不会混淆

### 修复2：优化优先级7的模糊匹配

**核心改进**：

1. **缩短时间窗口**：从5分钟缩短到2分钟，减少误匹配概率
2. **唯一性检查**：只有在找到唯一候选任务时才匹配
3. **错误告警**：当发现多个候选任务时记录错误日志，并拒绝匹配
4. **详细日志**：添加警告和错误日志，便于问题追踪

### 修改后的逻辑（优先级7）

```csharp
// 新的安全代码
var cutoffTime = DateTimeOffset.Now.AddMinutes(-2).ToUnixTimeMilliseconds();
var candidateTasks = instance.FindRunningTask(c => 
    (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
    (c.BotType == botType || c.RealBotType == botType) && 
    c.Action == action &&
    c.StartTime >= cutoffTime)
    .OrderBy(c => c.StartTime)
    .ToList();

// ✅ 只有唯一候选时才匹配
if (candidateTasks.Count == 1)
{
    task = candidateTasks.First();
    Log.Warning("使用模糊匹配找到任务, TaskId: {TaskId}, Action: {Action}, 建议优化任务提交时的唯一标识", 
        task.Id, action);
}
// ❌ 多个候选时拒绝匹配，避免错误
else if (candidateTasks.Count > 1)
{
    Log.Error("发现多个候选任务无法区分, Count: {Count}, Action: {Action}, MessageId: {MessageId}, 可能导致任务混淆！", 
        candidateTasks.Count, action, msgId);
    task = null;  // 不匹配任何任务
}
```

### 修改的文件

1. **BotMessageHandler.cs**
   - 位置：`src/Midjourney.Infrastructure/Handle/BotMessageHandler.cs`
   - 修改行：183-269行
   - 改进：优先级7的模糊匹配逻辑

2. **UserMessageHandler.cs**
   - 位置：`src/Midjourney.Infrastructure/Handle/UserMessageHandler.cs`
   - 修改行：175-261行
   - 改进：优先级6的模糊匹配逻辑（对应优先级7）

## 修复效果

### 修复前

- ❌ **即使有提示词也会串任务**（优先级4和5的部分匹配导致）
- ❌ 多个用户同时提交相似任务时容易串任务
- ❌ 只传图片不传提示词时容易混淆
- ❌ 没有警告机制，问题难以发现
- ❌ 时间窗口过长（5分钟）增加误匹配概率

### 修复后

- ✅ **移除部分匹配，只有完全相同的提示词才匹配**（修复优先级4和5）
- ✅ 只有唯一候选任务时才进行模糊匹配
- ✅ 多个候选任务时拒绝匹配，防止错误
- ✅ 添加详细的警告和错误日志
- ✅ 缩短时间窗口到2分钟，减少误匹配
- ✅ 当模糊匹配成功时记录警告，提示需要优化

## 日志输出

### 正常匹配（唯一候选）

```
[WARN] USER 使用模糊匹配找到任务, TaskId: 1234567890123, Action: IMAGINE, 建议优化任务提交时的唯一标识
```

### 异常情况（多个候选）

```
[ERROR] USER 发现多个候选任务无法区分, Count: 2, Action: IMAGINE, MessageId: 9876543210, 可能导致任务混淆！
```

## 建议

虽然此修复大大降低了任务混淆的概率，但仍建议：

1. **用户侧优化**：
   - 尽量在提交任务时添加提示词
   - 即使只传图片，也可以添加简单的描述文字
   - 避免短时间内提交大量无提示词的任务

2. **系统侧监控**：
   - 关注日志中的警告和错误信息
   - 如果频繁出现"多个候选任务"的错误，需要优化任务提交流程
   - 考虑在任务创建时添加更多唯一标识（如用户ID、会话ID等）

3. **未来优化方向**：
   - 在任务创建时强制要求提示词（至少是默认值）
   - 增强MessageId和InteractionMetadataId的可靠性
   - 考虑添加用户维度的任务隔离

## 测试建议

1. **场景1：单用户多任务**
   - 同一用户短时间内提交多个无提示词的图片任务
   - 验证任务结果是否正确对应

2. **场景2：多用户并发**
   - 多个用户同时提交相同类型的任务
   - 验证任务结果不会交叉混淆

3. **场景3：有提示词任务**
   - 正常带提示词的任务应该不受影响
   - 验证前6个优先级的匹配仍然正常工作

## 版本信息

- **修复日期**：2025-01-XX
- **影响范围**：任务匹配逻辑
- **兼容性**：向后兼容，不影响现有功能
- **风险等级**：低（仅优化匹配逻辑，不改变核心流程）

## 联系方式

如有问题或发现新的任务混淆情况，请及时反馈并提供：
- 详细的任务提交时间和参数
- 相关的日志信息（特别是WARNING和ERROR级别）
- 任务混淆的具体表现

