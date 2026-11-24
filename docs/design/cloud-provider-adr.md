# ADR: Cloud provider choice for the ReelBolt SaaS launch (WP F0)

- **Status:** PROPOSED. The final choice is a founder gate (see "Founder decisions needed").
- **Date:** 2026-10-05. Every price below was read on this date unless the row says otherwise.
- **Decides:** control plane host, compute plane host, Postgres, RabbitMQ, Qdrant, KMS, secrets, observability, edge, and the cheap S1 topology.
- **Related:** the SaaS launch plan (not in the public repository).

## 1. Context and constraints

- The operator is a small independent team on a tight budget, so the plan is to avoid large fixed spend until the product pays for itself.
- **S1 ships first and runs compute on the existing Local target.** That is the `workflow-engine` container (which runs ffmpeg itself for `VideoAnalyze`/`VideoCompile`) plus `sandbox-executor` (which holds the Docker socket and starts the Remotion containers) on Docker-capable VMs, scaled by hand. The elastic worker pool and the autoscaler (F12) arrive in S2. The chosen topology must therefore be cheap on day one and have a path to the D19 shape (managed k8s control plane plus elastic worker VMs).
- Fixed by earlier decisions: Cloudflare R2 for objects (D19), nginx stays the in-cluster router (F15), the Data Protection application name and purpose string never change (D20), a deploy never cancels healthy runs (D21).
- Workload facts used in this ADR (measured below): Remotion sandbox containers are limited to 2 CPU and 2 GB each (`SANDBOX_CPU_LIMIT=2`, `SANDBOX_MEMORY_LIMIT=2g` in `sandbox/main.go`). ffmpeg runs in the engine container with `VideoEditing:MaxConcurrentJobs=1`.

## 2. What a "render" costs (measured on the dev database, read-only)

Queried 2026-10-05 from `workflow_executions` / `workflow_step_results` in the dev stack (an AMD Ryzen 9 5950X, 16 cores, running whisper and embeddings locally, so absolute times are pessimistic for ASR and optimistic for ffmpeg parallelism).

| Metric | Value |
|---|---|
| Completed executions (status `Passed`), 2026-09-27 to 2026-10-05 | 60 |
| Execution wall time, mean / p50 / p90 | 339 s / 241 s / 631 s |
| `VideoAnalyze` step (non-cached, n=57), mean / p50 / p90 | 143.7 s / 141.6 s / 270.4 s (includes a local CPU whisper call) |
| `VideoCompile` step (n=154), mean / p50 / p90 | 23.1 s / 14.7 s / 36.6 s |
| `Voiceover` step (n=158), mean | 31.8 s (external TTS, not our CPU) |
| `AuthorAgent` step (n=21; the Remotion authoring and render agent), mean / p90 | 198 s / 320 s |
| LLM tokens per execution (75 executions), mean / median | 1.30 M / 16 k (a few promo runs dominate the mean) |

**Modelling assumption.** One "render" is one workflow execution. CPU-bound time per render is about **3.0 node-minutes of an 8 vCPU node** at full utilisation. That is roughly: ffmpeg analysis about 85 s (the 144 s minus ASR, which moves to a hosted provider in F6), compile 23 s, and Remotion sandbox work of about 60 s on a 2 CPU slot. Range 2 to 5 node-minutes. Billed node-hours are inflated for idle and packing: utilisation 35% at 50 renders/day, 50% at 500, 65% at 5,000. Billed node-hours per month are therefore about **214 / 1,500 / 11,538**.

**Storage assumption.** 0.5 GB stored per render (outputs, artifacts and the sandbox journal), 30 days' retention (F10 makes retention a per-plan lever). That gives about 0.75 TB / 7.5 TB / 75 TB stored. About 200 write operations and 1,000 read operations per render.

**Important caveat.** LLM tokens (1.3 M per execution on average today) will cost far more than this infrastructure at any load point. They are priced by D10/B13 from metering and are excluded here. Hosted ASR is shown separately as a pass-through.

Exchange-rate assumption: US$1 = EUR 0.92. This was not looked up. All US$ prices are converted with it.

## 3. Price comparison (EU regions only)

Confidence tags: **V** = read from the provider's own page or public catalogue API on 2026-10-05. **T** = third-party price aggregator, same date. **E** = estimate or typical list price I could not verify on the provider's page; confirm before committing.

### 3.1 Render node (8 vCPU, 16-32 GB)

