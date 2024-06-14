using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NAudio.Wave;

public class WithOpenAI
{
    private static readonly string apiKey = "your-api-key";
    private static readonly string textEndpoint = "https://api.openai.com/v1/engines/gpt-4o/completions"; // Example endpoint for text
    private static readonly string audioEndpoint = "https://api.openai.com/v1/audio/transcriptions"; // Example endpoint for audio

    static async Task Main(string[] args)
    {
        Console.WriteLine("Sending initial text prompt...");

        // Send initial text prompt
        var initialResponse = await SendTextPrompt("Please listen to the following audio and provide your feedback:");

        Console.WriteLine("Recording and streaming audio...");
        var waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(16000, 1) // Ensure this matches your phone system output format
        };

        waveIn.DataAvailable += async (sender, e) =>
        {
            await SendAudioStream(e.Buffer, e.BytesRecorded);
        };

        waveIn.StartRecording();
        Console.WriteLine("Press Enter to stop recording...");
        Console.ReadLine();
        waveIn.StopRecording();
    }

    static async Task<string> SendTextPrompt(string prompt)
    {
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            var requestBody = new
            {
                model = "gpt-4o",
                prompt = prompt,
                max_tokens = 50
            };

            var json = JsonConvert.SerializeObject(requestBody);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await client.PostAsync(textEndpoint, content);
            var responseString = await response.Content.ReadAsStringAsync();

            Console.WriteLine($"Text Response: {responseString}");
            return responseString;
        }
    }

    static async Task SendAudioStream(byte[] buffer, int bytesRecorded)
    {
        using (var client = new HttpClient())
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

            using (var content = new ByteArrayContent(buffer, 0, bytesRecorded))
            {
                content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav"); // Use the correct MIME type for your audio format
                var response = await client.PostAsync(audioEndpoint, content);
                var responseString = await response.Content.ReadAsStringAsync();

                var json = JObject.Parse(responseString);
                var textResponse = json["text"].ToString();
                Console.WriteLine($"Transcription: {textResponse}");
            }
        }
    }
}