# Submission Notes

- Deployed frontend URL: `https://<your-github-username>.github.io/sentiment-analysis/`
- Deployed backend URL: `https://<api-app-name>.azurewebsites.net`
- Secrets are configured only on the API host via environment variables or Azure App Service app settings, especially `OpenAI__ApiKey`. The Blazor client contains only `ApiBaseUrl` and never contains the OpenAI key.
- Async processing works by having `POST /jobs` persist the uploaded PDF and a `queued` job row immediately. `QueuedJobWorker` runs in the API process, creates a scoped `AppDbContext`, finds queued jobs by creation time, marks each as `running`, extracts/parses feedback, calls `ISentimentAnalyzer`, saves a `JobResult`, and marks the job `completed` or `failed`.
- Concurrency isolation is handled with per-job GUIDs, per-job stored PDF paths, persisted status fields, and a one-to-one `JobResult` row keyed by `JobId`. Results are loaded by `JobId`, so one job cannot overwrite another job's output.
- To demo 3 simultaneous jobs, open three browser tabs or run three `curl -F "file=@sample.pdf;type=application/pdf" <api>/jobs` commands quickly. Copy each returned job ID into the status page or open `/job/{id}` for each one; each should move independently from `queued` to `running` to `completed` or `failed`.
