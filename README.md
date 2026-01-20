# SW WhatsApp Lambda SQS Processor

Esta AWS Lambda em .NET 6/8 é responsável por processar webhooks de mensagens do WhatsApp provenientes tanto do **Whapi** quanto da **API Oficial da Meta**. 

## 🚀 Funcionalidades

- **Normalização de Mídia**: Detecta arquivos de imagem, vídeo, áudio e documentos.
- **Integração com S3**: Faz o download automático de arquivos de mídia das APIs originais e armazena em um bucket S3 próprio para persistência.
- **Broker de Credenciais**: Invoca uma Lambda secundária para obtenção segura de Tokens de API.
- **Pipeline SQS**: Consome mensagens de uma fila de entrada e envia os dados processados para uma fila de saída (pronta para escrita em banco de dados).

## 🛠️ Arquitetura

1. **Trigger**: SQS Queue (Mensagens brutas).
2. **Processamento**: 
   - Identificação do provedor (Meta ou Whapi).
   - Download de mídia via `HttpClient`.
   - Upload para S3 via `AWSSDK.S3`.
3. **Saída**: SQS Queue (JSON enriquecido com links do S3).

## ⚙️ Configuração

As seguintes variáveis de ambiente (ou constantes) devem ser configuradas:

| Variável | Descrição |
|----------|-----------|
| `BUCKET_NAME` | Nome do bucket S3 onde as mídias serão salvas. |
| `PROCESSED_QUEUE_URL` | URL da fila SQS que receberá o JSON final. |
| `CREDENTIALS_LAMBDA_NAME` | Nome da Lambda responsável por retornar os tokens da Meta. |

## 📦 Dependências Principais

- `Amazon.Lambda.Core`
- `Amazon.Lambda.SQSEvents`
- `AWSSDK.S3`
- `AWSSDK.SQS`
- `AWSSDK.Lambda`

## 📝 Como publicar

```bash
dotnet lambda deploy-function SWWhatsAppLambdaSQSReceiver
