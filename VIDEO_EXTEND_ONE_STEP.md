# 视频扩展(Video Extend)一步提交功能实现文档

## 功能概述

本次更新实现了视频扩展(Video Extend)操作的一步提交功能。用户只需通过 `/mj/submit/video` 接口提交一次请求，系统会自动完成以下步骤：

1. **Upscale操作** - 放大指定索引的视频
2. **Extend操作** - 自动触发视频扩展
3. **返回最终结果** - 完成后返回扩展后的视频

## API使用方法

### 请求示例

```json
POST /mj/submit/video

{
    "prompt": "又飞来好几只蜜蜂",
    "motion": "high",
    "action": "extend",
    "index": 0,
    "taskId": "1762547298152655"
}
```

### 参数说明

| 参数 | 类型 | 必填 | 说明 |
|------|------|------|------|
| prompt | string | 是 | 扩展视频的提示词 |
| motion | string | 否 | 运动程度：low/medium/high，默认为low |
| action | string | 是 | 操作类型，固定为"extend" |
| index | int | 是 | 要放大的视频索引（0-3或1-4都支持，系统会自动转换） |
| taskId | string | 是 | 源视频任务ID |

### 响应示例

```json
{
    "code": 1,
    "description": "提交成功",
    "result": "1762547298152656",
    "properties": {}
}
```

返回的 `result` 是新创建的VIDEO_EXTEND任务ID，可以通过 `/mj/task/{id}/fetch` 接口查询任务状态和最终结果。

## 实现原理

### 1. 任务流程

```
用户请求 (action=extend)
    ↓
SubmitVideoExtend (创建VIDEO_EXTEND任务)
    ↓
SubmitVideoUpscale (提交video_virtual_upscale)
    ↓
Upscale完成 (UserUpscaleSuccessHandler)
    ↓
自动触发Extend (AutoSubmitVideoExtend)
    ↓
    ├─ 提交Extend Action (触发modal)
    ├─ 等待Modal消息
    └─ 提交RemixModal (with prompt)
    ↓
Extend完成 (最终视频结果)
```

**重要说明**: 
1. 视频扩展需要先进行 `video_virtual_upscale` 操作（不是普通的 `upsample`）
2. Upscale完成后才会出现extend按钮
3. 系统自动检测VIDEO_EXTEND任务并触发extend操作

### 2. 核心代码修改

#### 2.1 TaskInfo.Success() 方法修改

**文件**: `src/Midjourney.Infrastructure/Models/TaskInfo.cs`

**修改内容**:
- 当VIDEO_EXTEND任务的upscale完成时，不立即设置为SUCCESS状态
- 而是保持SUBMITTED状态，进度设置为50%
- 存储extend相关信息，等待后续的extend操作完成

```csharp
if (Action == TaskAction.VIDEO_EXTEND && !string.IsNullOrWhiteSpace(GetProperty<string>("EXTEND_PROMPT", default)))
{
    // 重置状态为SUBMITTED，表示upscale完成，但整个extend任务还在进行中
    Status = TaskStatus.SUBMITTED;
    Progress = "50%";  // 表示完成了一半（upscale完成）
    
    // 存储extend相关信息
    SetProperty(Constants.TASK_PROPERTY_CUSTOM_ID, customId);
    SetProperty(Constants.TASK_PROPERTY_REMIX_MODAL, "MJ::AnimateModal::prompt");
    SetProperty(Constants.TASK_PROPERTY_REMIX_CUSTOM_ID, customId);
    SetProperty("EXTEND_UPSCALE_COMPLETED", "true");
    
    // 不在这里设置为SUCCESS，让任务继续等待extend操作完成
    return;
}
```

#### 2.2 UserUpscaleSuccessHandler 增强

**文件**: `src/Midjourney.Infrastructure/Handle/UserUpscaleSuccessHandler.cs`

**新增功能**:
- 检测VIDEO_EXTEND任务的upscale完成
- 自动触发extend操作的提交
- 处理remix modal的自动提交流程

**关键步骤**:

1. **检测VIDEO_EXTEND任务**
```csharp
if (task.Action == TaskAction.VIDEO_EXTEND && !string.IsNullOrWhiteSpace(task.GetProperty<string>("EXTEND_PROMPT", default)))
{
    AutoSubmitVideoExtend(instance, task);
}
```

2. **自动提交Extend操作**
```csharp
private void AutoSubmitVideoExtend(DiscordInstance instance, TaskInfo task)
{
    // 步骤1: 提交action，触发modal弹窗
    var actionResult = await instance.ActionAsync(task.MessageId, customId, messageFlags, nonce, task);
    
    // 步骤2: 等待获取modal的messageId和interactionMetadataId
    while (string.IsNullOrWhiteSpace(task.RemixModalMessageId) || string.IsNullOrWhiteSpace(task.InteractionMetadataId))
    {
        await Task.Delay(1000);
        task = DbHelper.Instance.TaskStore.Get(task.Id);
    }
    
    // 步骤3: 提交remix modal with prompt
    var remixResult = await instance.RemixAsync(task, TaskAction.VIDEO_EXTEND, task.RemixModalMessageId, 
        "MJ::AnimateModal::prompt", convertedCustomId, extendPrompt, modalNonce, task.RealBotType ?? task.BotType);
}
```

