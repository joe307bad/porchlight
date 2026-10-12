using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Porchlight.Shared.Api;
using Porchlight.Shared.Model;
using UnityEngine.Networking;

namespace Porchlight.Net
{
    /// <summary>
    /// Where towns come from. Today that's always HTTP. Keeping it behind an interface lets the Switch
    /// build swap in bundled or offline data, since certification has its own rules for online features.
    /// </summary>
    public interface ITownSource
    {
        string Description { get; }
        Task<List<TownSummaryDto>> ListTownsAsync();
        Task<TownDto> GetTownAsync(string id);
        Task<TownDto> CreateTownAsync(string name);

        /// <summary>Saves and returns the server's copy, which carries the new version.</summary>
        Task<TownDto> SaveTownAsync(TownDto town);
    }

    public sealed class TownApiException : Exception
    {
        public TownApiException(long statusCode, string message) : base(message)
        {
            StatusCode = statusCode;
        }

        /// <summary>HTTP status, or 0 when the server couldn't be reached.</summary>
        public long StatusCode { get; }

        public bool IsConflict => StatusCode == 409;
    }

    public sealed class HttpTownSource : ITownSource
    {
        // Matches the server's System.Text.Json web defaults (camelCase).
        private static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly string _baseUrl;

        public HttpTownSource(string baseUrl)
        {
            _baseUrl = baseUrl.TrimEnd('/');
        }

        public string Description => _baseUrl;

        public async Task<List<TownSummaryDto>> ListTownsAsync() =>
            JsonConvert.DeserializeObject<List<TownSummaryDto>>(await Send("GET", ApiRoutes.Towns, null), Json);

        public async Task<TownDto> GetTownAsync(string id) =>
            JsonConvert.DeserializeObject<TownDto>(await Send("GET", ApiRoutes.Town(id), null), Json);

        public async Task<TownDto> CreateTownAsync(string name)
        {
            var body = JsonConvert.SerializeObject(new CreateTownRequest { Name = name }, Json);
            return JsonConvert.DeserializeObject<TownDto>(await Send("POST", ApiRoutes.Towns, body), Json);
        }

        public async Task<TownDto> SaveTownAsync(TownDto town)
        {
            var body = JsonConvert.SerializeObject(town, Json);
            return JsonConvert.DeserializeObject<TownDto>(await Send("PUT", ApiRoutes.Town(town.Id), body), Json);
        }

        private async Task<string> Send(string method, string path, string body)
        {
            using (var request = new UnityWebRequest(_baseUrl + path, method))
            {
                request.downloadHandler = new DownloadHandlerBuffer();
                if (body != null)
                {
                    request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(body));
                    request.SetRequestHeader("Content-Type", "application/json");
                }

                await Completion(request.SendWebRequest());

                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new TownApiException(request.responseCode, ErrorMessage(request));
                }

                return request.downloadHandler.text;
            }
        }

        private static string ErrorMessage(UnityWebRequest request)
        {
            var text = request.downloadHandler?.text;
            if (!string.IsNullOrEmpty(text))
            {
                try
                {
                    var error = JsonConvert.DeserializeObject<ApiError>(text, Json);
                    if (!string.IsNullOrEmpty(error?.Error))
                    {
                        return error.Details != null && error.Details.Count > 0
                            ? error.Error + ": " + string.Join("; ", error.Details)
                            : error.Error;
                    }
                }
                catch (JsonException)
                {
                    // The body wasn't an ApiError; fall back to the transport error.
                }
            }

            return request.error;
        }

        private static Task Completion(UnityWebRequestAsyncOperation operation)
        {
            // If the operation is already done, Unity runs the completed callback immediately.
            var done = new TaskCompletionSource<bool>();
            operation.completed += _ => done.TrySetResult(true);
            return done.Task;
        }
    }
}
