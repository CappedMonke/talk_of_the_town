using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;

namespace ollama
{
    /// <summary>Simple request structure for chat endpoint</summary>
    [Serializable]
    public class SimpleChatRequest
    {
        public string model;
        public List<SimpleChatMessage> messages;
        public bool stream;
        public int keep_alive;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public int? num_ctx;
        /// <summary>
        /// Controls reasoning/thinking for models that support it (e.g. gpt-oss).
        /// null = model default, false = disable, "low"/"medium"/"high" for effort levels.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object think;
        /// <summary>
        /// Forces structured output. Set to "json" for basic JSON mode,
        /// or a JSON schema object for grammar-constrained output.
        /// null = no constraint (model output is freeform).
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public object format;
        /// <summary>
        /// Runtime options passed to the model (num_predict, temperature, top_p, etc.).
        /// null = use Ollama defaults.
        /// </summary>
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, object> options;
    }

    [Serializable]
    public class SimpleChatMessage
    {
        public string role;
        public string content;
        public string[] images;
    }

    /// <summary>Extended response with full metadata</summary>
    [Serializable]
    public class ChatResponseExtended
    {
        public string model;
        public SimpleChatMessage message;
        
        [JsonProperty("created_at")]
        public string createdAt;
        
        [JsonProperty("done_reason")]
        public string doneReason;
        
        public bool done;
        
        [JsonProperty("prompt_eval_count")]
        public int prompt_eval_count;
        
        [JsonProperty("eval_count")]
        public int eval_count;
        
        [JsonProperty("prompt_eval_duration")]
        public long prompt_eval_duration;
        
        [JsonProperty("eval_duration")]
        public long eval_duration;
        
        [JsonProperty("total_duration")]
        public long total_duration;
        
        [JsonProperty("load_duration")]
        public long load_duration;
    }

    /// <summary>
    /// Rolling conversation history with a configurable size cap.
    /// Trims by full user/assistant pairs so the array never ends on a dangling user message.
    /// System message is pinned and never evicted.
    /// </summary>
    public class ConversationHistory
    {
        private readonly List<SimpleChatMessage> _messages = new();
        private SimpleChatMessage _systemMessage;

        /// <summary>How many (user, assistant) pairs to retain. 0 disables memory.</summary>
        public int MaxPairs { get; set; }

        public IReadOnlyList<SimpleChatMessage> Messages => _messages;
        public int MessageCount => _messages.Count;
        public int PairCount
        {
            get
            {
                int n = 0;
                foreach (var m in _messages)
                    if (m.role == "user") n++;
                return n;
            }
        }

        public ConversationHistory(int maxPairs = 5) { MaxPairs = maxPairs; }

        public void SetSystemMessage(string content)
        {
            _systemMessage = string.IsNullOrEmpty(content)
                ? null
                : new SimpleChatMessage { role = "system", content = content };
        }

        public void AddUser(string content, string[] images = null)
        {
            _messages.Add(new SimpleChatMessage { role = "user", content = content, images = images });
        }

        public void AddAssistant(string content)
        {
            _messages.Add(new SimpleChatMessage { role = "assistant", content = content });
            TrimToMaxPairs();
        }

        public void Clear() => _messages.Clear();

        private void TrimToMaxPairs()
        {
            if (MaxPairs <= 0) { _messages.Clear(); return; }

            while (PairCount > MaxPairs && _messages.Count >= 2)
            {
                if (_messages[0].role == "user") _messages.RemoveAt(0);
                if (_messages.Count > 0 && _messages[0].role == "assistant") _messages.RemoveAt(0);
            }
        }

        /// <summary>Builds the full payload: [system?] + history + currentUser.</summary>
        public List<SimpleChatMessage> BuildPayload(SimpleChatMessage currentUser)
        {
            var list = new List<SimpleChatMessage>();
            if (_systemMessage != null) list.Add(_systemMessage);
            list.AddRange(_messages);
            list.Add(currentUser);
            return list;
        }
    }

    public static class OllamaExtensions
    {
        private const string SERVER = "http://localhost:11434/";
        private const string CHAT_ENDPOINT = "api/chat";

