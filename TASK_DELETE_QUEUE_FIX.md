# 删除任务时队列资源释放修复

## 问题描述

在后台删除任务时，特别是删除已提交（SUBMITTED）状态的任务，不会影响队列计数。例如：
- 当实例卡在 SUBMITTED 状态时，导致后面的任务都变成 NOT_STARTED
- 删除 SUBMITTED 任务后，队列计数没有恢复，后续任务仍然无法继续执行

## 根本原因

**删除任务时没有正确释放队列资源和信号量**：

1. **队列资源未释放**：
   - 如果任务还在队列中（`_queueTasks` 或 `_priorityQueueTasks`），删除时只是调用了 `Fail()`，但没有从队列中移除任务
   - 队列位置被占用，导致后续任务无法进入队列

2. **信号量未释放**：
   - 如果任务已经获取了信号量（状态为 SUBMITTED 或 IN_PROGRESS），删除时只是调用了 `Fail()`
   - 信号量释放只在 `ExecuteTaskAsync` 的 `finally` 块中进行，直接调用 `Fail()` 不会触发信号量释放
   - 信号量被占用，导致后续任务无法获取信号量执行

## 修复方案

### 1. 在 DiscordInstance 中添加 CancelTask 方法

**新增方法**：`CancelTask(string taskId, string reason = "删除任务")`

**功能**：
- 从队列中移除任务（如果还在队列中）
- 释放信号量（如果任务已经获取了信号量）
- 从运行任务列表中移除
- 从任务Future映射中移除
- 标记任务为失败

**关键逻辑**：
```csharp
// 1. 检查任务是否在运行中
var task = GetRunningTask(taskId);
if (task == null)
{
    // 2. 如果不在运行中，检查是否在队列中
    // 从优先队列或普通队列中查找并移除
    ExitTask(task);
    task.Fail(reason);
}
else
{
    // 3. 如果正在运行中，需要释放信号量
    task.Fail(reason);
    _runningTasks.TryRemove(taskId, out _);
    _taskFutureMap.TryRemove(taskId, out _);
    
    // 🔧 关键修复：释放信号量
    if (task.Status == TaskStatus.SUBMITTED || task.Status == TaskStatus.IN_PROGRESS)
    {
        _semaphoreSlimLock.Unlock();
    }
    
    ExitTask(task);
}
```

### 2. 修改 AdminController.TaskDelete 方法

**修改前**：
```csharp
var queueTask = _loadBalancer.GetQueueTasks().FirstOrDefault(t => t.Id == id);
if (queueTask != null)
{
    queueTask.Fail("删除任务");
    Thread.Sleep(1000);
}

var task = DbHelper.Instance.TaskStore.Get(id);
if (task != null)
{
    var ins = _loadBalancer.GetDiscordInstance(task.InstanceId);
    if (ins != null)
    {
        var model = ins.FindRunningTask(c => c.Id == id).FirstOrDefault();
        if (model != null)
        {
            model.Fail("删除任务");
            Thread.Sleep(1000);
        }
    }
    DbHelper.Instance.TaskStore.Delete(id);
}
```

**修改后**：
```csharp
var task = DbHelper.Instance.TaskStore.Get(id);
if (task != null)
{
    var ins = _loadBalancer.GetDiscordInstance(task.InstanceId);
    if (ins != null)
    {
        // 使用 CancelTask 方法取消任务，这会：
        // 1. 从队列中移除任务（如果还在队列中）
        // 2. 释放信号量（如果任务已经获取了信号量）
        // 3. 从运行任务列表中移除
        // 4. 标记任务为失败
        var cancelled = ins.CancelTask(id, "删除任务");
        if (cancelled)
        {
            Thread.Sleep(500);
        }
    }
    DbHelper.Instance.TaskStore.Delete(id);
}
```

## 修复效果