| Provider / type | vCPU kind | EUR/h | EUR per vCPU-h | Spot | Conf. | Source |
|---|---|---|---|---|---|---|
| **Hetzner CX43** (8 vCPU, 16 GB) | shared | 0.0256 | 0.0032 | none | V | [Hetzner price adjustment](https://docs.hetzner.com/general/infrastructure-and-availability/price-adjustment/) |
| Hetzner CX53 (16 vCPU, 32 GB; EUR 29.49/mo) | shared | 0.0473 | 0.0030 | none | V | same |
| **Hetzner CCX33** (8 vCPU, 32 GB) | dedicated | 0.2219 | 0.0277 | none | V | same |
| Scaleway BASIC2-A8C-32G | shared | 0.1378 | 0.0172 | none | V | [Scaleway instance catalogue API](https://api.scaleway.com/instance/v1/zones/fr-par-1/products/servers) |
| Scaleway COMPUTE3-X8C-16G | dedicated | 0.2341 | 0.0293 | none | V | same |
| OVHcloud b3-32 (8 vCPU, 32 GB) | general purpose | 0.2046 | 0.0256 | none | V | [OVHcloud catalogue API](https://api.ovh.com/1.0/order/catalog/public/cloud?ovhSubsidiary=IE) |
| OVHcloud c3-16 (8 vCPU, 16 GB) | dedicated | 0.1825 | 0.0228 | none | V | same |
| AWS eu-central-1 c7a.2xlarge (8 vCPU, 16 GB) | dedicated | 0.43 (US$0.469) | 0.054 | 0.215 (US$0.234) | T | [cloudchipr/doit aggregators via search](https://instances.cloudchipr.com/aws/ec2/c7a.2xlarge) |
| GCP europe-west4 c3-standard-8 (8 vCPU, 32 GB) | dedicated | 0.39 (US$0.423) | 0.049 | 0.157 (US$0.171) | T | [devzero.io](https://www.devzero.io/instances/gcp/c3-standard-8) |

Notes:
- **Hetzner repriced on 15 June 2026.** CX roughly +30-40%, CCX about +120%, CPX +140-175% (CCX33 went from EUR 62.49 to 138.49/month). This is a real price-risk signal and the reason the compute layer must stay behind a `Provisioner` interface (F12). The cheap Hetzner option is now the **shared-vCPU CX line**, not the dedicated CCX line. The 8x gap between CX43 and CCX33 is the biggest number in this ADR. Whether shared vCPUs hold up under sustained ffmpeg encodes is unmeasured and must be benchmarked (see Founder decisions).
- Scaleway's catalogue API did not list the POP2 line; the BASIC2 and COMPUTE3 prices above are what that endpoint returned. Scaleway says its list prices include no egress fees ([pricing](https://www.scaleway.com/en/pricing/virtual-instances/)).
- AWS/GCP spot is cheap but can be reclaimed mid-render. Hetzner needs no spot.
- All providers bill per hour (Hetzner and Scaleway to the second or hour with a monthly cap) and all expose an API for create/delete. **Only AWS and GCP have native autoscaling groups.** Hetzner, Scaleway and OVH need our own autoscaler (F12, `hcloud-go` or the Scaleway SDK behind `Provisioner`).

### 3.2 Managed services

| Item | Hetzner | Scaleway (fr-par) | OVHcloud | AWS eu-central-1 | GCP europe-west |
|---|---|---|---|---|---|
| Managed k8s control-plane fee | no managed k8s | **Kapsule mutualized: free**; Dedicated-4 EUR 80.30/mo (V, [Scaleway containers pricing](https://www.scaleway.com/en/pricing/containers/)) | Free plan: no control-plane fee, single zone (V, [OVH MKS plans](https://docs.ovhcloud.com/en/guides/public-cloud/containers-orchestration/managed-kubernetes/mks-plans)) | EKS US$0.10/h = about EUR 67/mo (V, [EKS pricing](https://aws.amazon.com/eks/pricing/)) | GKE zonal US$0.10/h, regional US$0.30/h, one free zonal cluster per billing account (E, [GKE pricing](https://cloud.google.com/kubernetes-engine/pricing)) |
| Managed Postgres with PITR | none (self-host) | DB-DEV-S 2 vCPU/2 GB EUR 0.0156/h (about EUR 11/mo); DB-POP2-2C-8G EUR 0.1434/h (about EUR 105/mo); HA standby adds 48-64% of the node price; backups EUR 0.03/GB-mo (V, [Scaleway managed DB pricing](https://www.scaleway.com/en/pricing/managed-databases/)) | Essential db1-4 EUR 0.0746/h (about EUR 54/mo); Business db1-4 EUR 0.0949/h (about EUR 69/mo); Business db1-7 EUR 0.1899/h (V, OVH catalogue API above) | RDS db.t4g.medium (2 vCPU, 4 GB) US$0.065/h single-AZ (T, [vantage](https://instances.vantage.sh/aws/rds/db.t4g.medium?region=eu-central-1&os=PostgreSQL)); Multi-AZ about 2x (E) | Cloud SQL, per vCPU and per GB RAM, HA about 2x compute (E, [Cloud SQL pricing](https://cloud.google.com/sql/pricing)) |
| Managed RabbitMQ | none | none native (CloudAMQP lists Scaleway as a host) | none | Amazon MQ: pricing page not retrievable; US East mq.m5.large about US$0.288/h per node, 3-node cluster about US$630/mo (T, [cloudburn](https://cloudburn.io/blog/amazon-mq-pricing)); Frankfurt rate unverified | none |
| KMS | none | Key Manager and Secret Manager exist; price page returned 404 (unverified) | KMS exists (not researched) | **KMS US$1/key/mo, US$0.03 per 10k requests, 20k free (V, [AWS KMS pricing](https://aws.amazon.com/kms/pricing/))**; Secrets Manager about US$0.40/secret/mo (E) | Cloud KMS about US$0.06/key version/mo (E) |
| Intra-provider / egress to R2 | included traffic 20-30 TB per server, extra EUR 1/TB (T, [agentdeals](https://agentdeals.dev/hetzner-pricing-2026)); load balancer LB11 EUR 7.49/mo (T, same) | list prices carry no egress fee (V) | not researched | **internet egress about US$0.09/GB, and traffic to R2 counts as internet egress (E)** | same class of charge (E) |

Hetzner details (V, [price adjustment page](https://docs.hetzner.com/general/infrastructure-and-availability/price-adjustment/)): CX33 EUR 8.49/mo, CX43 EUR 15.99/mo, CX53 EUR 29.49/mo, CCX33 EUR 138.49/mo, all excluding VAT and IPv4. CX43/CX53 vCPU and RAM figures (8 vCPU/16 GB and 16 vCPU/32 GB) come from Hetzner's product line as I know it; the fetched page listed only prices. Confirm before ordering.

### 3.3 Third-party services (provider-independent)

| Service | Price | Conf. | Source |
|---|---|---|---|
| Cloudflare R2 | storage US$0.015/GB-mo; Class A (writes) US$4.50/M; Class B (reads) US$0.36/M; **egress free**; free tier 10 GB, 1 M Class A, 10 M Class B per month; Infrequent Access US$0.01/GB-mo with 30-day minimum | V | [R2 pricing](https://developers.cloudflare.com/r2/pricing/) |
| CloudAMQP RabbitMQ shared | Little Lemur free (1 M msgs/mo, 20 connections); **Tough Tiger US$19/mo (10 M msgs/mo, 100 connections, 1,000 queues)** | T | [CloudAMQP pricing, via search](https://www.cloudamqp.com/pricing) |
| CloudAMQP RabbitMQ dedicated | Sassy Squirrel US$50/mo (500 msg/s) up to Raging Rhino US$5,499/mo; multi-AZ clusters on higher tiers; VPC US$99/mo; hosts on AWS, Azure, GCP, DigitalOcean, Scaleway | V | same |
| Qdrant Cloud | free forever tier: 1 node, 0.5 vCPU, 1 GB RAM, 4 GB disk; Standard is usage-based on AWS, Azure and GCP with backups; the hourly rate is only in their calculator (not retrievable). Paid cluster costs below are estimates | V (free tier), E (paid) | [Qdrant pricing](https://qdrant.tech/pricing/) |
| Grafana Cloud | free: 10k metric series, 50 GB logs, 50 GB traces (14-day retention); Pro: US$19/mo platform fee plus usage (US$6.50 per 1k series, logs and traces per GB) | V | [Grafana pricing](https://grafana.com/pricing/) |
| Hosted ASR | OpenAI gpt-4o-mini-transcribe US$0.003/min, whisper-1 and gpt-4o-transcribe US$0.006/min | T | [diyai.io](https://diyai.io/ai-tools/speech-to-text/openai-whisper-api-pricing-2026/) |
| Hosted embeddings | OpenAI text-embedding-3-small US$0.02 per M tokens (supports `dimensions=1024`, which F6 needs) | T | [costbench](https://www.costbench.com/software/embedding-apis/openai-embeddings/) |

Queue-volume note: CloudAMQP Tough Tiger caps at 10 M messages per month. I assume about 200 broker messages per execution (step events, progress, chat turns). That is 0.3 M/month at 50 renders/day (fits, even the free tier), 3 M at 500/day (fits Tough Tiger), 30 M at 5,000/day (needs a dedicated plan). This per-execution figure is an estimate, not measured.

### 3.4 GDPR and data-processing notes

Not re-verified this session; confirm each at signing.

| Vendor | Entity | DPA / transfer position | Action |
|---|---|---|---|
| Hetzner | Hetzner Online GmbH (DE) | EU entity; DPA available in the customer console. Data stays in DE/FI | Sign the DPA, choose FSN/NBG/HEL |
| Scaleway | Scaleway SAS (FR, Iliad group) | EU entity; DPA accepted online | Accept DPA, choose fr-par |
| OVHcloud | OVH SAS (FR) | EU entity, DPA in the contract | Not selected |
| AWS | US parent, EU entities | DPA in the service terms; US parent means CLOUD Act exposure; EU Data Privacy Framework applies | Only for one KMS key (wrapped key material, no personal data), if chosen |
| GCP | US parent | as AWS | Not selected |
| Cloudflare (R2, proxy, DNS) | US company | DPA with Standard Contractual Clauses, DPF-certified. **Create the R2 bucket in the EU jurisdiction** so objects stay in the EU (F5 must use the `.eu.r2.cloudflarestorage.com` endpoint and a jurisdiction-scoped bucket, not the default location) | Sign DPA, EU jurisdiction |
| CloudAMQP | 84codes AB (SE) | EU entity | Sign DPA, pick an EU region |
| Qdrant | Qdrant Solutions GmbH (DE) | EU entity; pick an EU region | Sign DPA |
| Grafana Cloud | Grafana Labs (US) | DPF/SCC; choose an EU stack; spans carry `org_id` but no user content (F9) | Sign DPA, EU stack |
| ASR / LLM providers | various (US) | their own DPAs; sub-processors of the tenant's content | Out of scope here; list in the privacy policy (H7) |

Customer content (source video, transcripts) will transit hosted ASR and LLM providers regardless of where we host. The privacy policy (C0/H7) has to list every sub-processor. Hosting on EU-owned providers (Hetzner, Scaleway) is a clean story and a sales point, but it does not change that.

## 4. Monthly cost model

Inputs: 3.0 node-min per render, utilisation 35/50/65%, R2 at 0.5 GB per render for 30 days, assumptions in section 2. Compute rows below show the cost of the render nodes alone at each provider's on-demand (and spot) price. Figures are EUR/month excluding VAT.

### 4.1 Compute plane only (render nodes)

| Provider and type | 50/day (214 h) | 500/day (1,500 h) | 5,000/day (11,538 h) |
|---|---|---|---|
| Hetzner CX43 (shared) | 5 | 38 | 295 |
| Hetzner CCX33 (dedicated) | 48 | 333 | 2,560 |
| Scaleway BASIC2-A8C-32G | 30 | 207 | 1,590 |
| Scaleway COMPUTE3-X8C-16G | 50 | 351 | 2,701 |
| OVH b3-32 | 44 | 307 | 2,361 |
| OVH c3-16 | 39 | 274 | 2,106 |
| AWS c7a.2xlarge on-demand | 92 | 647 | 4,979 |
| AWS c7a.2xlarge spot | 46 | 323 | 2,484 |
| GCP c3-standard-8 on-demand | 83 | 584 | 4,490 |
| GCP c3-standard-8 spot | 34 | 236 | 1,815 |

At 50/day the choice of compute provider is worth tens of euros. At 5,000/day it is the difference between about EUR 300 and EUR 5,000.

### 4.2 Whole platform, three stacks

Each stack is right-sized per load. The 50/day column uses the S1 topology (section 5), not a full k8s shape. Excluded: LLM tokens and the Paddle fee. Included as lines: R2, Postgres, queue, Qdrant, observability, KMS, backup bucket, load balancer.

| Line (EUR/month) | **A. Hybrid** (Hetzner pool and S1 VMs, Scaleway k8s and Postgres) | | | **B. All-Scaleway** | | | **C. AWS-only** (EKS, RDS, spot nodes, R2) | | |
|---|---|---|---|---|---|---|---|---|---|
| Load (renders/day) | 50 | 500 | 5,000 | 50 | 500 | 5,000 | 50 | 500 | 5,000 |
| Control plane | 8 (CX33) | 101 (2x Scaleway A4C-16G, Kapsule free) | 302 (3x A8C-32G) | 38 | 101 | 302 | n/a | 403 (EKS plus 2 nodes) | 967 |
| Compute (render) | 29 (CX53, always on, in-engine) | 38 (CX43 pool) | 295 (CX43 pool) | 101 | 207 | 1,590 | n/a | 323 (spot) | 2,484 (spot) |
| Postgres with PITR | 105 (DB-POP2-2C-8G, no HA) | 163 (same, HA) | about 650 (larger node, HA, estimate) | 105 | 163 | 650 | n/a | 250 | 700 |
| RabbitMQ | 0 (container on the control VM) | 17 (Tough Tiger) | about 190 (dedicated, estimate) | 0 | 17 | 190 | n/a | 17 (CloudAMQP; Amazon MQ cluster would be about 580) | 190 |
| R2 storage and ops | 10 | 113 | 1,201 | 10 | 113 | 1,201 | n/a | 113 | 1,201 |
| Qdrant | 0 (free tier) | about 30 (estimate) | about 120 (estimate) | 0 | 30 | 120 | n/a | 30 | 120 |
| Observability (Grafana) | 0 (free tier) | 0 | about 100 (Pro, estimate) | 0 | 0 | 100 | n/a | 0 | 100 |
| KMS, LB, backup bucket, IPv4 | about 10 | about 31 | about 111 | about 10 | about 31 | about 111 | n/a | 190 (adds NAT and ALB) | 300 |
| Cross-cloud egress to R2 (AWS to internet) | 0 | 0 | 0 | 0 | 0 | 0 | n/a | 124 | 1,242 |
| **Total** | **about 162** | **about 493** | **about 2,970** | **about 264** | **about 662** | **about 4,260** | n/a | **about 1,450** | **about 7,300** |

Reading the table:
- Hybrid (A) is the cheapest at every load. Putting only the render nodes on a different provider from the control plane saves about EUR 1,300/month at 5,000/day compared to all-Scaleway.
- AWS (C) is about 2.9x the hybrid at 500/day and 2.5x at 5,000/day. It stays much more expensive even though R2 egress being free is the reason to keep R2. **The main trap is that traffic from AWS compute to R2 is billed as internet egress**; the "free R2 egress" saving does not reach an AWS-hosted compute plane. Amazon MQ (3 nodes) would add about EUR 580 where CloudAMQP costs EUR 17. C's 50/day column is "n/a" because nobody would run EKS at that load.
- **Dedicated-vCPU sensitivity.** If shared vCPUs fail the benchmark and the pool needs CCX33, hybrid compute becomes EUR 48 / 333 / 2,560, so hybrid totals rise to about EUR 271 / 788 / 5,235. Still below AWS at every load, but above all-Scaleway (using its shared instances) at 5,000/day, so the benchmark result matters.
- **R2 is the dominant line at 5,000/day (about EUR 1,200).** Cutting retention from 30 to 7 days would cut the stored-GB part by roughly three quarters. The storage assumption (0.5 GB per render) is the least certain input in the model; measure it in staging (X1).
- Hosted ASR pass-through (gpt-4o-mini-transcribe, 3 min of audio per render): about EUR 12 / 124 / 1,242 per month. It is metered into credits (B), not an infrastructure cost.
- Estimates in the table (marked "estimate") carry roughly +/- 30%. The bare compute and R2 figures are exact arithmetic on the cited prices.

## 5. S1 minimal launch topology

S1 runs on the existing Local compute target, with managed Postgres and R2 (`01-schedule.md` section 1). Nothing here needs Kubernetes, the runner gateway or the autoscaler.

```
Cloudflare (Free plan: DNS, TLS, proxy, WAF)  --  browser uploads go direct to R2 via presigned PUT
        |
  Hetzner (DE)                                     Scaleway fr-par
  +-- VM1 "control"  CX33 (4 vCPU/8 GB)            +-- Managed PostgreSQL (PITR)
  |     nginx, web, site, docs, go-api,            |
  |     inference api, mcp, rabbitmq (container)   Cloudflare R2 (EU jurisdiction)
  +-- VM2 "engine"   CX53 (16 vCPU/32 GB)          Qdrant Cloud (free tier, EU)
        workflow-engine + sandbox-executor         Grafana Cloud free (EU stack)
        (Docker host, ffmpeg + Remotion sandboxes) Hosted ASR + embeddings (OpenAI-compatible)
                                                   AWS KMS eu-central-1 (one key, wraps the Data Protection ring)
```

| Item | EUR/month | Basis |
|---|---|---|
| VM1 control, Hetzner CX33 | 8.49 | V |
| VM2 engine and sandbox host, Hetzner CX53 | 29.49 | V |
| Hetzner backups for both (20% of price) | 7.60 | V (Hetzner's 20% rule, as I know it; confirm) |
| IPv4 for two VMs | about 1 | E |
| Managed Postgres, Scaleway DB-POP2-2C-8G, single node, PITR | 104.70 | V (EUR 0.1434/h x 730) |
| Cloudflare R2 (about 0.75 TB at 50/day) | 10 | model |
| AWS KMS, one key | 1 | V |
| RabbitMQ, Qdrant, Grafana, Cloudflare Free | 0 | V |
| **S1 total at launch** | **about 162** | |
| **Pre-revenue "beta" variant:** Postgres on Scaleway DB-PLAY2-PICO (about EUR 17), no backups | **about 67** | V for PICO price (EUR 0.0233/h) |
| If CCX33 is needed instead of CX53 for the engine VM | +109 | V |
| Add CloudAMQP Tough Tiger instead of the container | +17 | T |

Capacity: one CX53 is 16 shared vCPUs. At about 3 node-minutes per render (an 8 vCPU node), 16 vCPUs give about 960 renders/day at 100% load, so roughly **250-400 renders/day sustained** at realistic packing. That covers the 50/day load point with about 5x headroom. `WORKFLOW_MAX_CONCURRENCY` and `VIDEO_MAX_CONCURRENT_JOBS` (default 1) need tuning to the node (suggest 4 and 2).

Rules for this topology:
- `sandbox-executor` mounts the Docker socket, so VM2 is a single-purpose box. It is firewalled to nginx/VM1 only (private Hetzner network), never public, and keeps `SANDBOX_NETWORK_EGRESS=false`. Do not co-locate any other service on VM2.
- Postgres is in another provider and country (Hetzner DE to Scaleway Paris is about 10-15 ms). The engine and API are chatty, so F1 must include a one-day latency spike. If it bites, the fallback is all-Scaleway for S1 (EUR 264/month, column B).
- The managed database is reached over the public internet with an IP allow-list plus TLS (a Hetzner private network does not reach Scaleway). Use the VMs' fixed IPs.
- RabbitMQ in a container on VM1 is acceptable at S1 because F13 already treats the broker as transient (state lives in Postgres and F4 re-publishes `Queued` work). Move it to CloudAMQP before a second control VM exists.
- Secrets at S1: SOPS with age in the repository, decrypted on the VM. External Secrets Operator (F8) arrives with k8s.
- The Data Protection ring must be in the database before more than one control replica exists (F3). With a single VM1 the file-system store on a persistent volume is still acceptable.

### Scale-out path

| Trigger | Step | Cost impact |
|---|---|---|
| Sustained above about 250 renders/day, or queue wait over 2 minutes | Add a second CX53 by hand (S1 still), raise `MaxConcurrency` | +29/month each |
| Second control replica needed, or the first zero-downtime deploy requirement | Move control plane to Scaleway Kapsule (mutualized, free control plane) on 2 nodes; Postgres to HA; RabbitMQ to CloudAMQP Tough Tiger (S2/F1) | about +330 (see 500/day column) |
| Autoscaler and gateway ready (S2, D13/F12) | Retire VM2; render nodes become CX43 pool VMs created and deleted by the autoscaler (hcloud driver), scale to zero | compute drops to about EUR 38 at 500/day |
| Over 2,000 renders/day | Dedicated CloudAMQP, bigger Postgres, Qdrant paid cluster, Grafana Pro, retention tuning | see 5,000/day column |
| Shared-vCPU performance not sufficient | Switch the pool type to CCX33 or Scaleway COMPUTE3 (one `Provisioner` setting) | see sensitivity above |

## 6. Decision

**Recommended (proposed, awaiting the founder):**

1. **Compute plane: Hetzner, shared-vCPU CX line, hourly, driven by our own autoscaler (F12) behind a `Provisioner` interface.** At S1 the engine host is a single always-on CX53 (EUR 29.49/month) with `sandbox-executor` on the same machine.
2. **Control plane: Hetzner CX33 VM at S1; Scaleway Kapsule (mutualized, free control plane) from S2.** Scaleway is EU-owned, bills in EUR, and its Kapsule has no control-plane fee, unlike EKS and GKE. Hetzner has no managed Kubernetes at all.
3. **Postgres: Scaleway Managed Database for PostgreSQL** (PITR, HA when needed). Hetzner has no managed Postgres, and EU managed options are otherwise OVH (similar price, similar maturity) or the hyperscalers (2-3x).
4. **RabbitMQ: a container at S1; CloudAMQP shared (Tough Tiger) from S2, dedicated at scale.** No EU cloud here offers native RabbitMQ except Amazon MQ, which costs about 30x more.
5. **Objects: Cloudflare R2, EU jurisdiction bucket** (D19). Egress free; this is exactly why compute should not be on AWS or GCP.
6. **Qdrant Cloud, EU region** (free tier at S1, paid cluster later). Rebuildable by re-index (F7).
7. **KMS: AWS KMS eu-central-1, one customer key for the Data Protection ring** (US$1/month). It is the only KMS here with first-class .NET support. F3 must code against a small `IKeyWrapper` interface so the key service can be swapped. Alternative: Scaleway Key Manager, which is EU-only but whose price and SDK support I could not confirm.
8. **Observability: Grafana Cloud EU free tier** (F9), Pro at scale.
9. **Edge: Cloudflare Free plan proxy** in front of nginx (F15). Its 100 MB request body cap on lower plans means browser uploads must go direct to R2 by presigned PUT (nginx's `client_max_body_size 100M` path breaks otherwise).

**Hypothesis in F0 step 3, confirmed or rejected:**
- *Compute on Hetzner dedicated-vCPU VMs, cheapest per vCPU-hour:* **rejected for the dedicated CCX line.** After the June 2026 repricing CCX33 is EUR 0.0277 per vCPU-hour, about the same as Scaleway COMPUTE3 and OVH. **Confirmed for the shared CX line** (EUR 0.0032 per vCPU-hour, 5-9x cheaper than anyone else), pending a ffmpeg benchmark.
- *Control plane on Scaleway Kapsule or AWS:* **Scaleway confirmed**; AWS rejected on cost and on R2 egress.
- *RabbitMQ on CloudAMQP in the same region:* confirmed from S2; a container is enough for S1.
- *Storage on R2, edge on Cloudflare:* confirmed.

**Why not the others:**
- *AWS eu-central-1:* the best autoscaling and managed services, but 2.5-2.9x the cost, and traffic to R2 is billed as internet egress. Reasonable only if the product ever needs enterprise procurement ("runs on AWS").
- *GCP europe-west:* compute is comparable to AWS; managed RabbitMQ is absent; no reason to prefer it over AWS.
- *OVHcloud:* a credible all-EU alternative (free k8s control plane, managed Postgres at about EUR 54-69/month). Slightly cheaper Postgres than Scaleway but weaker CloudAMQP integration and I did not verify its object-storage and egress terms. Keep as the second source for the exit plan.
- *All-Scaleway:* simplest operations (one provider, one bill, one DPA, no cross-provider latency) at about +EUR 100/month at S1 and +EUR 170 at 500/day over the hybrid. This is the fallback if the cross-provider latency spike or Hetzner account friction fails.

## 7. Exit plan

The design keeps every provider-specific layer thin:

| Layer | Portable because | Exit procedure |
|---|---|---|
| Compute VMs | one `Provisioner` interface (F12); images from GHCR | Add a driver for Scaleway, OVH or AWS and change the pool setting. Tested once on staging before launch |
| Kubernetes | upstream k8s manifests (F1); no provider CRDs; ingress is our nginx | Re-apply manifests on any managed k8s |
| Postgres | standard Postgres 16; PITR dump plus WAL | `pg_dump` or base backup to the target; cut over in a maintenance window; Data Protection keys live in this database (F3), so provider-key decryption survives the move |
| Objects | S3 API (R2); nightly rclone of `userFiles` to a second-provider bucket (F13) | Point `MinIO__Endpoint` at any S3-compatible store and rclone-sync |
| RabbitMQ | plain AMQP; transient by design | Create a broker elsewhere, change the connection string, restart (F4 republishes queued work) |
| Qdrant | rebuildable from the database | Re-index into a new cluster |
| KMS | `IKeyWrapper` abstraction (F3) | Unwrap the Data Protection ring with the old key service, re-wrap with the new one, keep the old key until verified. Never rename the application name or purpose string (D20) |
| Edge | Cloudflare DNS and proxy | Lower the TTL first; DNS can be re-pointed in minutes |
| Observability | OTLP only (F9) | Change `OTEL_EXPORTER_OTLP_*` |

Targets: RPO 15 minutes for Postgres, 24 hours for blobs, RTO 4 hours (F13). Run one full provider-exit rehearsal (staging rebuilt on the second source: OVH or all-Scaleway) during F14. Budget a two-day rehearsal; the artefact is a checked-in runbook.

Hetzner-specific risks recorded: the June 2026 price rise shows provider price risk (one more reason for `Provisioner`); new accounts can be subject to identity verification and server-count limits (confirm with support before the launch week). Both are mitigated by the all-Scaleway fallback.

## 8. Founder decisions needed

1. **Approve the hybrid (A)**: Hetzner for VMs, Scaleway for managed Postgres and (from S2) k8s. Or choose the simpler all-Scaleway (B, +about EUR 100/month at S1), or AWS (C). Default: A, with B as the fallback if the latency spike or Hetzner account verification fails.
2. **S1 database spend**: managed Scaleway Postgres at about EUR 105/month with PITR, or the beta variant at about EUR 17 until the first paying customer. Default: managed at launch, PICO only for staging and beta.
3. **KMS**: AWS KMS (US$1/month, one key, US company) or Scaleway Key Manager (EU-only, unverified price and SDK), or defer the wrap and keep the ring unencrypted in Postgres (not recommended).
4. **RabbitMQ at S1**: a container on the control VM (EUR 0) or CloudAMQP Tough Tiger (about EUR 17/month). Default: container.
5. **Shared vs dedicated vCPU for render nodes.** Shared CX is 8x cheaper than CCX but unproven under sustained encodes. Approve a half-day benchmark (one `VideoAnalyze` plus `VideoCompile` run, 4 in parallel, on CX53 vs CCX33) as the first F1 task. Default: shared if the slowdown is under 1.5x.
6. **Accounts and budget alerts** (founder-only, lead time): Hetzner (may require ID check), Scaleway, Cloudflare (R2, EU jurisdiction), AWS (KMS only), CloudAMQP (from S2), Qdrant Cloud, Grafana Cloud. Set a hard monthly alert at each provider (F11), for example EUR 250 total at S1.
7. **Storage retention defaults** (R2 is the dominant cost at scale): confirm 30 days for outputs by default and per-plan values later (F10), and 7 days for `runner-staging/`.
8. **Currency and VAT**: Hetzner and Scaleway bill in EUR with EU reverse charge (needs a VAT number); R2, AWS, CloudAMQP and Grafana bill in USD. Budget at US$1 = EUR 0.92 until a live rate is checked.

## 9. Sources (all accessed 2026-10-05)

- Hetzner price adjustment table: https://docs.hetzner.com/general/infrastructure-and-availability/price-adjustment/
- Hetzner repricing coverage: https://wz-it.com/en/blog/hetzner-price-increase-june-2026-cpx-ccx-alternatives/
- Hetzner LB and traffic: https://agentdeals.dev/hetzner-pricing-2026
- Scaleway instance catalogue API: https://api.scaleway.com/instance/v1/zones/fr-par-1/products/servers
- Scaleway Kapsule: https://www.scaleway.com/en/pricing/containers/
- Scaleway managed databases: https://www.scaleway.com/en/pricing/managed-databases/
- Scaleway instances page: https://www.scaleway.com/en/pricing/virtual-instances/
- OVHcloud catalogue API: https://api.ovh.com/1.0/order/catalog/public/cloud?ovhSubsidiary=IE
- OVHcloud MKS plans: https://docs.ovhcloud.com/en/guides/public-cloud/containers-orchestration/managed-kubernetes/mks-plans
- AWS EKS: https://aws.amazon.com/eks/pricing/ ; AWS KMS: https://aws.amazon.com/kms/pricing/
- AWS c7a.2xlarge: https://instances.cloudchipr.com/aws/ec2/c7a.2xlarge ; RDS: https://instances.vantage.sh/aws/rds/db.t4g.medium?region=eu-central-1&os=PostgreSQL ; Amazon MQ: https://cloudburn.io/blog/amazon-mq-pricing
- GCP c3-standard-8: https://www.devzero.io/instances/gcp/c3-standard-8 ; GKE: https://cloud.google.com/kubernetes-engine/pricing ; Cloud SQL: https://cloud.google.com/sql/pricing
- Cloudflare R2: https://developers.cloudflare.com/r2/pricing/
- CloudAMQP: https://www.cloudamqp.com/pricing
- Qdrant: https://qdrant.tech/pricing/
- Grafana: https://grafana.com/pricing/
- OpenAI ASR and embeddings: https://diyai.io/ai-tools/speech-to-text/openai-whisper-api-pricing-2026/ , https://www.costbench.com/software/embedding-apis/openai-embeddings/
- Workload data: dev database `reelforge` (read-only), queried 2026-10-05; sandbox limits in `sandbox/main.go` lines 51-68 (before D4a; now `sandbox/cmd/sandbox-executor/main.go`).
