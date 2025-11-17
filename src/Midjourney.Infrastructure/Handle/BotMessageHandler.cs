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
using Discord;
using Discord.WebSocket;
using Midjourney.Infrastructure.Data;
using Midjourney.Infrastructure.LoadBalancer;
using Midjourney.Infrastructure.Util;
using Serilog;

namespace Midjourney.Infrastructure.Handle
{
    /// <summary>
    /// 机器人消息事件处理器
    /// </summary>
    public abstract class BotMessageHandler
    {
        protected DiscordLoadBalancer discordLoadBalancer;
        protected DiscordHelper discordHelper;

        public BotMessageHandler(DiscordLoadBalancer discordLoadBalancer, DiscordHelper discordHelper)
        {
            this.discordLoadBalancer = discordLoadBalancer;
            this.discordHelper = discordHelper;
        }

        public abstract void Handle(DiscordInstance instance, MessageType messageType, SocketMessage message);

        public virtual int Order() => 100;

        protected string GetMessageContent(SocketMessage message)
        {
            return message.Content;
        }

        protected string GetFullPrompt(SocketMessage message)
        {
            return ConvertUtils.GetFullPrompt(message.Content);
        }

        protected string GetMessageId(SocketMessage message)
        {
            return message.Id.ToString();
        }

        protected string GetInteractionName(SocketMessage message)
        {
            return message?.Interaction?.Name ?? string.Empty;
        }

        protected string GetReferenceMessageId(SocketMessage message)
        {
            return message?.Reference?.MessageId.ToString() ?? string.Empty;
        }

        protected EBotType? GetBotType(SocketMessage message)
        {
            var botId = message.Author?.Id.ToString();
            EBotType? botType = null;

            if (botId == Constants.NIJI_APPLICATION_ID)
            {
                botType = EBotType.NIJI_JOURNEY;
            }
            else if (botId == Constants.MJ_APPLICATION_ID)
            {
                botType = EBotType.MID_JOURNEY;
            }

            return botType;
        }

        protected void FindAndFinishImageTask(DiscordInstance instance, TaskAction action, string finalPrompt, SocketMessage message)
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
                Log.Debug("BOT 消息已经处理过了 {@0}", message.Id);
                return;
            }

            if (string.IsNullOrWhiteSpace(finalPrompt))
                return;

            var msgId = GetMessageId(message);
            var fullPrompt = GetFullPrompt(message);

            string imageUrl = GetImageUrl(message);
            string messageHash = discordHelper.GetMessageHash(imageUrl);

