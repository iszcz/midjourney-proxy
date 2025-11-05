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
using Swashbuckle.AspNetCore.Annotations;

namespace Midjourney.Infrastructure.Dto
{
    /// <summary>
    /// Video提交参数。
    /// </summary>
    [SwaggerSchema("Video提交参数")]
    public class SubmitVideoDTO : BaseSubmitDTO
    {
        /// <summary>
        /// bot 类型，mj(默认)或niji
        /// MID_JOURNEY | 枚举值: NIJI_JOURNEY
        /// </summary>
        public string BotType { get; set; }

        /// <summary>
        /// 提示词。
        /// </summary>
        [SwaggerSchema("提示词", Description = "Begin with the majestic Hippopotamus...")]
        public string Prompt { get; set; }

        /// <summary>
        /// 运动程度，low/medium/high
        /// </summary>
        [SwaggerSchema("运动程度", Description = "low/medium/high")]
        public string Motion { get; set; }

        /// <summary>
        /// 是否循环播放
        /// </summary>
        [SwaggerSchema("是否循环播放", Description = "true表示添加--loop参数")]
        public bool? Loop { get; set; }

        /// <summary>
        /// 起始图片URL
        /// </summary>
        [SwaggerSchema("起始图片URL")]
        public string Image { get; set; }

        /// <summary>
        /// 结束图片URL
        /// </summary>
        [SwaggerSchema("结束图片URL")]
        public string EndImage { get; set; }

        /// <summary>
        /// 批次大小，用于控制视频生成的批次数量
        /// </summary>
        [SwaggerSchema("批次大小", Description = "批次大小，范围通常为1-4")]
        public int? BatchSize { get; set; }

        /// <summary>
        /// 操作类型，extend表示视频扩展操作
        /// </summary>
        [SwaggerSchema("操作类型", Description = "extend表示视频扩展操作")]
        public string Action { get; set; }

        /// <summary>
        /// 扩展操作时要放大的图片索引 (1-4)
        /// </summary>
        [SwaggerSchema("图片索引", Description = "扩展操作时要放大的图片索引 (1-4)")]
        public int? Index { get; set; }

        /// <summary>
        /// 扩展操作时源任务ID
        /// </summary>
        [SwaggerSchema("源任务ID", Description = "扩展操作时源任务ID")]
        public string TaskId { get; set; }

        /// <summary>
        /// 账号过滤
        /// </summary>
        public AccountFilter AccountFilter { get; set; }
    }
} 