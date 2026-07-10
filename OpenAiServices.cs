internal sealed class OpenAiSceneAnalyzer(HttpClient httpClient, AppSettings settings) : IDisposable
{
    private const string ScenePrompt = """
        You are a concise visual assistant for smart glasses.
        Describe what is visible in the image in natural Spanish.
        Mention the main objects and relevant context.
        If a hand appears making a pinch gesture with thumb and index finger, ignore that gesture because it is only the capture trigger.
        Do not warn about ordinary clutter, cables, furniture, or everyday objects unless there is a clear and immediate danger.
        Keep the response short enough to be spoken aloud in under fifteen seconds.
        """;

    public async Task<string> DescribeSceneAsync(byte[] jpegBytes, CancellationToken cancellationToken)
    {
        EnsureApiKey();

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiApiKey);

        var imageUrl = "data:image/jpeg;base64," + Convert.ToBase64String(jpegBytes);
        var payload = new
        {
            model = settings.OpenAiModel,
            input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = ScenePrompt },
                        new { type = "input_image", image_url = imageUrl }
                    }
                }
            }
        };

        request.Content = JsonContent(payload);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI vision request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
        }

        return ExtractResponseText(json);
    }

    public void Dispose()
    {
    }

    private void EnsureApiKey()
    {
        if (string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is required for scene analysis.");
        }
    }

    private static HttpContent JsonContent(object payload)
    {
        return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }

    private static string ExtractResponseText(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("output_text", out var outputText))
        {
            return outputText.GetString() ?? string.Empty;
        }

        if (document.RootElement.TryGetProperty("output", out var output))
        {
            foreach (var outputItem in output.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("content", out var content))
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("text", out var text))
                    {
                        var value = text.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException("OpenAI response did not contain output text.");
    }
}

internal sealed class OpenAiQuestionAnswerer(HttpClient httpClient, AppSettings settings) : IDisposable
{
    private const string QuestionPrompt = """
        Eres un asistente breve para unas gafas inteligentes.
        Si el texto del usuario es una pregunta o una peticion de informacion, responde en espanol natural.
        Usa la imagen de la ultima escena capturada cuando la pregunta pueda referirse a objetos, texto, detalles visuales, posiciones, colores o riesgos.
        Usa el analisis previo solo como resumen orientativo; si la pregunta pide un detalle concreto, revisa la imagen.
        Si aparece una mano haciendo un gesto de pinza con pulgar e indice, ignoralo salvo que el usuario pregunte explicitamente por la mano o el gesto.
        Si no hay contexto de escena y la pregunta depende de la vision, indica brevemente que primero necesitas una captura.
        Si el texto no es una pregunta ni una peticion de informacion, responde exactamente: NO_QUESTION.
        La respuesta debe ser util y lo bastante corta para escucharse en menos de quince segundos.
        """;

    public async Task<string> AnswerIfQuestionAsync(
        string transcript,
        SceneContext? sceneContext,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is required for voice question answering.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiApiKey);

        var sceneContextText = sceneContext is null
            ? "No hay una escena capturada todavia."
            : $"""
                Ultima escena capturada:
                - Fuente: {sceneContext.SourceName}
                - Hora local: {sceneContext.CapturedAt:HH:mm:ss}
                - Analisis: {sceneContext.Description}
                """;
        var sceneContextContent = sceneContext is null
            ? new object[]
            {
                new { type = "input_text", text = sceneContextText }
            }
            : new object[]
            {
                new { type = "input_text", text = sceneContextText },
                new
                {
                    type = "input_image",
                    image_url = "data:image/jpeg;base64," + Convert.ToBase64String(sceneContext.JpegBytes)
                }
            };

        var payload = new
        {
            model = settings.OpenAiModel,
            input = new object[]
            {
                new
                {
                    role = "system",
                    content = new object[]
                    {
                        new { type = "input_text", text = QuestionPrompt }
                    }
                },
                new
                {
                    role = "user",
                    content = sceneContextContent
                },
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = transcript }
                    }
                }
            }
        };

        request.Content = JsonContent(payload);
        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI question request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
        }

        var answer = ExtractResponseText(json).Trim();
        return answer.Equals("NO_QUESTION", StringComparison.OrdinalIgnoreCase) ? string.Empty : answer;
    }

    public void Dispose()
    {
    }

    private static HttpContent JsonContent(object payload)
    {
        return new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
    }

    private static string ExtractResponseText(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.TryGetProperty("output_text", out var outputText))
        {
            return outputText.GetString() ?? string.Empty;
        }

        if (document.RootElement.TryGetProperty("output", out var output))
        {
            foreach (var outputItem in output.EnumerateArray())
            {
                if (!outputItem.TryGetProperty("content", out var content))
                {
                    continue;
                }

                foreach (var contentItem in content.EnumerateArray())
                {
                    if (contentItem.TryGetProperty("text", out var text))
                    {
                        var value = text.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            return value;
                        }
                    }
                }
            }
        }

        throw new InvalidOperationException("OpenAI response did not contain output text.");
    }
}

internal sealed class OpenAiSpeechSynthesizer(HttpClient httpClient, AppSettings settings) : IDisposable
{
    public async Task<byte[]> CreateSpeechAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is required for speech synthesis.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/speech");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiApiKey);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                model = settings.OpenAiTtsModel,
                voice = "alloy",
                input = text,
                response_format = "mp3",
                speed = settings.OpenAiTtsSpeed
            }),
            Encoding.UTF8,
            "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = Encoding.UTF8.GetString(bytes);
            throw new InvalidOperationException($"OpenAI speech request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {body}");
        }

        return bytes;
    }

    public void Dispose()
    {
    }
}

internal sealed class OpenAiTranscriptionService(HttpClient httpClient, AppSettings settings) : IDisposable
{
    public async Task<string> TranscribeAsync(byte[] wavBytes, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(settings.OpenAiApiKey))
        {
            throw new InvalidOperationException("OPENAI_API_KEY is required for voice command transcription.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAiApiKey);

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(wavBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(fileContent, "file", "voice-command.wav");
        content.Add(new StringContent(settings.OpenAiTranscriptionModel), "model");
        content.Add(new StringContent("json"), "response_format");
        content.Add(new StringContent("El usuario puede decir: \"Gafas, captura\"."), "prompt");
        request.Content = content;

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"OpenAI transcription request failed: {(int)response.StatusCode} {response.ReasonPhrase}. {json}");
        }

        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty("text", out var text)
            ? text.GetString() ?? string.Empty
            : string.Empty;
    }

    public void Dispose()
    {
    }
}