            // 优先级1: 通过MessageId匹配
            var task = instance.FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) && c.MessageId == msgId).FirstOrDefault();

            // 优先级2: 通过InteractionMetadataId匹配
            if (task == null && message is SocketUserMessage umsg && umsg != null && umsg.InteractionMetadata?.Id != null)
            {
                task = instance.FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) && c.InteractionMetadataId == umsg.InteractionMetadata.Id.ToString()).FirstOrDefault();

                // 如果通过 meta id 找到任务，但是 full prompt 为空，则更新 full prompt
                if (task != null && string.IsNullOrWhiteSpace(task.PromptFull))
                {
                    task.PromptFull = fullPrompt;
                }
            }

            var botType = GetBotType(message);

            // 优先级3: 通过PromptFull匹配（智能匹配策略：减少串任务风险）
            if (task == null)
            {
                if (!string.IsNullOrWhiteSpace(fullPrompt))
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var timeWindow = 10 * 60 * 1000; // 10分钟时间窗口
                    var minSubmitTime = now - timeWindow;

                    // 智能匹配策略：优先匹配 SUBMITTED 状态 + 时间窗口内的任务
                    var candidateTasks = instance
                        .FindRunningTask(c => 
                            (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) 
                            && (c.BotType == botType || c.RealBotType == botType) 
                            && c.PromptFull == fullPrompt
                            && c.SubmitTime.HasValue 
                            && c.SubmitTime.Value >= minSubmitTime) // 只匹配最近10分钟内的任务
                        .OrderByDescending(c => c.Status == TaskStatus.SUBMITTED ? 1 : 0) // 优先 SUBMITTED 状态
                        .ThenByDescending(c => c.SubmitTime ?? 0) // 然后按提交时间降序（最近提交的优先）
                        .ToList();
                    
                    if (candidateTasks.Count > 0)
                    {
                        task = candidateTasks.First();
                        if (candidateTasks.Count > 1)
                        {
                            Log.Warning("BOT PromptFull匹配发现多个相同提示词的任务, Count: {Count}, MessageId: {MessageId}, 选择最近提交的任务: {TaskId} (SubmitTime: {SubmitTime})", 
                                candidateTasks.Count, msgId, task.Id, task.SubmitTime?.ToDateTimeString() ?? "N/A");
                        }
                    }
                }
            }

            // 优先级4: 通过FormatPrompt匹配（智能匹配策略：减少串任务风险）
            if (task == null)
            {
                var prompt = finalPrompt.FormatPrompt();

                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var timeWindow = 10 * 60 * 1000; // 10分钟时间窗口
                    var minSubmitTime = now - timeWindow;

                    var candidateTasks = instance
                        .FindRunningTask(c =>
                        (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED)
                        && (c.BotType == botType || c.RealBotType == botType)
                        && !string.IsNullOrWhiteSpace(c.PromptEn)
                        && c.SubmitTime.HasValue 
                        && c.SubmitTime.Value >= minSubmitTime // 只匹配最近10分钟内的任务
                        && (c.PromptEn.FormatPrompt() == prompt || c.PromptEn.FormatPrompt().EndsWith(prompt) || prompt.StartsWith(c.PromptEn.FormatPrompt())))
                        .OrderByDescending(c => c.Status == TaskStatus.SUBMITTED ? 1 : 0) // 优先 SUBMITTED 状态
                        .ThenByDescending(c => c.SubmitTime ?? 0) // 然后按提交时间降序（最近提交的优先）
                        .ToList();
                    
                    if (candidateTasks.Count > 0)
                    {
                        task = candidateTasks.First();
                        if (candidateTasks.Count > 1)
                        {
                            Log.Warning("BOT FormatPrompt匹配发现多个相同提示词的任务, Count: {Count}, MessageId: {MessageId}, Prompt: {Prompt}, 选择最近提交的任务: {TaskId} (SubmitTime: {SubmitTime})", 
                                candidateTasks.Count, msgId, prompt.Substring(0, Math.Min(50, prompt.Length)), task.Id, task.SubmitTime?.ToDateTimeString() ?? "N/A");
                        }
                    }
                }
                else
                {
                    // 如果最终提示词为空，则可能是重绘、混图等任务（智能匹配策略）
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var timeWindow = 10 * 60 * 1000; // 10分钟时间窗口
                    var minSubmitTime = now - timeWindow;

                    task = instance
                        .FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
                        (c.BotType == botType || c.RealBotType == botType) 
                        && c.Action == action
                        && c.SubmitTime.HasValue 
                        && c.SubmitTime.Value >= minSubmitTime) // 只匹配最近10分钟内的任务
                        .OrderByDescending(c => c.Status == TaskStatus.SUBMITTED ? 1 : 0) // 优先 SUBMITTED 状态
                        .ThenByDescending(c => c.SubmitTime ?? 0) // 然后按提交时间降序
                        .FirstOrDefault();
                }
            }

            // 优先级5: 通过FormatPromptParam匹配（智能匹配策略：减少串任务风险）
            if (task == null)
            {
                var prompt = finalPrompt.FormatPromptParam();
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var timeWindow = 10 * 60 * 1000; // 10分钟时间窗口
                    var minSubmitTime = now - timeWindow;

                    var candidateTasks = instance
                            .FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
                            (c.BotType == botType || c.RealBotType == botType) 
                            && !string.IsNullOrWhiteSpace(c.PromptEn)
                            && c.SubmitTime.HasValue 
                            && c.SubmitTime.Value >= minSubmitTime // 只匹配最近10分钟内的任务
                            && (c.PromptEn.FormatPromptParam() == prompt || c.PromptEn.FormatPromptParam().EndsWith(prompt) || prompt.StartsWith(c.PromptEn.FormatPromptParam())))
                            .OrderByDescending(c => c.Status == TaskStatus.SUBMITTED ? 1 : 0) // 优先 SUBMITTED 状态
                            .ThenByDescending(c => c.SubmitTime ?? 0) // 然后按提交时间降序（最近提交的优先）
                            .ToList();
                    
                    if (candidateTasks.Count > 0)
                    {
                        task = candidateTasks.First();
                        if (candidateTasks.Count > 1)
                        {
                            Log.Warning("BOT FormatPromptParam匹配发现多个相同提示词的任务, Count: {Count}, MessageId: {MessageId}, Prompt: {Prompt}, 选择最近提交的任务: {TaskId} (SubmitTime: {SubmitTime})", 
                                candidateTasks.Count, msgId, prompt.Substring(0, Math.Min(50, prompt.Length)), task.Id, task.SubmitTime?.ToDateTimeString() ?? "N/A");
                        }
                    }
                }
            }

            // 优先级6: 特殊任务类型匹配（SHOW任务）
            if (task == null && action == TaskAction.SHOW)
            {
                task = instance.FindRunningTask(c => (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
                (c.BotType == botType || c.RealBotType == botType) && c.Action == TaskAction.SHOW && c.JobId == messageHash).OrderBy(c => c.StartTime).FirstOrDefault();
            }

            // 优先级7: 改进的空prompt匹配逻辑
            if (task == null)
            {
                // 对于特定的任务类型，当prompt为空时提供更精确的匹配
                if (action == TaskAction.VIDEO || action == TaskAction.VIDEO_EXTEND ||
                    action == TaskAction.BLEND || action == TaskAction.DESCRIBE ||
                    action == TaskAction.ACTION)
                {
                    // 首先尝试通过imageUrl匹配，如果任务的prompt包含相同的URL
                    if (!string.IsNullOrWhiteSpace(imageUrl))
                    {
                        task = instance.FindRunningTask(c => 
                            (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
                            (c.BotType == botType || c.RealBotType == botType) && 
                            c.Action == action &&
                            !string.IsNullOrWhiteSpace(c.PromptEn) && c.PromptEn.Contains(imageUrl))
                            .OrderBy(c => c.StartTime).FirstOrDefault();
                    }

                    // 如果通过URL匹配失败，尝试通过messageHash匹配
                    if (task == null && !string.IsNullOrWhiteSpace(messageHash))
                    {
                        task = instance.FindRunningTask(c => 
                            (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
                            (c.BotType == botType || c.RealBotType == botType) && 
                            c.Action == action &&
                            (c.JobId == messageHash || c.MessageId == messageHash))
                            .OrderBy(c => c.StartTime).FirstOrDefault();
                    }

                    // 最后才使用原有的模糊匹配，但严格验证prompt匹配，避免串任务
                    if (task == null)
                    {
                        // 缩短时间窗口到1分钟，减少误匹配概率
                        var cutoffTime = DateTimeOffset.Now.AddMinutes(-1).ToUnixTimeMilliseconds();
                        var candidateTasks = instance.FindRunningTask(c => 
                            (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
                            (c.BotType == botType || c.RealBotType == botType) && 
                            c.Action == action &&
                            c.StartTime >= cutoffTime)
                            .OrderBy(c => c.StartTime)
                            .ToList();
                        
                        // 严格验证：即使只有一个候选，也要验证prompt是否匹配
                        if (candidateTasks.Count > 0)
                        {
                            // 如果消息中有prompt，必须验证候选任务的prompt是否匹配
                            if (!string.IsNullOrWhiteSpace(finalPrompt))
                            {
                                var matchedTask = candidateTasks.FirstOrDefault(c => 
                                    !string.IsNullOrWhiteSpace(c.PromptEn) && 
                                    (c.PromptEn == finalPrompt || 
                                     c.PromptEn.FormatPrompt() == finalPrompt.FormatPrompt() ||
                                     c.PromptEn.FormatPromptParam() == finalPrompt.FormatPromptParam()));
                                
                                if (matchedTask != null)
                                {
                                    task = matchedTask;
                                    if (candidateTasks.Count > 1)
                                    {
                                        Log.Warning("BOT 使用模糊匹配找到任务（已验证prompt匹配）, TaskId: {TaskId}, Action: {Action}, CandidateCount: {Count}", 
                                            task.Id, action, candidateTasks.Count);
                                    }
                                }
                                else
                                {
                                    // Prompt不匹配，拒绝匹配，避免串任务
                                    Log.Error("BOT 模糊匹配失败：候选任务的prompt与消息prompt不匹配, MessagePrompt: {MessagePrompt}, CandidateCount: {Count}, Action: {Action}, MessageId: {MessageId}, 拒绝匹配以避免串任务！", 
                                        finalPrompt.Substring(0, Math.Min(50, finalPrompt.Length)), candidateTasks.Count, action, msgId);
                                    task = null;
                                }
                            }
                            else
                            {
                                // 如果消息中没有prompt，且只有一个候选任务，才匹配
                                if (candidateTasks.Count == 1)
                                {
                                    task = candidateTasks.First();
                                    Log.Warning("BOT 使用模糊匹配找到任务（无prompt验证）, TaskId: {TaskId}, Action: {Action}, 建议优化任务提交时的唯一标识", 
                                        task.Id, action);
                                }
                                else if (candidateTasks.Count > 1)
                                {
                                    Log.Error("BOT 发现多个候选任务无法区分（无prompt验证）, Count: {Count}, Action: {Action}, MessageId: {MessageId}, 可能导致任务混淆！", 
                                        candidateTasks.Count, action, msgId);
                                    task = null;
                                }
                            }
                        }
                    }
                }
                else
                {
                    // 其他任务类型使用严格验证的模糊匹配，避免串任务
                    var cutoffTime = DateTimeOffset.Now.AddMinutes(-1).ToUnixTimeMilliseconds();
                    var candidateTasks = instance.FindRunningTask(c => 
                        (c.Status == TaskStatus.IN_PROGRESS || c.Status == TaskStatus.SUBMITTED) &&
                        (c.BotType == botType || c.RealBotType == botType) && 
                        c.Action == action &&
                        c.StartTime >= cutoffTime)
                        .OrderBy(c => c.StartTime)
                        .ToList();
                    
                    // 严格验证：即使只有一个候选，也要验证prompt是否匹配
                    if (candidateTasks.Count > 0)
                    {
                        // 如果消息中有prompt，必须验证候选任务的prompt是否匹配
                        if (!string.IsNullOrWhiteSpace(finalPrompt))
                        {
                            var matchedTask = candidateTasks.FirstOrDefault(c => 
                                !string.IsNullOrWhiteSpace(c.PromptEn) && 
                                (c.PromptEn == finalPrompt || 
                                 c.PromptEn.FormatPrompt() == finalPrompt.FormatPrompt() ||
                                 c.PromptEn.FormatPromptParam() == finalPrompt.FormatPromptParam()));
                            
                            if (matchedTask != null)
                            {
                                task = matchedTask;
                                if (candidateTasks.Count > 1)
                                {
                                    Log.Warning("BOT 使用模糊匹配找到任务（已验证prompt匹配）, TaskId: {TaskId}, Action: {Action}, CandidateCount: {Count}", 
                                        task.Id, action, candidateTasks.Count);
                                }
                            }
                            else
                            {
                                // Prompt不匹配，拒绝匹配，避免串任务
                                Log.Error("BOT 模糊匹配失败：候选任务的prompt与消息prompt不匹配, MessagePrompt: {MessagePrompt}, CandidateCount: {Count}, Action: {Action}, MessageId: {MessageId}, 拒绝匹配以避免串任务！", 
                                    finalPrompt.Substring(0, Math.Min(50, finalPrompt.Length)), candidateTasks.Count, action, msgId);
                                task = null;
                            }
                        }
                        else
                        {
                            // 如果消息中没有prompt，且只有一个候选任务，才匹配
                            if (candidateTasks.Count == 1)
                            {
                                task = candidateTasks.First();
                                Log.Warning("BOT 使用模糊匹配找到任务（无prompt验证）, TaskId: {TaskId}, Action: {Action}, 建议优化任务提交时的唯一标识", 
                                    task.Id, action);
                            }
                            else if (candidateTasks.Count > 1)
                            {
                                Log.Error("BOT 发现多个候选任务无法区分（无prompt验证）, Count: {Count}, Action: {Action}, MessageId: {MessageId}, 可能导致任务混淆！", 
                                    candidateTasks.Count, action, msgId);
                                task = null;
                            }
                        }
                    }
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
        }

        protected void FinishTask(TaskInfo task, SocketMessage message)
        {
            // 设置图片信息
            var image = message.Attachments?.FirstOrDefault();
            if (task != null && image != null)
            {
                task.Width = image.Width;
                task.Height = image.Height;
                task.Url = image.Url;
                task.ProxyUrl = image.ProxyUrl;
                task.ContentType = image.ContentType;
                task.Size = image.Size;
            }

            task.SetProperty(Constants.TASK_PROPERTY_MESSAGE_ID, message.Id.ToString());
            task.SetProperty(Constants.TASK_PROPERTY_FLAGS, Convert.ToInt32(message.Flags));
            task.SetProperty(Constants.TASK_PROPERTY_MESSAGE_HASH, discordHelper.GetMessageHash(task.ImageUrl));

            task.Buttons = message.Components.SelectMany(x => x.Components)
                .Select(c =>
                {
                    if (c is ButtonComponent btn)
                    {
                        return new CustomComponentModel
                        {
                            CustomId = btn.CustomId?.ToString() ?? string.Empty,
                            Emoji = btn.Emote?.Name ?? string.Empty,
                            Label = btn.Label ?? string.Empty,
                            Style = (int?)btn.Style ?? 0,
                            Type = (int?)btn.Type ?? 0,
                        };
                    }
                    return null;
                }).Where(c => c != null && !string.IsNullOrWhiteSpace(c.CustomId)).ToList();

            if (string.IsNullOrWhiteSpace(task.Description))
            {
                task.Description = "Submit success";
            }
            if (string.IsNullOrWhiteSpace(task.FailReason))
            {
                task.FailReason = "";
            }
            if (string.IsNullOrWhiteSpace(task.State))
            {
                task.State = "";
            }

            task.Success();

            // 表示消息已经处理过了
            CacheHelper<string, bool>.AddOrUpdate(message.Id.ToString(), true);

            Log.Debug("由 BOT 确认消息处理完成 {@0}", message.Id);
        }

        protected bool HasImage(SocketMessage message)
        {
            return message?.Attachments?.Count > 0;
        }

        protected string GetImageUrl(SocketMessage message)
        {
            if (message?.Attachments?.Count > 0)
            {
                return ReplaceCdnUrl(message.Attachments.FirstOrDefault()?.Url);
            }

            return default;
        }

        protected string ReplaceCdnUrl(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl)) return imageUrl;

            string cdn = discordHelper.GetCdn();
            if (imageUrl.StartsWith(cdn)) return imageUrl;

            return imageUrl.Replace(DiscordHelper.DISCORD_CDN_URL, cdn);
        }
    }
}