### 修复前的问题
1. **队列阻塞**：删除 SUBMITTED 任务后，队列位置仍然被占用，后续任务无法进入队列
2. **信号量泄漏**：删除 SUBMITTED 任务后，信号量没有被释放，后续任务无法获取信号量执行
3. **任务无法恢复**：即使删除了卡住的任务，后续任务仍然无法继续执行

### 修复后的改进
1. **队列资源正确释放**：删除任务时，从队列中移除任务，释放队列位置
2. **信号量正确释放**：删除任务时，如果任务已经获取了信号量，会正确释放信号量
3. **任务可以恢复**：删除卡住的任务后，后续任务可以正常进入队列并执行

## 关键改进点

### 1. 统一的资源释放逻辑
- **位置**：`DiscordInstance.CancelTask()` 方法
- **效果**：统一处理队列移除、信号量释放、任务状态更新等操作

### 2. 信号量释放检测
- **位置**：`DiscordInstance.CancelTask()` 方法第 666 行
- **逻辑**：检查任务状态（SUBMITTED 或 IN_PROGRESS）来判断是否获取了信号量
- **效果**：确保已获取信号量的任务在删除时正确释放信号量

### 3. 队列移除逻辑
- **位置**：`DiscordInstance.CancelTask()` 方法使用 `ExitTask()` 方法
- **效果**：从优先队列和普通队列中正确移除任务

### 4. 增强的错误处理
- **位置**：`DiscordInstance.CancelTask()` 方法
- **效果**：处理任务不存在、任务在队列中、任务在运行中等各种情况

## 使用场景

### 场景 1：删除队列中的任务
- 任务还在队列中等待执行
- 删除时会从队列中移除，释放队列位置

### 场景 2：删除 SUBMITTED 状态的任务
- 任务已经获取了信号量，正在等待 Discord 响应
- 删除时会释放信号量，允许后续任务执行

### 场景 3：删除 IN_PROGRESS 状态的任务
- 任务正在执行中
- 删除时会释放信号量，允许后续任务执行

## 验证步骤

### 1. 测试删除队列中的任务
1. 提交多个任务，让一些任务进入队列
2. 删除队列中的任务
3. 验证后续任务可以正常进入队列并执行

### 2. 测试删除 SUBMITTED 状态的任务
1. 提交任务，让任务进入 SUBMITTED 状态
2. 删除 SUBMITTED 状态的任务
3. 验证信号量被释放，后续任务可以正常执行

### 3. 测试删除卡住的任务
1. 模拟任务卡在 SUBMITTED 状态（网络问题等）
2. 删除卡住的任务
3. 验证队列计数恢复，后续任务可以正常执行

### 4. 检查日志
查看日志中是否有以下信息：
- `"已从队列中取消任务 {taskId}, 原因: 删除任务"`
- `"已释放信号量，任务 {taskId} 已取消, 剩余运行任务数: {count}, 可用信号量: {count}"`
- `"任务 {taskId} 已成功取消并释放所有资源"`

## 注意事项

### 1. 信号量释放时机
- 只有在任务状态为 SUBMITTED 或 IN_PROGRESS 时才释放信号量
- 如果任务还在队列中，不需要释放信号量（因为还没有获取）

### 2. 队列移除顺序
- 先检查任务是否在运行中
- 如果不在运行中，再检查是否在队列中
- 使用 `ExitTask()` 方法统一处理队列移除

### 3. 任务状态更新
- 删除任务时，任务状态会被标记为 FAILURE
- 失败原因设置为 "删除任务"

### 4. 异步任务处理
- 从 `_taskFutureMap` 中移除任务，但不等待任务完成
- 这样可以快速释放资源，避免阻塞

## 总结

通过添加 `CancelTask` 方法和修改 `TaskDelete` 方法，我们解决了以下问题：
1. **队列资源未释放**：删除任务时正确从队列中移除任务
2. **信号量未释放**：删除任务时正确释放已获取的信号量
3. **任务无法恢复**：删除卡住的任务后，后续任务可以正常执行

修复后，删除任务时会正确释放所有相关资源（队列位置、信号量等），确保后续任务可以正常执行。

