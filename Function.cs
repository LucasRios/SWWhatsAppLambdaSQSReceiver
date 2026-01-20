using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SQS;
using Amazon.SQS.Model;
using Amazon.Lambda;
using Amazon.Lambda.Model;
using System.Net.Http.Headers;
using System.Text;

// Configura o serializador JSON padrão para a Lambda
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace SWWhatsAppLambdaSQSReceiver
{
    public class SqsProcessor
    {
        // Instâncias estáticas para otimizar o "Warm Start" da Lambda e reutilizar conexões TCP
        private static readonly IAmazonS3 _s3Client = new AmazonS3Client();
        private static readonly IAmazonSQS _sqsClient = new AmazonSQSClient();
        private static readonly AmazonLambdaClient _lambdaClient = new AmazonLambdaClient();
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

        // Variáveis de configuração (Devem ser preenchidas via variáveis de ambiente no console AWS)
        private static readonly string BUCKET_NAME = Environment.GetEnvironmentVariable("BUCKET_NAME") ?? "<BUCKET_NAME>";
        private static readonly string PROCESSED_QUEUE_URL = Environment.GetEnvironmentVariable("PROCESSED_QUEUE_URL") ?? "<PROCESSED_QUEUE_URL>";
        private const string CREDENTIALS_LAMBDA_NAME = "<CREDENTIALS_LAMBDA_NAME>";

        /// <summary>
        /// Ponto de entrada principal da Lambda acionada pelo SQS
        /// </summary>
        public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
        {
            if (evnt?.Records == null) return;

            foreach (var message in evnt.Records)
            {
                try
                {
                    await ProcessMessageAsync(message, context);
                }
                catch (Exception ex)
                {
                    // Loga o erro e relança a exceção para que a mensagem volte para a fila (Retry/DLQ)
                    context.Logger.LogLine($"[FATAL] Erro ao processar MessageId {message.MessageId}: {ex.Message}");
                    throw;
                }
            }
        }

        /// <summary>
        /// Orquestra a lógica de identificação do provedor e processamento
        /// </summary>
        private async Task ProcessMessageAsync(SQSEvent.SQSMessage message, ILambdaContext context)
        {
            string jsonPayload = message.Body;
            if (string.IsNullOrWhiteSpace(jsonPayload)) return;

            var jsonNode = JsonNode.Parse(jsonPayload);
            if (jsonNode == null) return;

            // Lógica de detecção do tipo de Webhook
            string channelId = jsonNode["channel_id"]?.ToString();
            bool isOfficialApi = jsonNode["object"] != null && jsonNode["entry"] != null;

            if (!string.IsNullOrEmpty(channelId))
            {
                context.Logger.LogLine($"[INFO] Whapi detectado. Channel: {channelId}");
                jsonPayload = await ProcessarWhapi(jsonNode, channelId, context);
            }
            else if (isOfficialApi)
            {
                context.Logger.LogLine($"[INFO] Meta Oficial detectada.");
                jsonPayload = await ProcessarMetaOficial(jsonNode, context);
            }
            else
            {
                context.Logger.LogLine("[WARN] Formato de JSON desconhecido ignorado.");
                return;
            }

            // Envia o JSON processado (com links do S3) para a fila de escrita final
            await _sqsClient.SendMessageAsync(new SendMessageRequest
            {
                QueueUrl = PROCESSED_QUEUE_URL,
                MessageBody = jsonPayload
            });
        }

        /// <summary>
        /// Processa mensagens vindas da plataforma Whapi
        /// </summary>
        private async Task<string> ProcessarWhapi(JsonNode jsonNode, string channelId, ILambdaContext context)
        {
            try
            {
                var messagesArray = jsonNode["messages"]?.AsArray();
                if (messagesArray == null || messagesArray.Count == 0) return jsonNode.ToJsonString();

                var firstMsg = messagesArray[0];
                string msgTipo = firstMsg?["type"]?.ToString();
                var tiposMidia = new HashSet<string> { "image", "video", "audio", "voice", "document" };

                // Se for mídia, faz o download e atualiza o link
                if (msgTipo != null && tiposMidia.Contains(msgTipo))
                {
                    var midiaNode = firstMsg[msgTipo];
                    string urlOriginal = midiaNode?["link"]?.ToString();

                    if (!string.IsNullOrEmpty(urlOriginal))
                    {
                        string novaUrlS3 = await DownloadESalvarNoS3(urlOriginal, channelId, msgTipo, null, context);
                        midiaNode["link"] = novaUrlS3;
                    }
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"[ERROR] Falha processamento Whapi: {ex.Message}");
            }
            return jsonNode.ToJsonString();
        }

        /// <summary>
        /// Processa mensagens vindas da API Oficial da Meta (Facebook)
        /// </summary>
        private async Task<string> ProcessarMetaOficial(JsonNode jsonNode, ILambdaContext context)
        {
            try
            {
                var entry = jsonNode["entry"]?[0];
                string metaBusinessId = entry?["id"]?.ToString();
                var message = entry?["changes"]?[0]?["value"]?["messages"]?[0];

                if (message == null) return jsonNode.ToJsonString();

                string msgTipo = message["type"]?.ToString();
                var tiposMidia = new HashSet<string> { "image", "video", "audio", "voice", "document" };

                if (msgTipo != null && tiposMidia.Contains(msgTipo))
                {
                    var midiaNode = message[msgTipo];
                    string mediaId = midiaNode?["id"]?.ToString();

                    if (!string.IsNullOrEmpty(mediaId))
                    {
                        // A Meta exige um Token de acesso para baixar mídias
                        string token = await ObterTokenMeta(metaBusinessId, context);

                        if (!string.IsNullOrEmpty(token))
                        {
                            // Endpoint para buscar mídia (Exemplo via Chakra ou direto Meta)
                            string urlChakra = $"https://api.chakrahq.com/v1/whatsapp/v19.0/media/{mediaId}/show";
                            string novaUrlS3 = await DownloadESalvarNoS3(urlChakra, metaBusinessId, msgTipo, token, context);

                            // Atualiza o JSON com a URL estável do S3
                            midiaNode["url"] = novaUrlS3;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"[ERROR] Falha processamento Meta: {ex.Message}");
            }

            return jsonNode.ToJsonString();
        }

        /// <summary>
        /// Chama outra Lambda de forma síncrona para obter o token de acesso da Meta
        /// </summary>
        private async Task<string> ObterTokenMeta(string metaId, ILambdaContext context)
        {
            try
            {
                var request = new InvokeRequest
                {
                    FunctionName = CREDENTIALS_LAMBDA_NAME,
                    Payload = JsonSerializer.Serialize(new { MetaId = metaId })
                };

                var response = await _lambdaClient.InvokeAsync(request);
                var payload = Encoding.UTF8.GetString(response.Payload.ToArray());

                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;

                // Verifica se a Lambda de credenciais retornou sucesso
                if (root.TryGetProperty("Sucesso", out var sucesso) && sucesso.GetBoolean())
                {
                    return root.GetProperty("Token").GetString();
                }
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"[ERROR] Erro ao invocar Broker de Credenciais: {ex.Message}");
            }
            return null;
        }

        /// <summary>
        /// Realiza o download do arquivo e faz o upload para o S3
        /// </summary>
        private async Task<string> DownloadESalvarNoS3(string urlOrigem, string folderId, string msgTipo, string token, ILambdaContext context)
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                var requestMessage = new HttpRequestMessage(HttpMethod.Get, urlOrigem);

                // Configura autorização e Headers para evitar bloqueios de API
                if (!string.IsNullOrEmpty(token))
                {
                    string cleanToken = token.Replace("Bearer ", "", StringComparison.OrdinalIgnoreCase).Trim();
                    requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cleanToken);
                    requestMessage.Headers.Add("User-Agent", "PostmanRuntime/7.29.2");
                }

                using var response = await _httpClient.SendAsync(requestMessage, HttpCompletionOption.ResponseHeadersRead, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    context.Logger.LogLine($"[ERROR] Falha no download. Status: {response.StatusCode}");
                    return urlOrigem; // Retorna a original em caso de falha
                }

                long? contentLength = response.Content.Headers.ContentLength;
                using var contentStream = await response.Content.ReadAsStreamAsync();

                // Gera o caminho do arquivo: PastaID/Ano-Mes/GUID.extensao
                string extensao = ObterExtensao(urlOrigem, msgTipo);
                string key = $"{folderId}/{DateTime.Now:yyyy-MM}/{Guid.NewGuid():N}{extensao}";

                var putRequest = new PutObjectRequest
                {
                    BucketName = BUCKET_NAME,
                    Key = key,
                    InputStream = contentStream,
                    ContentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream"
                };

                if (contentLength.HasValue)
                    putRequest.Headers.ContentLength = contentLength.Value;

                await _s3Client.PutObjectAsync(putRequest, cts.Token);

                // Retorna a URL pública/interna do S3
                return $"https://{BUCKET_NAME}.s3.sa-east-1.amazonaws.com/{key}";
            }
            catch (Exception ex)
            {
                context.Logger.LogLine($"[ERROR] Erro S3/Download: {ex.Message}");
                return urlOrigem;
            }
        }

        private string ObterExtensao(string url, string tipo)
        {
            return tipo.ToLower() switch
            {
                "image" => ".jpg",
                "video" => ".mp4",
                "audio" => ".ogg",
                "voice" => ".ogg",
                "document" => ".pdf",
                _ => ".bin"
            };
        }
    }
}