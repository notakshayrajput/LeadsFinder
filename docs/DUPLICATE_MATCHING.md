# Duplicate checking and AWS setup

The first matching stage compares saved source observations. It produces inspection reports; it does not yet create final leads, merge businesses, or delete observations. Select a finished job in the dashboard and use **Duplicate checks → Check duplicates**. Each report retains the exact field values compared, rule version, scores, reasons, and any AI suggestion.

## Deterministic comparison

- PostGIS selects candidates within 100 metres, plus normalized exact phone or website-host candidates outside that circle. Existing enrichment values are used when available.
- Name comparison removes punctuation, accents, and common lodging words, then combines normalized edit similarity and token Dice similarity. Reordered names remain comparable. Placeholder and generic names provide no distinctive-name evidence.
- Very similar distinctive names (similarity at least 0.92) within **40 metres** suggest a likely duplicate. Conflicting Indian postal codes or leading property numbers downgrade the suggestion to a possible duplicate.
- Other combinations of name, address, and contacts flag possible duplicates. Shared contact information alone does not establish identity; branches and booking services can share it.
- Missing addresses remain missing. Percentages shown in the UI are text similarity scores, not calibrated probabilities of business identity.

These starting thresholds need evaluation on labeled hotel pairs before automatic merging is introduced. Pairwise similarity is not transitive: A matching B and B matching C does not establish that all three are one business.

Checks cover up to 500 selected-job observations, 50 candidates per observation, and 2,000 unique comparisons. Reports explicitly flag truncation. Phone punctuation is removed, but country codes are retained: a local-format number and a country-prefixed number may not select each other outside the spatial radius. Name-only matching outside 100 metres is not currently a candidate strategy.

## Optional AWS comparison

The API calls **Amazon Bedrock Runtime Converse**. A selected pair includes names, addresses, coordinates, phone, email, website, and deterministic signals. Raw HTML and entire database rows are not sent. The model returns `same`, `different`, or `uncertain`, a reason, and exact supporting field values.

Returned row IDs and cited values are validated. `same` needs name evidence plus another supplied field. An incomplete response, invalid citation, timeout, or AWS error leaves a visible local comparison. Citations establish that values were supplied; they do not independently verify a business or prove a match. Every suggestion still requires inspection.

AWS is disabled by default. Even after configuration, calls require the dashboard's **Ask AWS** checkbox. At most five pairs are sent per check by default (configurable from one to ten); uncertain pairs are prioritized. A service error stops further requests in that check. Requests have a 30-second default timeout, an 800-output-token limit, and no automatic retries. The request count and token usage are retained in reports. This bounds calls, not the monetary price of a request.

## Configure your AWS account

1. Open [Amazon Bedrock](https://console.aws.amazon.com/bedrock/) and choose the AWS Region you want to use. Select a Converse-compatible model in the model catalog. Test it in the chat playground, then copy its **model ID** or **inference profile ID/ARN**. A small model such as Amazon Nova Lite is an initial candidate; confirm availability and evaluate it on actual lodging pairs before relying on its decisions.
2. Enable model access if your account requires it. Current AWS documentation describes automatic model access when the required Marketplace permissions and prerequisites are satisfied. Anthropic models additionally require a first-use use-case form. Model-access setup can be performed once by an account administrator; the application does not need Marketplace subscription permissions after access is enabled.
3. The invoking identity needs `bedrock:InvokeModel` for the selected model. Scope the permission to the model resource. For an inference profile, include the profile and underlying model resources required by AWS. Do not grant the application account administration permissions.
4. For this local prototype, open **Bedrock → API keys → Short-term API keys → Generate short-term API keys**. Generate it in the same Region as the runtime endpoint. It inherits your identity's permissions and expires with the console session, at most 12 hours. Save it locally in `.env` as described below.
5. Check the selected model's pricing and set an AWS budget alert if useful. Budget alerts are notifications, not a hard spending limit.

At the repository root, copy `.env.example` to `.env` if the local file does not exist yet. Edit these lines in `.env`:

```dotenv
DuplicateMatching__Bedrock__Region=ap-southeast-2
DuplicateMatching__Bedrock__ModelId=YOUR_MODEL_OR_INFERENCE_PROFILE_ID
DuplicateMatching__Bedrock__ApiKey=YOUR_BEDROCK_KEY
DuplicateMatching__Bedrock__Enabled=true
```

Restart the API after changing these settings. Refresh the dashboard, select a finished search job, enable **Ask AWS**, and run a check. A successful model call is the final access test; the dashboard's configured indicator only checks local configuration.

`.env` is ignored by Git and is loaded only by the .NET services; `.env.example` is the tracked template and must contain placeholders only. Nonblank `.env` values override appsettings and user-secrets; OS environment variables and command-line arguments override `.env`. Existing user-secrets remain supported. `AWS_BEARER_TOKEN_BEDROCK` is also accepted as a fallback when the canonical API-key setting is empty. Never commit a token or put it in browser configuration. This implementation uses bearer-token authentication; `AWS_PROFILE` alone does not authenticate it. For deployment, replace this local adapter's authentication with the AWS SDK credential chain and an IAM execution role or temporary credentials.

Information needed to connect: **Region, model/inference profile ID, and the fact that the key is stored locally in `.env`.** The key itself should not be pasted into chat. No database, S3 bucket, Bedrock agent, or training job needs to be created in AWS for this feature.

## APIs and verification

- `GET /api/duplicate-matching/config`: local readiness and comparison limits, without credentials.
- `POST /api/jobs/{id}/duplicate-checks` with `{ "useAi": false }` or `{ "useAi": true }`: run a bounded check and retain its report.
- `GET /api/jobs/{id}/duplicate-checks/latest`: load the most recent report.

Reports are stored in `duplicate_check_runs`, separately from source and enrichment rows. Configured external databases need the new EF migration applied explicitly. The bundled local development database applies pending migrations when the API starts. Requests are manual and synchronous; closing a request can cancel it, and a canceled check may have incurred AWS calls without producing a saved report. A durable processing queue is a later step.

The existing application is local and has no user authentication. These endpoints do not make admin decisions or modify lead values. Add authentication and authorization before exposing this workspace beyond local development or adding admin merge operations.

Run `dotnet test LeadsFinder.Matching.Tests` for focused rule and model-response tests. No test needs AWS credentials or makes a real model call.

An optional PostGIS smoke test runs when `LEADSFINDER_TEST_DB` is set to a migrated PostgreSQL connection string. It writes to session-local temporary tables that shadow the source, enrichment, and report tables, then rolls back; it does not insert fixtures into the real tables. It verifies spatial lookup, contact matches outside the radius, enriched contact values, pair deduplication, and report persistence.

## AWS references

- [Converse API and required permission](https://docs.aws.amazon.com/bedrock/latest/APIReference/API_runtime_Converse.html)
- [Generate a Bedrock API key](https://docs.aws.amazon.com/bedrock/latest/userguide/api-keys-generate.html)
- [Use a Bedrock API key](https://docs.aws.amazon.com/bedrock/latest/userguide/api-keys-use.html)
- [Model access prerequisites](https://docs.aws.amazon.com/bedrock/latest/userguide/model-access.html)
- [Bedrock pricing](https://aws.amazon.com/bedrock/pricing/)
