// Midjourney Proxy - Proxy for Midjourney's Discord, enabling AI drawings via API with one-click face swap. A free, non-profit drawing API project.
// Copyright (C) 2024 trueai.org

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

// Additional Terms:
// This software shall not be used for any illegal activities. 
// Users must comply with all applicable laws and regulations,
// particularly those related to image and video processing. 
// The use of this software for any form of illegal face swapping,
// invasion of privacy, or any other unlawful purposes is strictly prohibited. 
// Violation of these terms may result in termination of the license and may subject the violator to legal action.
using Midjourney.Infrastructure.Data;
using Midjourney.Infrastructure.Dto;
using Midjourney.Infrastructure.LoadBalancer;
using Midjourney.Infrastructure.Util;
using Serilog;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Midjourney.Infrastructure.Handle
{
    /// <summary>
    /// 用户放大成功处理程序
    /// </summary>
    public class UserUpscaleSuccessHandler : UserMessageHandler
    {
        private const string CONTENT_REGEX_1 = "\\*\\*(.*)\\*\\* - Upscaled \\(.*?\\) by <@\\d+> \\((.*?)\\)";
        private const string CONTENT_REGEX_2 = "\\*\\*(.*)\\*\\* - Upscaled by <@\\d+> \\((.*?)\\)";

        private const string CONTENT_REGEX_U = "\\*\\*(.*)\\*\\* - Image #(\\d) <@\\d+>";
        
        // 视频upscale完成的消息格式（不包含"Upscaled"关键字）
        private const string CONTENT_REGEX_VIDEO = "\\*\\*(.*)\\*\\* - <@\\d+> \\((.*?)\\)";

        public UserUpscaleSuccessHandler(DiscordLoadBalancer discordLoadBalancer, DiscordHelper discordHelper)
            : base(discordLoadBalancer, discordHelper)
        {
        }

        public override void Handle(DiscordInstance instance, MessageType messageType, EventData message)
        {
            // 跳过 Waiting to start 消息
            if (!string.IsNullOrWhiteSpace(message.Content) && message.Content.Contains("(Waiting to start)"))
            {
                return;
            }

            // 判断消息是否处理过了
            CacheHelper<string, bool>.TryAdd(message.Id.ToString(), false);
            if (CacheHelper<string, bool>.Get(message.Id.ToString()))
            {
                Log.Debug("USER 消息已经处理过了 {@0}", message.Id);
                return;
            }

            string content = GetMessageContent(message);
            var parseData = GetParseData(content);
            
            Log.Debug("UserUpscaleSuccessHandler处理消息: Type={Type}, HasImage={HasImage}, Content={Content}", 
                messageType, HasImage(message), content?.Substring(0, Math.Min(100, content?.Length ?? 0)));
            
            if (messageType == MessageType.CREATE && parseData != null && HasImage(message))
            {
                if (parseData is UContentParseData uContentParseData)
                {
                    FindAndFinishUTask(instance, uContentParseData.Prompt, uContentParseData.Index, message);
                }
                else
                {
                    FindAndFinishImageTask(instance, TaskAction.UPSCALE, parseData.Prompt, message);
                }
            }
            // 检查是否为视频upscale完成消息（用于VIDEO_EXTEND）
            else if (messageType == MessageType.CREATE && HasImage(message))
            {
                Log.Information("🔍 检查是否为视频upscale消息: Content={Content}", content);
                var videoParseData = GetVideoUpscaleParseData(content);
                if (videoParseData != null)
                {
                    Log.Information("✅ 检测到视频upscale完成消息，开始处理: {Content}", content);
                    FindAndFinishVideoUpscaleTask(instance, videoParseData.Prompt, message);
                }
                else
                {
                    Log.Debug("❌ 不是视频upscale消息，正则不匹配");
                }
            }
        }
        
        /// <summary>
        /// 解析视频upscale完成消息
        /// </summary>
        private static ContentParseData GetVideoUpscaleParseData(string content)
        {
            var matcher = Regex.Match(content, CONTENT_REGEX_VIDEO);
            if (!matcher.Success)
            {
                return null;
            }

            return new ContentParseData
            {
                Prompt = matcher.Groups[1].Value,
                Status = matcher.Groups[2].Value
            };
        }
        
        /// <summary>
        /// 查找并完成视频upscale任务
        /// </summary>
        private void FindAndFinishVideoUpscaleTask(DiscordInstance instance, string finalPrompt, EventData message)
        {
            string imageUrl = GetImageUrl(message);
            string messageHash = discordHelper.GetMessageHash(imageUrl);

            var msgId = GetMessageId(message);
            
            // 通过InteractionMetadataId匹配VIDEO_EXTEND任务
            var task = instance.FindRunningTask(c => 
                (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) && 
                c.Action == TaskAction.VIDEO_EXTEND &&
                c.InteractionMetadataId == message.InteractionMetadata?.Id).FirstOrDefault();

            if (task == null || task.Status == TaskStatus.SUCCESS || task.Status == TaskStatus.FAILURE)
            {
                return;
            }

            task.MessageId = msgId;

            if (!task.MessageIds.Contains(msgId))
                task.MessageIds.Add(msgId);

            task.SetProperty(Constants.MJ_MESSAGE_HANDLED, true);
            task.SetProperty(Constants.TASK_PROPERTY_FINAL_PROMPT, finalPrompt);
            task.SetProperty(Constants.TASK_PROPERTY_MESSAGE_HASH, messageHash);
            task.SetProperty(Constants.TASK_PROPERTY_MESSAGE_CONTENT, message.Content);

            task.ImageUrl = imageUrl;
            task.JobId = messageHash;

            FinishTask(task, message);
            task.Awake();
            
            // 在FinishTask之后检查是否为VIDEO_EXTEND任务（此时Buttons已经设置好了）
            if (task.Action == TaskAction.VIDEO_EXTEND && 
                !string.IsNullOrWhiteSpace(task.GetProperty<string>("EXTEND_PROMPT", default)) &&
                task.GetProperty<string>("EXTEND_UPSCALE_COMPLETED", default) != "true")
            {
                Log.Information("🎬 检测到VIDEO_EXTEND任务upscale完成(User消息)，准备自动提交extend: TaskId={TaskId}, Action={Action}, ButtonsCount={Count}", 
                    task.Id, task.Action, task.Buttons?.Count ?? 0);
                
                // 标记已处理，防止重复处理
                task.SetProperty("EXTEND_UPSCALE_COMPLETED", "true");
                
                // 重置状态和进度（从SUCCESS改回SUBMITTED）
                task.Status = TaskStatus.SUBMITTED;
                task.Progress = "50%";
                task.Description = "Upscale完成，正在进行extend操作...";
                
                // 保存状态变更
                DbHelper.Instance.TaskStore.Update(task);
                
                AutoSubmitVideoExtend(instance, task);
            }
        }

        /// <summary>
        /// 注意处理混图放大的情况，混图放大是没有提示词的
        /// </summary>
        /// <param name="instance"></param>
        /// <param name="finalPrompt"></param>
        /// <param name="index"></param>
        /// <param name="message"></param>
        private void FindAndFinishUTask(DiscordInstance instance, string finalPrompt, int index, EventData message)
        {
            string imageUrl = GetImageUrl(message);
            string messageHash = discordHelper.GetMessageHash(imageUrl);

            var msgId = GetMessageId(message);
            var fullPrompt = GetFullPrompt(message);

            var task = instance.FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) && c.MessageId == msgId).FirstOrDefault();

            if (task == null && message.InteractionMetadata?.Id != null)
            {
                task = instance.FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) && c.InteractionMetadataId == message.InteractionMetadata.Id.ToString()).FirstOrDefault();

                // 如果通过 meta id 找到任务，但是 full prompt 为空，则更新 full prompt
                if (task != null && string.IsNullOrWhiteSpace(task.PromptFull))
                {
                    task.PromptFull = fullPrompt;
                }
            }

            // 如果依然找不到任务，可能是 NIJI 任务
            // 不判断 && botType == EBotType.NIJI_JOURNEY
            var botType = GetBotType(message);

            if (task == null)
            {
                if (!string.IsNullOrWhiteSpace(fullPrompt))
                {
                    task = instance.FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) && (c.BotType == botType || c.RealBotType == botType) && c.PromptFull == fullPrompt)
                    .OrderBy(c => c.StartTime).FirstOrDefault();
                }
            }


            if (task == null)
            {
                var prompt = finalPrompt.FormatPrompt();

                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    task = instance
                        .FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
                        (c.BotType == botType || c.RealBotType == botType) && !string.IsNullOrWhiteSpace(c.PromptEn)
                        && (c.PromptEn.FormatPrompt() == prompt || c.PromptEn.FormatPrompt().EndsWith(prompt) || prompt.StartsWith(c.PromptEn.FormatPrompt())))
                        .OrderBy(c => c.StartTime).FirstOrDefault();
                }

                // 有可能为 kong blend 时
                //else
                //{
                //    // 放大时，提示词不可为空
                //    return;
                //}
            }

            // 如果依然找不到任务，保留 prompt link 进行匹配
            if (task == null)
            {
                var prompt = finalPrompt.FormatPromptParam();
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    task = instance
                            .FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
                            (c.BotType == botType || c.RealBotType == botType) && !string.IsNullOrWhiteSpace(c.PromptEn)
                            && (c.PromptEn.FormatPromptParam() == prompt || c.PromptEn.FormatPromptParam().EndsWith(prompt) || prompt.StartsWith(c.PromptEn.FormatPromptParam())))
                            .OrderBy(c => c.StartTime).FirstOrDefault();
                }
            }

            if (task == null || task.Status == TaskStatus.SUCCESS || task.Status == TaskStatus.FAILURE)
            {
                return;
            }

            task.MessageId = msgId;

            if (!task.MessageIds.Contains(msgId))
                task.MessageIds.Add(msgId);

            task.SetProperty(Constants.MJ_MESSAGE_HANDLED, true);
            task.SetProperty(Constants.TASK_PROPERTY_FINAL_PROMPT, finalPrompt);
            task.SetProperty(Constants.TASK_PROPERTY_MESSAGE_HASH, messageHash);
            task.SetProperty(Constants.TASK_PROPERTY_MESSAGE_CONTENT, message.Content);

            task.ImageUrl = imageUrl;
            task.JobId = messageHash;

            FinishTask(task, message);
            task.Awake();
            
            // 在FinishTask之后检查是否为VIDEO_EXTEND任务（此时Buttons已经设置好了）
            if (task.Action == TaskAction.VIDEO_EXTEND && 
                !string.IsNullOrWhiteSpace(task.GetProperty<string>("EXTEND_PROMPT", default)) &&
                task.GetProperty<string>("EXTEND_UPSCALE_COMPLETED", default) != "true")
            {
                Log.Information("🎬 检测到VIDEO_EXTEND任务upscale完成(User消息)，准备自动提交extend: TaskId={TaskId}, Action={Action}, ButtonsCount={Count}", 
                    task.Id, task.Action, task.Buttons?.Count ?? 0);
                
                // 标记已处理，防止重复处理
                task.SetProperty("EXTEND_UPSCALE_COMPLETED", "true");
                
                // 重置状态和进度（从SUCCESS改回SUBMITTED）
                task.Status = TaskStatus.SUBMITTED;
                task.Progress = "50%";
                task.Description = "Upscale完成，正在进行extend操作...";
                
                // 保存状态变更
                DbHelper.Instance.TaskStore.Update(task);
                
                AutoSubmitVideoExtend(instance, task);
            }
        }
        
        /// <summary>
        /// 自动提交VIDEO_EXTEND的extend操作
        /// </summary>
        private void AutoSubmitVideoExtend(DiscordInstance instance, TaskInfo task)
        {
            Log.Information("🚀 AutoSubmitVideoExtend方法被调用: TaskId={TaskId}", task.Id);
            
            try
            {
                var extendPrompt = task.GetProperty<string>("EXTEND_PROMPT", default);
                var extendMotion = task.GetProperty<string>("EXTEND_MOTION", default);
                
                Log.Information("📋 Extend参数: Prompt={Prompt}, Motion={Motion}, ButtonsCount={Count}", 
                    extendPrompt, extendMotion, task.Buttons?.Count ?? 0);
                
                // 从Buttons中查找正确的extend customId，而不是自己构建
                // 因为upscale后的JobId可能不是正确的hash值
                var extendButton = task.Buttons?.FirstOrDefault(x => 
                    x.CustomId?.Contains($"animate_{extendMotion}_extend") == true);
                
                if (extendButton == null || string.IsNullOrWhiteSpace(extendButton.CustomId))
                {
                    Log.Warning("❌ VIDEO_EXTEND任务找不到extend按钮: {TaskId}, Motion: {Motion}, Buttons: {@Buttons}", 
                        task.Id, extendMotion, task.Buttons);
                    task.Status = TaskStatus.FAILURE;
                    task.FailReason = $"找不到extend按钮 (motion: {extendMotion})";
                    DbHelper.Instance.TaskStore.Update(task);
                    return;
                }
                
                var customId = extendButton.CustomId;
                
                Log.Information("✅ 找到extend按钮，开始自动提交extend操作: {TaskId}, CustomId: {CustomId}, Prompt: {Prompt}", 
                    task.Id, customId, extendPrompt);
                
                // 更新任务状态和进度
                task.Status = TaskStatus.SUBMITTED;
                task.Progress = "50%";
                task.Description = $"Upscale完成，正在进行extend操作...";
                task.PromptEn = extendPrompt;  // 设置extend的prompt
                
                // 存储extend相关信息
                task.SetProperty(Constants.TASK_PROPERTY_CUSTOM_ID, customId);
                task.SetProperty(Constants.TASK_PROPERTY_REMIX_MODAL, "MJ::AnimateModal::prompt");
                task.SetProperty(Constants.TASK_PROPERTY_REMIX_CUSTOM_ID, customId);
                task.SetProperty("EXTEND_UPSCALE_COMPLETED", "true");
                task.RemixAutoSubmit = true;  // 标记为自动提交
                task.RemixModaling = false;   // 初始化modal状态
                
                // 保存任务状态
                DbHelper.Instance.TaskStore.Update(task);
                
                // 异步提交extend操作
                Task.Run(async () =>
                {
                    try
                    {
                        // 等待一小段时间确保消息已完全处理
                        await Task.Delay(2000);
                        
                        // 获取消息flags
                        var messageFlags = task.GetProperty<string>(Constants.TASK_PROPERTY_FLAGS, default)?.ToInt() ?? 0;
                        var nonce = SnowFlake.NextId();
                        
                        // 更新任务的nonce，用于后续消息匹配
                        task.Nonce = nonce;
                        task.SetProperty(Constants.TASK_PROPERTY_NONCE, nonce);
                        
                        Log.Information("📤 步骤1: 提交extend action");
                        Log.Information("  TaskId={TaskId}", task.Id);
                        Log.Information("  MessageId={MessageId}", task.MessageId);
                        Log.Information("  CustomId={CustomId}", customId);
                        Log.Information("  MessageFlags={Flags}", messageFlags);
                        Log.Information("  Nonce={Nonce}", nonce);
                        
                        // 步骤1: 提交action，触发modal弹窗
                        task.RemixModaling = true;
                        DbHelper.Instance.TaskStore.Update(task);
                        
                        var actionResult = await instance.ActionAsync(task.MessageId, customId, messageFlags, nonce, task);
                        
                        Log.Information("📥 步骤1响应: Code={Code}, Description={Description}", 
                            actionResult.Code, actionResult.Description);
                        
                        if (actionResult.Code != ReturnCode.SUCCESS)
                        {
                            Log.Warning("VIDEO_EXTEND的extend action提交失败: {TaskId}, Error: {Error}", task.Id, actionResult.Description);
                            task.Status = TaskStatus.FAILURE;
                            task.FailReason = $"Extend action提交失败: {actionResult.Description}";
                            DbHelper.Instance.TaskStore.Update(task);
                            return;
                        }
                        
                        // 检查账号是否开启了Remix模式
                        var account = instance.Account;
                        var isRemixOn = (task.RealBotType ?? task.BotType) == EBotType.MID_JOURNEY ? account.MjRemixOn : account.NijiRemixOn;
                        
                        if (!isRemixOn)
                        {
                            // Remix未开启，extend操作会直接执行，不需要提交modal
                            Log.Information("✅ Remix未开启，extend操作已直接开始执行: {TaskId}", task.Id);
                            task.Description = "Extend操作已开始执行...";
                            task.Progress = "60%";
                            task.RemixModaling = false;
                            DbHelper.Instance.TaskStore.Update(task);
                            
                            // 任务会通过正常的进度更新机制继续跟踪
                            return;
                        }
                        
                        Log.Information("Extend action提交成功，Remix已开启，等待modal消息: {TaskId}", task.Id);
                        
                        // 步骤2: 等待获取modal的messageId和interactionMetadataId
                        var sw = new Stopwatch();
                        sw.Start();
                        while (string.IsNullOrWhiteSpace(task.RemixModalMessageId) || string.IsNullOrWhiteSpace(task.InteractionMetadataId))
                        {
                            if (sw.ElapsedMilliseconds > 60000) // 60秒超时
                            {
                                Log.Warning("等待modal消息超时: {TaskId}", task.Id);
                                task.Status = TaskStatus.FAILURE;
                                task.FailReason = "等待modal消息超时";
                                DbHelper.Instance.TaskStore.Update(task);
                                return;
                            }
                            
                            await Task.Delay(1000);
                            task = DbHelper.Instance.TaskStore.Get(task.Id);  // 重新加载任务状态
                        }
                        
                        Log.Information("收到modal消息: TaskId={TaskId}, RemixModalMessageId={RemixModalMessageId}", 
                            task.Id, task.RemixModalMessageId);
                        
                        // 步骤3: 等待一小段时间后提交remix modal
                        await Task.Delay(1500);
                        task.RemixModaling = false;
                        DbHelper.Instance.TaskStore.Update(task);
                        
                        // 转换customId格式用于modal提交
                        // MJ::JOB::animate_high_extend::1::b8803f08-fc00-43e6-97a8-bade18e41231::SOLO 
                        // -> MJ::AnimateModal::b8803f08-fc00-43e6-97a8-bade18e41231::1::high::1
                        var parts = customId.Split("::");
                        var animateType = parts[2].Replace("animate_", "").Replace("_extend", "");
                        var hash = parts[4];  // 使用customId中的hash，而不是task.JobId
                        var convertedCustomId = $"MJ::AnimateModal::{hash}::{parts[3]}::{animateType}::1";  // 最后的1表示extend
                        
                        task.SetProperty(Constants.TASK_PROPERTY_REMIX_CUSTOM_ID, convertedCustomId);
                        DbHelper.Instance.TaskStore.Update(task);
                        
                        var modalNonce = SnowFlake.NextId();
                        task.Nonce = modalNonce;
                        task.SetProperty(Constants.TASK_PROPERTY_NONCE, modalNonce);
                        
                        Log.Information("📤 步骤2: 提交remix modal");
                        Log.Information("  TaskId={TaskId}", task.Id);
                        Log.Information("  Action=VIDEO_EXTEND");
                        Log.Information("  RemixModalMessageId={RemixModalMessageId}", task.RemixModalMessageId);
                        Log.Information("  Modal=MJ::AnimateModal::prompt");
                        Log.Information("  ConvertedCustomId={CustomId}", convertedCustomId);
                        Log.Information("  Prompt={Prompt}", extendPrompt);
                        Log.Information("  ModalNonce={Nonce}", modalNonce);
                        Log.Information("  BotType={BotType}", task.RealBotType ?? task.BotType);
                        
                        var remixResult = await instance.RemixAsync(task, TaskAction.VIDEO_EXTEND, task.RemixModalMessageId, 
                            "MJ::AnimateModal::prompt", convertedCustomId, extendPrompt, modalNonce, task.RealBotType ?? task.BotType);
                        
                        Log.Information("📥 步骤2响应: Code={Code}, Description={Description}", 
                            remixResult.Code, remixResult.Description);
                        
                        if (remixResult.Code == ReturnCode.SUCCESS || remixResult.Code == ReturnCode.IN_QUEUE)
                        {
                            Log.Information("VIDEO_EXTEND的extend操作完整提交成功: {TaskId}", task.Id);
                            task.Description = "Extend操作已提交，等待MJ处理...";
                            task.Progress = "60%";
                        }
                        else
                        {
                            Log.Warning("VIDEO_EXTEND的remix modal提交失败: {TaskId}, Error: {Error}", task.Id, remixResult.Description);
                            task.Status = TaskStatus.FAILURE;
                            task.FailReason = $"Extend modal提交失败: {remixResult.Description}";
                        }
                        
                        DbHelper.Instance.TaskStore.Update(task);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "VIDEO_EXTEND自动提交extend操作时发生异常: {TaskId}", task.Id);
                        task.Status = TaskStatus.FAILURE;
                        task.FailReason = $"Extend操作提交异常: {ex.Message}";
                        DbHelper.Instance.TaskStore.Update(task);
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "准备VIDEO_EXTEND自动提交时发生异常: {TaskId}", task.Id);
            }
        }

        public static ContentParseData GetParseData(string content)
        {
            var parseData = ConvertUtils.ParseContent(content, CONTENT_REGEX_1)
                ?? ConvertUtils.ParseContent(content, CONTENT_REGEX_2);
            if (parseData != null)
            {
                return parseData;
            }

            var matcher = Regex.Match(content, CONTENT_REGEX_U);
            if (!matcher.Success)
            {
                return null;
            }

            var uContentParseData = new UContentParseData
            {
                Prompt = matcher.Groups[1].Value,
                Index = int.Parse(matcher.Groups[2].Value),
                Status = "done"
            };
            return uContentParseData;
        }

        public class UContentParseData : ContentParseData
        {
            public int Index { get; set; }
        }
    }
}