        /// <summary>Chat with full metadata response</summary>
        public static async Task<ChatResponse> ChatWithMetadataExt(
            string model,
            string prompt,
            int keep_alive = 300,
            int num_ctx = 0,
            Texture2D image = null,
            object think = null,
            object format = null,
            Dictionary<string, object> options = null)
        {
            try
            {
                // Instead of using Ollama's chat, we'll manage our own simple history
                // This is a workaround since Ollama.Chat's internal classes are private
                var message = new SimpleChatMessage
                {
                    role = "user",
                    content = prompt,
                    images = image != null ? new[] { Texture2Base64(image) } : null
                };

                var request = new SimpleChatRequest
                {
                    model = model,
                    messages = new List<SimpleChatMessage> { message },
                    stream = false,
                    keep_alive = keep_alive,
                    num_ctx = num_ctx > 0 ? num_ctx : (int?)null,
                    think = think,
                    format = format,
                    options = options
                };

                string payload = JsonConvert.SerializeObject(request, Formatting.None,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create($"{SERVER}{CHAT_ENDPOINT}");
                httpWebRequest.ContentType = "application/json";
                httpWebRequest.Method = "POST";

                using (var streamWriter = new StreamWriter(await httpWebRequest.GetRequestStreamAsync()))
                    await streamWriter.WriteAsync(payload);

                string result;
                using (var httpResponse = await httpWebRequest.GetResponseAsync())
                using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                    result = await streamReader.ReadToEndAsync();

                var fullResponse = JsonConvert.DeserializeObject<ChatResponseExtended>(result);

                return new ChatResponse
                {
                    content = fullResponse.message.content,
                    model = fullResponse.model,
                    promptEvalCount = fullResponse.prompt_eval_count,
                    promptEvalDuration = fullResponse.prompt_eval_duration,
                    evalCount = fullResponse.eval_count,
                    evalDuration = fullResponse.eval_duration,
                    totalDuration = fullResponse.total_duration,
                    loadDuration = fullResponse.load_duration
                };
            }
            catch (WebException webEx)
            {
                string errorResponse = "";
                if (webEx.Response != null)
                {
                    using (var errorStream = webEx.Response.GetResponseStream())
                    using (var reader = new StreamReader(errorStream))
                        errorResponse = await reader.ReadToEndAsync();
                }

                Debug.LogError($"[OllamaExt] HTTP Error: {webEx.Message}\n{errorResponse}");
                return CreateErrorResponse(webEx.Message);
            }
            catch (Exception e)
            {
                Debug.LogError($"[OllamaExt] Error: {e.Message}\n{e.StackTrace}");
                return CreateErrorResponse(e.Message);
            }
        }

        /// <summary>Chat with metadata response and persistent conversation history.</summary>
        public static async Task<ChatResponse> ChatWithMetadataExt(
            string model,
            string prompt,
            ConversationHistory history,
            int keep_alive = 300,
            int num_ctx = 0,
            Texture2D image = null,
            object think = null,
            object format = null,
            Dictionary<string, object> options = null)
        {
            try
            {
                string[] images = image != null ? new[] { Texture2Base64(image) } : null;

                var userMessage = new SimpleChatMessage
                {
                    role = "user",
                    content = prompt,
                    images = images
                };

                var messages = history != null
                    ? history.BuildPayload(userMessage)
                    : new List<SimpleChatMessage> { userMessage };

                var request = new SimpleChatRequest
                {
                    model = model,
                    messages = messages,
                    stream = false,
                    keep_alive = keep_alive,
                    num_ctx = num_ctx > 0 ? num_ctx : (int?)null,
                    think = think,
                    format = format,
                    options = options
                };

                string payload = JsonConvert.SerializeObject(request, Formatting.None,
                    new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

                HttpWebRequest httpWebRequest = (HttpWebRequest)WebRequest.Create($"{SERVER}{CHAT_ENDPOINT}");
                httpWebRequest.ContentType = "application/json";
                httpWebRequest.Method = "POST";

                using (var streamWriter = new StreamWriter(await httpWebRequest.GetRequestStreamAsync()))
                    await streamWriter.WriteAsync(payload);

                string result;
                using (var httpResponse = await httpWebRequest.GetResponseAsync())
                using (var streamReader = new StreamReader(httpResponse.GetResponseStream()))
                    result = await streamReader.ReadToEndAsync();

                var fullResponse = JsonConvert.DeserializeObject<ChatResponseExtended>(result);

                // Commit this turn only after a successful round trip,
                // so a failed request does not poison the rolling window.
                // Skip empty responses to avoid poisoning history with blank assistant turns.
                if (history != null && !string.IsNullOrEmpty(fullResponse.message.content))
                {
                    history.AddUser(prompt, images);
                    history.AddAssistant(fullResponse.message.content);
                }

                return new ChatResponse
                {
                    content = fullResponse.message.content,
                    model = fullResponse.model,
                    promptEvalCount = fullResponse.prompt_eval_count,
                    promptEvalDuration = fullResponse.prompt_eval_duration,
                    evalCount = fullResponse.eval_count,
                    evalDuration = fullResponse.eval_duration,
                    totalDuration = fullResponse.total_duration,
                    loadDuration = fullResponse.load_duration
                };
            }
            catch (WebException webEx)
            {
                string errorResponse = "";
                if (webEx.Response != null)
                {
                    using (var errorStream = webEx.Response.GetResponseStream())
                    using (var reader = new StreamReader(errorStream))
                        errorResponse = await reader.ReadToEndAsync();
                }
                Debug.LogError($"[OllamaExt] HTTP Error: {webEx.Message}\n{errorResponse}");
                return CreateErrorResponse(webEx.Message);
            }
            catch (Exception e)
            {
                Debug.LogError($"[OllamaExt] Error: {e.Message}\n{e.StackTrace}");
                return CreateErrorResponse(e.Message);
            }
        }

        private static string Texture2Base64(Texture2D texture, bool fullQuality = true)
        {
            if (texture == null) return null;
            return Convert.ToBase64String(fullQuality ? texture.EncodeToPNG() : texture.EncodeToJPG());
        }

        private static ChatResponse CreateErrorResponse(string error)
        {
            return new ChatResponse
            {
                content = $"Error: {error}",
                model = "error",
                promptEvalCount = 0,
                evalCount = 0,
                promptEvalDuration = 0,
                evalDuration = 0,
                totalDuration = 0,
                loadDuration = 0
            };
        }
    }

    /// <summary>Response from Chat with metadata</summary>
    [Serializable]
    public class ChatResponse
    {
        public string content;
        public string model;
        
        public int promptEvalCount;
        public int evalCount;
        
        public long promptEvalDuration;
        public long evalDuration;
        public long totalDuration;
        public long loadDuration;

        public double PromptEvalSeconds => promptEvalDuration / 1_000_000_000.0;
        public double EvalSeconds => evalDuration / 1_000_000_000.0;
        public double TotalSeconds => totalDuration / 1_000_000_000.0;
        public double LoadSeconds => loadDuration / 1_000_000_000.0;

        public int TotalTokens => promptEvalCount + evalCount;
    }
}