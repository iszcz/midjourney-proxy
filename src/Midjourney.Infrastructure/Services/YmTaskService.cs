using System.Net.Http;
using Microsoft.Extensions.Caching.Memory;
using Midjourney.Base.Dto;
using Midjourney.Base.Models;
using Midjourney.Base.Services;

namespace Midjourney.Infrastructure.LoadBalancer
{
    /// <summary>
    /// Placeholder implementation after removing Midjourney.License dependency.
    /// All operations now throw <see cref="NotSupportedException"/> to signal
    /// that YouChuan/Official integrations are no longer available.
    /// </summary>
    public sealed class YmTaskService : IYmTaskService
    {
        public YmTaskService(
            DiscordAccount account,
            IDiscordInstance instance,
            IMemoryCache cache,
            IHttpClientFactory httpClientFactory)
        {
        }

        public string YouChuanToken => null;

        public string OfficialToken => null;

        public Task YouChuanLogin() => ThrowRemoved();

        public Task<Message> SubmitTaskAsync(TaskInfo task, ITaskStoreService taskStoreService, IDiscordInstance instance) => ThrowRemoved<Message>();

        public Task<Message> SubmitActionAsync(TaskInfo task, SubmitActionDTO submitAction, TaskInfo targetTask, ITaskStoreService taskStoreService, IDiscordInstance discordInstance, string newPrompt = null) => ThrowRemoved<Message>();

        public Task UpdateStatus(TaskInfo info, ITaskStoreService taskStoreService, DiscordAccount account) => ThrowRemoved();

        public Task YouChuanSyncInfo(bool isClearCache = false) => ThrowRemoved();

        public Task OfficialSyncInfo(bool isClearCache = false) => ThrowRemoved();

        public Task<string> GetSeed(TaskInfo task) => ThrowRemoved<string>();

        public Task Describe(TaskInfo task) => ThrowRemoved();

        public Task<Message> SubmitModal(TaskInfo task, TaskInfo parentTask, SubmitModalDTO submitAction, ITaskStoreService taskStoreService) => ThrowRemoved<Message>();

        public Task<string> UploadFile(TaskInfo task, byte[] fileContent, string fileName, int type = 0) => ThrowRemoved<string>();

        public Task<ProfileCreateResultDto> ProfileCreateAsync(ProfileCreateDto request) => ThrowRemoved<ProfileCreateResultDto>();

        public Task<ProfileGetRandomPairsResponse> ProfileCreateSkipAsync(PersonalizeTag personalize, string cursor = "") => ThrowRemoved<ProfileGetRandomPairsResponse>();

        public Task<ProfileGetRandomPairsResponse> ProfileCreateRateAsync(PersonalizeTag personalize, bool? isRight = null) => ThrowRemoved<ProfileGetRandomPairsResponse>();

        private static Task ThrowRemoved() => Task.FromException(new NotSupportedException("YouChuan and Official integrations have been removed."));

        private static Task<T> ThrowRemoved<T>() => Task.FromException<T>(new NotSupportedException("YouChuan and Official integrations have been removed."));
    }
}