### 3. CustomId 格式转换

Extend操作需要特殊的customId格式转换：

**原始格式** (用于action提交):
```
MJ::JOB::animate_high_extend::1::{jobId}::SOLO
```

**转换后格式** (用于modal提交):
```
MJ::AnimateModal::{jobId}::1::high::1
```

转换规则：
- 提取motion类型 (high/low/medium)
- 提取jobId (图片hash)
- 最后的参数：0表示普通video，1表示extend

### 4. 任务状态变化

| 阶段 | 状态 | 进度 | 说明 |
|------|------|------|------|
| 初始提交 | SUBMITTED | 0% | 用户提交extend请求 |
| Upscale进行中 | IN_PROGRESS | 1-49% | 正在放大视频 |
| Upscale完成 | SUBMITTED | 50% | 放大完成，准备extend |
| Extend提交 | SUBMITTED | 60% | Extend操作已提交 |
| Extend进行中 | IN_PROGRESS | 61-99% | 正在扩展视频 |
| 最终完成 | SUCCESS | 100% | 扩展完成，返回结果 |

## 错误处理

### 1. Upscale失败
如果upscale操作失败，任务会直接标记为FAILURE，不会继续执行extend操作。

### 2. Extend Action提交失败
```
FailReason: "Extend action提交失败: {error message}"
```

### 3. Modal消息超时
如果60秒内未收到modal消息，任务会失败：
```
FailReason: "等待modal消息超时"
```

### 4. Remix Modal提交失败
```
FailReason: "Extend modal提交失败: {error message}"
```

## 日志追踪

系统会记录详细的日志，便于追踪问题：

```
VIDEO_EXTEND任务upscale完成，开始自动提交extend操作: {TaskId}, CustomId: {CustomId}, Prompt: {Prompt}
提交extend action: TaskId={TaskId}, MessageId={MessageId}, CustomId={CustomId}
Extend action提交成功，等待modal消息: {TaskId}
收到modal消息: TaskId={TaskId}, RemixModalMessageId={RemixModalMessageId}
提交remix modal: TaskId={TaskId}, CustomId={CustomId}, Prompt={Prompt}
VIDEO_EXTEND的extend操作完整提交成功: {TaskId}
```

## 兼容性说明

### 1. Index参数兼容
系统同时支持0-3和1-4两种索引格式：
- 如果传入0-3，会自动转换为1-4
- 如果传入1-4，直接使用

### 2. Remix模式兼容
- 系统自动处理remix模式的modal弹窗
- 无需用户手动调用modal接口
- 自动提交用户指定的prompt

### 3. 向后兼容
- 原有的分步操作方式仍然支持
- 用户可以继续使用action接口逐步操作
- 也可以使用新的一步提交方式

## 测试建议

### 1. 基础功能测试
```bash
# 1. 首先创建一个video任务
POST /mj/submit/video
{
    "prompt": "一只蜜蜂在花丛中飞舞",
    "motion": "high"
}

# 2. 等待video任务完成，获取taskId

# 3. 提交extend请求
POST /mj/submit/video
{
    "prompt": "又飞来好几只蜜蜂",
    "motion": "high",
    "action": "extend",
    "index": 0,
    "taskId": "{上一步的taskId}"
}

# 4. 查询extend任务状态
GET /mj/task/{extendTaskId}/fetch
```

### 2. 边界情况测试
- 无效的taskId
- 未完成的源任务
- 无效的index (负数、超出范围)
- 空的prompt
- 不同的motion参数 (low/medium/high)

### 3. 并发测试
- 同时提交多个extend请求
- 验证队列管理是否正常
- 验证任务不会相互干扰

## 注意事项

1. **源任务必须完成**: 只能对状态为SUCCESS的video任务执行extend操作
2. **Index范围**: 必须在0-3或1-4范围内
3. **Prompt必填**: extend操作需要提供prompt参数
4. **Motion参数**: 建议与源video保持一致，但也可以使用不同的motion
5. **任务ID保存**: 返回的任务ID需要保存，用于后续查询结果

## 优势总结

✅ **简化操作流程**: 从3步操作简化为1步
✅ **自动化处理**: 系统自动处理upscale和extend的衔接
✅ **错误处理完善**: 每个步骤都有详细的错误处理和日志
✅ **状态透明**: 通过进度百分比可以清楚了解当前阶段
✅ **向后兼容**: 不影响现有的分步操作方式
✅ **易于使用**: API接口简单直观，参数清晰

## 更新日期

2025-01-08

## 相关文件

- `src/Midjourney.API/Controllers/SubmitController.cs` - API接口
- `src/Midjourney.Infrastructure/Services/TaskService.cs` - 任务服务
- `src/Midjourney.Infrastructure/Models/TaskInfo.cs` - 任务模型
- `src/Midjourney.Infrastructure/Handle/UserUpscaleSuccessHandler.cs` - Upscale完成处理器
- `src/Midjourney.Infrastructure/Dto/SubmitVideoDTO.cs` - 请求DTO

