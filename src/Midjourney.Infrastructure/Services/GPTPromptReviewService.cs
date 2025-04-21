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

using Serilog;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Midjourney.Infrastructure.Services
{
    /// <summary>
    /// 基于OpenAI GPT的提示词审核服务
    /// </summary>
    public class GPTPromptReviewService : IPromptReviewService
    {
        private const string REVIEW_API = "https://api.openai.com/v1/chat/completions";
        private readonly string _apiUrl;
        private readonly string _apiKey;
        private readonly TimeSpan _timeout;
        private readonly string _model;
        private readonly int _maxTokens;
        private readonly double _temperature;
        private readonly HttpClient _httpClient;

        public GPTPromptReviewService()
        {
            var config = GlobalConfiguration.Setting?.Openai;

            _apiUrl = config?.GptApiUrl ?? REVIEW_API;
            _apiKey = config?.GptApiKey;
            _timeout = config?.Timeout ?? TimeSpan.FromSeconds(30);
            _model = config?.Model ?? "gpt-4o-mini";
            _maxTokens = config?.MaxTokens ?? 2048;
            _temperature = config?.Temperature ?? 0;

            WebProxy webProxy = null;
            var proxy = GlobalConfiguration.Setting.Proxy;
            if (!string.IsNullOrEmpty(proxy?.Host))
            {
                webProxy = new WebProxy(proxy.Host, proxy.Port ?? 80);
            }
            var hch = new HttpClientHandler
            {
                UseProxy = webProxy != null,
                Proxy = webProxy
            };
            _httpClient = new HttpClient(hch) { Timeout = _timeout };
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_apiKey}");
        }

        public PromptReviewResult ReviewPrompt(string prompt)
        {
            if (string.IsNullOrWhiteSpace(_apiKey) || string.IsNullOrWhiteSpace(_apiUrl))
            {
                return new PromptReviewResult { NeedModify = false, Prompt = prompt };
            }

            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new PromptReviewResult { NeedModify = false, Prompt = prompt };
            }

            // 默认结果
            var defaultResult = new PromptReviewResult
            {
                NeedModify = false,
                Prompt = prompt
            };

            // 开启AI审核功能
            if (GlobalConfiguration.Setting?.EnableAIReview != true)
            {
                return defaultResult;
            }
            
            var requestBody = new
            {
                model = _model,
                messages = new[]
                {
                    new { role = "system", content = "你是一个专业的Midjourney提示词审核专家。你的职责是严格审查用户提示词中的所有可能违规内容，并彻底清除这些内容。你必须进行多轮自我检查，确保最终结果完全合规。 \n \n你的核心职责是： \n1. 严格分析用户提示词中的每一个词语和短语 \n2. 识别所有可能违反Midjourney规则的内容 \n3. 彻底移除所有违规内容，不留任何痕迹 \n4. 对修改后的内容进行多轮自检，确保没有遗漏任何违规内容 \n \nMidjourney官方禁止内容规则（需要彻底过滤的内容）： \n1. 色情内容： \n   - 所有形式的裸露内容，包括隐晦或暗示的裸露 \n   - 性行为或性暗示的描述，无论多么间接 \n   - 与性相关的身体部位的强调或特写 \n   - 色情或成人内容相关的任何描述词 \n \n2. 暴力内容： \n   - 真实暴力场景、战争画面或武器详细描述 \n   - 血腥、gore或恐怖内容 \n   - 自残、自杀或伤害相关描述 \n   - 暴力行为的详细描述 \n \n3. 歧视与仇恨内容： \n   - 针对任何种族、民族、宗教、性别、性取向的歧视性语言 \n   - 仇恨言论或煽动性内容 \n   - 政治敏感或极端思想相关内容 \n   - 文化冒犯或不尊重的内容 \n \n4. 名人与公众人物： \n   - 对真实名人或公众人物的不尊重或误导性描述 \n   - 未经授权的真实人物肖像创作请求 \n   - 可能造成名誉损害的内容 \n \n5. 其他禁止内容： \n   - 药物、毒品相关详细描述 \n   - 非法活动或犯罪行为指导 \n   - 危险或有害行为的教程 \n   - 医疗建议或治疗方法 \n	 - 超长字母数字的无效单词组合，例如：06Q7tQ2QY91Y6Uz1z3 \n	 - 多个单词可能互相组成或构成新的违规内容 \n \n执行要求（极其重要）： \n1. 首次检查：识别所有可能违规的内容，列出具体违规词语 \n2. 彻底清除：从提示词中完全移除所有违规内容，不要尝试用其他词替代 \n3. 二次自检：对修改后的内容再次检查，确保没有遗漏的违规内容 \n4. 最终确认：进行第三轮检查，确保输出结果完全符合Midjourney规则 \n \n超严格内容处理原则： \n1. 宁可错杀一千，不可放过一个：如果对某内容有疑虑，必须移除 \n2. 彻底移除原则：发现违规内容，连同相关上下文一起移除 \n3. 不替换原则：不要用委婉语或其他表达替换违规内容，直接删除 \n4. 多重筛查：对常见规避审查的技巧（如使用同音字、拼写错误故意规避）保持高度警惕 \n \n除了以上规则，你还需根据以下内容判断是否需要修改： \n1. 需要修改的内容： \n   - 提示词中的任何违规内容，无论多么隐晦 \n   - 可能具有双重含义的词语（如果其中一个含义违规） \n   - 奇怪的符号组合或可能用于规避审查的特殊字符 \n   - 暗示性的表达方式或间接引用违规内容的词语 \n   - 无法组成正常语句 \n \n2. 不需要修改的内容： \n   - 类似于___URL_PLACEHOLDER___int，这些是参考图URL的占位符，int是序号 \n   - 艺术风格、技术参数或渲染设置相关的专业术语 \n   - 明确合规的创作描述，如风景、动物、建筑等安全主题 \n \n输出要求： \n1. 你必须确保最终修改后的提示词绝对不包含任何违规内容 \n2. 完成三轮自检后，以JSON格式返回结果 \n3. 如有任何修改，详细说明所有被移除的违规内容及原因 \n4. 如果不需要修改，返回原始提示词 \n5. 确保提示词的可读性，是连贯的语句，如果是无意义的词组，可以适当重组语句 \n \n请以JSON格式返回，包含以下字段： \n{ \n  \"need_modify\": true/false,      // 是否需要修改 \n  \"prompt\": \"最终修改后的提示词\",    // 确保此处内容已通过三轮检查，完全合规 \n  \"reason\": \"详细的修改原因和被移除内容说明\" \n}" },
                    new { role = "user", content = prompt }
                },
                max_tokens = _maxTokens,
                temperature = _temperature
            };

            try
            {
                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                var response = _httpClient.PostAsync(_apiUrl, content).Result;

                if (!response.IsSuccessStatusCode || string.IsNullOrWhiteSpace(response.Content.ReadAsStringAsync().Result))
                {
                    Log.Warning("GPT审核服务请求失败: {StatusCode} - {Content}", response.StatusCode, response.Content.ReadAsStringAsync().Result);
                    return defaultResult;
                }

                var result = JsonDocument.Parse(response.Content.ReadAsStringAsync().Result);
                var choices = result.RootElement.GetProperty("choices").EnumerateArray();
                var reviewResult = choices.First().GetProperty("message").GetProperty("content").GetString();

                if (string.IsNullOrWhiteSpace(reviewResult))
                {
                    return defaultResult;
                }

                try
                {
                    // 解析JSON响应
                    var responseObj = JsonSerializer.Deserialize<ReviewResponse>(reviewResult);
                    
                    return new PromptReviewResult
                    {
                        NeedModify = responseObj.need_modify,
                        Prompt = responseObj.prompt ?? prompt,
                        Reason = responseObj.reason
                    };
                }
                catch (JsonException jsonEx)
                {
                    Log.Warning(jsonEx, "解析GPT审核结果失败: {Result}", reviewResult);
                    return defaultResult;
                }
            }
            catch (HttpRequestException e)
            {
                Log.Warning(e, "HTTP请求失败");
            }
            catch (JsonException e)
            {
                Log.Warning(e, "解析JSON响应失败");
            }
            catch (Exception e)
            {
                Log.Warning(e, "调用OpenAI审核服务失败");
            }

            return defaultResult;
        }

        private class ReviewResponse
        {
            [JsonPropertyName("need_modify")]
            public bool need_modify { get; set; }

            [JsonPropertyName("prompt")]
            public string prompt { get; set; }

            [JsonPropertyName("reason")]
            public string reason { get; set; }
        }
    }
} 