using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using System;

public class OllamaClient : MonoBehaviour
{
    private const string OLLAMA_URL = "http://localhost:11434/api/generate";
    private const string MODEL_NAME = "hf.co/Jackrong/Qwen3.5-9B-Claude-4.6-Opus-Reasoning-Distilled-GGUF:Q6_K";

    [System.Serializable]
    private class OllamaRequest
    {
        public string model;
        public string prompt;
        public bool stream;
    }

    [System.Serializable]
    private class OllamaResponse
    {
        public string model;
        public string created_at;
        public string response;
        public string thinking;
        public bool done;
    }

    void Start()
    {
        // Simple test call when the script starts
        Debug.Log("Connecting to Ollama...");
        SendPrompt("Hello! Are you connected?", response =>
        {
            Debug.Log($"Ollama response: {response}");
        });
    }

    public void SendPrompt(string prompt, Action<string> onResponse)
    {
        StartCoroutine(SendPromptCoroutine(prompt, onResponse));
    }

    private IEnumerator SendPromptCoroutine(string prompt, Action<string> onResponse)
    {
        OllamaRequest requestData = new OllamaRequest
        {
            model = MODEL_NAME,
            prompt = prompt,
            stream = false
        };

        string jsonRequest = JsonUtility.ToJson(requestData);
        byte[] bodyRaw = Encoding.UTF8.GetBytes(jsonRequest);

        using (UnityWebRequest request = new UnityWebRequest(OLLAMA_URL, "POST"))
        {
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    OllamaResponse responseData = JsonUtility.FromJson<OllamaResponse>(request.downloadHandler.text);
                    onResponse?.Invoke(responseData.response);
                }
                catch (Exception e)
                {
                    Debug.LogError($"Failed to parse Ollama response: {e.Message}");
                    onResponse?.Invoke("Error parsing response.");
                }
            }
            else
            {
                Debug.LogError($"Ollama request failed: {request.error}");
                onResponse?.Invoke($"Error: {request.error}");
            }
        }
    }
}
