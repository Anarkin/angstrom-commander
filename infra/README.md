# infra/ — Azure environments as code

Terraform definitions of everything Angstrom Commander runs on. Two root modules:

- **`shared/`** — cross-stamp resources applied once, by hand: the shared resource group,
  the DNS zone, and the container registry. Never part of any environment, so no stamp
  teardown can touch them.
- **`./`** (this directory) — the per-environment stamp, driven by `azd` (one
  `azd env` = one stamp = one resource group).

## One-time bootstrap (already done — recorded for disaster recovery)

Terraform cannot create the storage its own state lives in, so the state account is the
one hand-made piece:

```sh
az storage account create -n angstromtfstate -g angstrom-commander-shared -l westeurope \
  --sku Standard_LRS --kind StorageV2 --min-tls-version TLS1_2 --allow-blob-public-access false
az storage account blob-service-properties update --account-name angstromtfstate \
  -g angstrom-commander-shared --enable-versioning true
az storage container create -n tfstate --account-name angstromtfstate --auth-mode key
```

Also predating Terraform: the `angstrom-commander-shared` resource group and the
`angstrom.adamlengyel.com` DNS zone (its NS delegation lives at the external registrar).
`shared/imports.tf` adopts both on first apply — they must never be recreated, because a
recreated zone gets different name servers and the delegation dies.

## Applying `shared/`

```sh
cd infra/shared
terraform init
terraform apply
```

Authentication comes from the Azure CLI (`az login`). Re-running is a no-op; the import
blocks are ignored once the resources are in state.

## Stamps

Managed through `azd` from the repo root — see the root README. Do not `terraform apply`
the stamp module directly; `azd` supplies its variables and remote-state key per
environment.

```sh
azd env new <name>
azd env set AZURE_LOCATION westeurope -e <name>
azd env set AZURE_SUBSCRIPTION_ID <id> -e <name>
azd provision -e <name>   # infrastructure
azd deploy -e <name>      # Server image + WebClient bundle
```

One manual step after the first provision of a stamp: the azurerm provider cannot
create Container Apps *managed* certificates, so Terraform binds the `api.` hostname
without one and ignores the certificate from then on. Attach it once:

```sh
az containerapp hostname bind --hostname api.<env>.angstrom.adamlengyel.com \
  -g angstrom-commander-<env> -n ca-server-<env> --environment cae-<env> \
  --validation-method CNAME
```

(The Static Web App's `app.` certificate needs no such step — Azure issues it on its
own once the custom domain validates.)

## Pausing an environment

The two billable resources can be stopped without losing anything — accounts,
pairings, DNS, TLS and the deployed image all stay put, and a stopped stamp costs
only PostgreSQL storage (~$4–5/month at 32 GiB):

```sh
# Pause: deactivate the Server's active revision (0 replicas), stop PostgreSQL
az containerapp revision deactivate -g angstrom-commander-<env> -n ca-server-<env> \
  --revision "$(az containerapp show -g angstrom-commander-<env> -n ca-server-<env> \
                --query properties.latestRevisionName -o tsv)"
az postgres flexible-server stop -g angstrom-commander-<env> -n angstrom-commander-<env>

# Resume: the same two, with start / activate
az postgres flexible-server start -g angstrom-commander-<env> -n angstrom-commander-<env>
az containerapp revision activate -g angstrom-commander-<env> -n ca-server-<env> \
  --revision "$(az containerapp show -g angstrom-commander-<env> -n ca-server-<env> \
                --query properties.latestRevisionName -o tsv)"
```

(`az containerapp stop`/`start` would be simpler, but the installed CLI does not have
them; revision deactivation is the same thing — zero replicas, zero compute billing.)
Daemons reconnect on their own after a resume (their retry loop never gives up, by
design).

Two caveats:

- **Azure auto-restarts a stopped PostgreSQL Flexible Server after 7 days** — its
  compute quietly starts billing again unless re-stopped. The Container App stays
  stopped indefinitely.
- Stopping is for weeks, `azd down` is for months: tearing the stamp down costs
  nothing at all, but loses the environment's data and needs the certificate bind
  and pairing redone on the way back up.
