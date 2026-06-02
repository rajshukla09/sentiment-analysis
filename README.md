# Consumer Sentiment AI App

A take-home Consumer Sentiment AI application that accepts PDF files with consumer feedback, queues asynchronous sentiment analysis jobs, persists job state and results in SQLite, and displays readable results in a static Blazor WebAssembly client.

## Architecture

```text
Browser / GitHub Pages static site
  └── SentimentAnalysis.Client (Blazor WebAssembly)
        ├── POST /jobs multipart PDF
        ├── POST /jobs/batch multipart PDFs in one request
        ├── GET /jobs recent job list
        ├── GET /jobs/{id} polling
        └── GET /jobs/{id}/result when complete

Azure App Service / local API
  └── SentimentAnalysis.Api (ASP.NET Core Web API)
        ├── EF Core + SQLite: jobs and job_results
        ├── Persistent upload folder: one PDF per job ID
        ├── BackgroundService worker
        ├── PdfPig text extraction
        ├── Feedback parser
        └── OpenAI gpt-4o-mini JSON sentiment analyzer

SentimentAnalysis.Shared
  └── DTOs shared by API, client, and tests

SentimentAnalysis.Tests
  └── xUnit integration tests with temporary SQLite, fake PDF extractor, and fake LLM
```

## Local setup

Prerequisites:

- .NET 8 SDK
- An OpenAI API key for real analysis runs

Restore and build:

```bash
dotnet restore
dotnet build
```

The API creates its SQLite database automatically on startup using `EnsureCreated`.

## Required environment variables

The OpenAI key is never stored in the Blazor client and should never be committed. The API uses `gpt-4o-mini` for analysis.

For local API development, set:

```bash
export OpenAI__ApiKey="sk-..."
```

Optional local overrides:

```bash
export ConnectionStrings__DefaultConnection="Data Source=App_Data/consumer_sentiment.db"
export Storage__UploadsPath="App_Data/uploads"
export Cors__AllowedOrigins__0="https://localhost:5001"
```

## Run the API

```bash
dotnet run --project SentimentAnalysis.Api
```

Default local persistence:

- SQLite: `SentimentAnalysis.Api/App_Data/consumer_sentiment.db`
- Uploads: `SentimentAnalysis.Api/App_Data/uploads`

Production fallback paths are used when `/home/data` exists:

- SQLite: `/home/data/consumer_sentiment.db`
- Uploads: `/home/data/uploads`

## Run the Blazor client

Set the API base URL in `SentimentAnalysis.Client/wwwroot/appsettings.json`. The checked-in local launch settings use the API at `https://localhost:51995` and the client at `https://localhost:51997` / `http://localhost:51998`:

```json
{
  "ApiBaseUrl": "https://localhost:51995"
}
```

Run:

```bash
dotnet run --project SentimentAnalysis.Client
```

## Run tests

```bash
dotnet test
```

Tests avoid real OpenAI calls. They use a temporary SQLite database per test factory, disable the hosted background worker, inject a fake text extractor, and call the scoped job processor directly.

## API examples

List recent jobs:

```bash
curl https://localhost:51995/jobs
```

Create one job:

```bash
curl -X POST https://localhost:51995/jobs \
  -F "file=@sample-feedback.pdf;type=application/pdf"
```

Create multiple jobs in one request:

```bash
curl -X POST https://localhost:51995/jobs/batch \
  -F "files=@sample-feedback-1.pdf;type=application/pdf" \
  -F "files=@sample-feedback-2.pdf;type=application/pdf"
```

Response:

```json
{
  "jobId": "00000000-0000-0000-0000-000000000000",
  "status": "queued"
}
```

Check status:

```bash
curl https://localhost:51995/jobs/00000000-0000-0000-0000-000000000000
```

Get result after completion:

```bash
curl https://localhost:51995/jobs/00000000-0000-0000-0000-000000000000/result
```

## PDF format

Text-based PDFs should contain 1 to 50 repeated blocks:

```text
Feedback ID: fb_001
Comment: The product was easy to use, but delivery was slow.

Feedback ID: fb_002
Comment: Support answered quickly.
```

Comments may span multiple lines until the next `Feedback ID:` marker. Scanned/image-only PDFs fail gracefully with `No readable feedback text found in the PDF.`

## Design choices

- `POST /jobs` only validates and stores the PDF, creates a queued row, and returns immediately.
- The `QueuedJobWorker` creates a DI scope for each loop so `AppDbContext` is scoped correctly.
- `JobProcessor` owns lifecycle transitions: `queued` → `running` → `completed` or `failed`.
- Every job receives a unique GUID, a unique stored file path, and a single related result row, preventing overwrite or result mixing.
- The OpenAI prompt is compact and requests strict JSON only. The response is validated before the job is marked complete.
- The result endpoint returns `202 Accepted` for queued/running jobs, `400 Bad Request` for failed jobs, and `404 Not Found` for missing jobs.
- The Blazor home page lets a user select multiple PDFs, sends them to `POST /jobs/batch` in one multipart request, creates one queued API job per PDF, and polls the recent job list so queued, processing, processed, and failed states stay visible.

## Why the PDF is parsed before calling the LLM

The assignment provides a predictable feedback format, so the app extracts and parses feedback first, then sends only compact structured feedback records to the LLM. This reduces token usage, improves reliability, preserves feedback IDs for evidence, and makes the system easier to test and debug. The raw PDF is stored only as an input artifact for asynchronous processing and is not sent directly to the LLM.

## Why SQLite was used

SQLite keeps deployment simple and free-tier friendly while still providing durable persistence for job state and results. EF Core SQLite is enough for this take-home workload and can persist under `/home/data` on Azure App Service. The schema can be migrated to a server database later without changing API contracts.

## Azure App Service deployment notes

Configure these app settings in the Azure App Service backend:

```text
OpenAI__ApiKey = actual key
ConnectionStrings__DefaultConnection = Data Source=/home/data/consumer_sentiment.db
Storage__UploadsPath = /home/data/uploads
Cors__AllowedOrigins__0 = deployed Blazor client URL
```

Publish the API:

```bash
dotnet publish SentimentAnalysis.Api -c Release -o publish-api
```

Deploy `publish-api` to Azure App Service. Ensure the App Service has write access to `/home/data`.

## GitHub Pages / static publish notes for Blazor WASM

Set `SentimentAnalysis.Client/wwwroot/appsettings.Production.json` before publishing:

```json
{
  "ApiBaseUrl": "https://<api-app-name>.azurewebsites.net"
}
```

Publish the client:

```bash
dotnet publish SentimentAnalysis.Client -c Release -o publish-client
```

Deploy `publish-client/wwwroot` to GitHub Pages or another static host. Add the final static origin to `Cors__AllowedOrigins__0` on the API.

## Known limitations

- PDF extraction targets text-based PDFs; scanned image PDFs require OCR and are not supported.
- The background worker is single-process and simple by design. Multi-instance deployments should add a stronger job claiming strategy or a queue.
- `EnsureCreated` is used for simplicity. Production schema evolution should use EF Core migrations.
- The app stores uploaded PDFs on disk and does not implement retention or deletion policies.
