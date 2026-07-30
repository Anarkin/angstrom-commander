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
