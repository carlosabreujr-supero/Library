# Kubernetes

Manifests: `configmap.yaml`, `deployment.yaml`, `service.yaml`, `pdb.yaml`, `secret.example.yaml` (modelo, sem valores) e `kustomization.yaml`.

## Dependências externas

PostgreSQL e Redis **não** são provisionados aqui; use serviços gerenciados ou instalações separadas.
Informe-os no Secret `library-loans-api` (`ConnectionStrings__Postgres`, `ConnectionStrings__Redis`).
O PostgreSQL é obrigatório (readiness 503 sem ele). O Redis é opcional: sem ele a API usa o PostgreSQL
(readiness `Degraded`; defina `Health__RedisRequired: "true"` para exigi-lo).

## Migrations

A API não aplica migrations ao iniciar. Rode-as uma vez por deploy, fora do Deployment
com [migration-job.yaml](migration-job.yaml) (imagem `docker build --target migrate`, fora do kustomization) ou etapa de CI,
antes de atualizar o Deployment. Ver [docs/migrations.md](../docs/migrations.md).

## Uso

```powershell
kubectl create secret generic library-loans-api --from-literal=ConnectionStrings__Postgres='...' --from-literal=ConnectionStrings__Redis='...'
kubectl apply -k k8s
kubectl scale deployment/library-loans-api --replicas=5
```

Imagem e réplicas são configuráveis via `images`/`replicas` do kustomize. Validação sem cluster:
`kubectl kustomize k8s` e `kubectl apply -k k8s --dry-run=client`.

## Réplicas (2 a 11)

A API não guarda estado crítico em memória. Estoque (UPDATE condicional atômico), idempotência (constraint única) e
auditoria são decididos no PostgreSQL, então qualquer número de réplicas é seguro. Redis é só cache de leitura.
Dimensione o pool de conexões do PostgreSQL para `réplicas × pool` (padrão Npgsql: 100 por instância).

## Encerramento gracioso

`preStop` (5 s) + SIGTERM tratado pelo host do ASP.NET Core, com `terminationGracePeriodSeconds: 30`.
`maxUnavailable: 0` e o PDB preservam capacidade durante atualizações.
