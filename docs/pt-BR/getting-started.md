# Primeiros passos

[English](../getting-started.md)

O Chameleon é um simulador para desenvolvimento e testes. Não use em produção.

## Rodar com docker

```bash
docker run -p 5000:8080 -e CHAMELEON_CONNECTORS=pix ghcr.io/erbio-digital/chameleon-mock:v0.1.0
```

- Painel: <http://localhost:5000/ui>. Saúde: `/_chameleon/health`.
- Documentação da API: `/scalar` (JSON OpenAPI em `/swagger/v1/swagger.json`).
- O contêiner escuta nas portas 8080 (HTTP) e 8443 (mTLS, só com `CHAMELEON_MTLS_PORT=8443`).
- O estado fica em memória por padrão e se perde quando o contêiner para.
- Sem `CHAMELEON_SECRET_KEY`, a imagem gera uma chave e uma CA de desenvolvimento em `/data/dev-secrets.json`. Monte um volume em `/data` (`-v chameleon-data:/data`) para manter a mesma chave e CA entre contêineres (credenciais emitidas e certificados de cliente continuam válidos com banco persistente), ou defina `CHAMELEON_SECRET_KEY` (e as variáveis da CA em [autenticação](authentication.md)) você mesmo; a variável explícita sempre vence o arquivo.
- `CHAMELEON_CONNECTORS` é uma lista separada por vírgulas de nomes do catálogo (`pix`, `bradesco,inter`). Os pacotes são baixados do NuGet na inicialização para `CHAMELEON_PLUGINS_DIR` (`/plugins` na imagem). Monte um volume ali para mantê-los entre execuções, ou use `CHAMELEON_OFFLINE=true` para usar só o que já foi baixado.
- Sem `CHAMELEON_AUTH` não há login (veja [Autenticação](authentication.md)).
- `CHAMELEON_CATALOG_URL` define de onde o catálogo de conectores é lido na inicialização (padrão: `catalog/connectors.json` da branch `main` deste repositório no GitHub). Aceita `https://`, `http://` só para endereço de loopback e `file://` (lido mesmo com `CHAMELEON_OFFLINE=true`). Se o catálogo não puder ser lido em 5 segundos, estiver vazio, ou a instância estiver offline com URL remota, o Chameleon usa o catálogo embutido na imagem. Use para apontar para um espelho ou para uma cópia local em uma máquina sem acesso à internet.

Instale, liste e busque conectores sem iniciar o servidor:

```bash
docker run --rm ghcr.io/erbio-digital/chameleon-mock:v0.1.0 list
docker run --rm ghcr.io/erbio-digital/chameleon-mock:v0.1.0 search zendesk
```

## Compose com Postgres

`CHAMELEON_STORE=postgres` guarda todos os armazenamentos (estado, histórico, sandboxes, tokens) no Postgres, e eles sobrevivem a reinícios.

```yaml
services:
  chameleon:
    image: ghcr.io/erbio-digital/chameleon-mock:v0.1.0
    ports:
      - "127.0.0.1:5000:8080"
    environment:
      CHAMELEON_CONNECTORS: bradesco,inter,nfse-sp,pix,zendesk
      CHAMELEON_STORE: postgres
      CHAMELEON_POSTGRES_CONNECTION: Host=postgres;Database=chameleon;Username=chameleon;Password=chameleon-local
      CHAMELEON_MIGRATE_ON_START: "true"
    volumes:
      - chameleon-data:/data
      - chameleon-plugins:/plugins
    depends_on:
      postgres:
        condition: service_healthy
  postgres:
    image: postgres:16
    environment:
      POSTGRES_DB: chameleon
      POSTGRES_USER: chameleon
      POSTGRES_PASSWORD: chameleon-local
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U chameleon -d chameleon"]
      interval: 5s
      retries: 10
volumes:
  chameleon-data:
  chameleon-plugins:
```

- `CHAMELEON_MIGRATE_ON_START=true` aplica as migrations do esquema na inicialização.
- Sem `oidc`, a chave secreta e a autoridade certificadora do mTLS são geradas em `/data/dev-secrets.json` no primeiro start (`CHAMELEON_DEV_SECRETS_FILE`). Mantenha o volume `/data`: apagá-lo invalida credenciais e certificados emitidos, então zere os sandboxes também.
- `CHAMELEON_RETENTION_DAYS` (padrão 7) remove entradas antigas do histórico, entregas de webhook e tokens simulados.
- O `docker-compose.yml` do repositório é a versão de desenvolvimento deste arquivo (veja Desenvolvimento local).

## Sandboxes

Um sandbox é uma cópia isolada do mundo: entidades, histórico, falhas, política de webhook e relógio próprios. O sandbox `default` sempre existe. Sua aplicação acessa um sandbox por prefixo de caminho:

```
http://localhost:5000/pix/v2/cob/...                    # o sandbox default
http://localhost:5000/s/checkout-test/pix/v2/cob/...    # o sandbox "checkout-test"
```

```bash
curl -X POST localhost:5000/_chameleon/sandboxes -H "Content-Type: application/json" -d '{"id":"checkout-test","ttlSeconds":3600}'
curl -X POST localhost:5000/_chameleon/sandboxes/checkout-test/reset
curl -X DELETE localhost:5000/_chameleon/sandboxes/checkout-test
```

- Os ids usam `[a-z0-9-]`, até 40 caracteres. Um sandbox expira após `ttlSeconds` (padrão 24 horas); `"permanent": true` desliga a expiração e exige `?confirm=<id>` para apagar.
- `"from": "<id>"` copia entidades, falhas, política de webhook e relógio de outro sandbox (não o histórico).
- As rotas de controle dentro de um sandbox usam o mesmo prefixo: `/s/checkout-test/_chameleon/faults`.
- Fora do `default`, um conector só responde depois que o sandbox o assina: `POST /s/<id>/_chameleon/connectors/<nome>/subscription`; depois use `GET` no mesmo caminho para ler `CLIENT_ID`, `CLIENT_SECRET` e a URL base emitidos. O painel faz isso por você.
- Conectores que identificam o cliente por certificado (`clientCertificate: true` em `GET /_chameleon/connectors`: `bradesco`, `inter`, `pix`) derrubam a chamada a um sandbox que não é o `default` quando ela não apresenta o certificado do sandbox. Ative a porta mTLS com `CHAMELEON_MTLS_PORT=8443`, `CHAMELEON_MTLS_HOSTNAMES` (nomes no certificado do servidor) e `CHAMELEON_MTLS_URL` (o que a assinatura mostra), publique a 8443 e baixe a CA em `/_chameleon/ca.crt`. O `docker-compose.yml` do repositório tem uma configuração que funciona.

## Relógio

Cada sandbox tem seu relógio, `Frozen` (o padrão: o tempo só anda quando você avança) ou `Realtime`.

```bash
curl -X POST localhost:5000/_chameleon/clock/advance -H "Content-Type: application/json" -d '{"seconds":3600}'
curl -X POST localhost:5000/_chameleon/sandboxes/default/clock -H "Content-Type: application/json" -d '{"mode":"Realtime"}'
curl -X POST localhost:5000/_chameleon/sandboxes/default/clock/now
```

Avançar aplica o que venceu: uma cobrança Pix expira, uma NFS-e é processada. Voltar no tempo não desfaz nada. A última chamada devolve o sandbox ao tempo real.

## Falhas

Arme uma falha em uma rota de um conector (ou `*` para todas). Os nomes de rota são `MÉTODO padrão`, como listados em `routes` em `GET /_chameleon/connectors`.

```bash
curl -X POST localhost:5000/_chameleon/faults -H "Content-Type: application/json" \
  -d '{"connector":"pix","route":"PUT /pix/v2/cob/{txid}","kind":"Error","status":503,"times":1}'
curl localhost:5000/_chameleon/faults
curl -X DELETE "localhost:5000/_chameleon/faults?connector=pix&route=*"
```

- Tipos: `Timeout`, `Error` (`status` de 400 a 599), `Latency` (`delayMs` até 30000), `Malformed` e, para a rota `webhook`, `Duplicate`, `Reorder` e `Hold`. Conectores com liquidação assíncrona também aceitam `Hold` na rota `settlement`.
- Uma falha dura `times` usos (1 a 1000) ou `durationSeconds` no relógio do sandbox, não os dois.
- Toda requisição entra no histórico: `GET /_chameleon/interactions`. `POST /_chameleon/verify` conta as chamadas que casam.

## Webhooks

Registre o receptor como o provedor real exige (no `pix`, `PUT /pix/v2/webhook/{chave}` com `{"webhookUrl": "..."}`), depois conduza a entidade e o Chameleon entrega o evento com novas tentativas (padrão 5 tentativas, dobrando a partir de 2 s).

- Execute uma ação da entidade pela rota genérica: `POST /_chameleon/connectors/pix/entities/cob/<txid>/actions/pay`.
- Inspecione e reenvie entregas: `GET /_chameleon/webhooks`, `POST /_chameleon/webhooks/{id}/resend`. Ajuste as tentativas por sandbox com `PUT /_chameleon/sandboxes/{id}/webhook-policy`.
- O Chameleon bloqueia destinos de webhook em endereços privados (proteção contra SSRF). Libere seu receptor com `CHAMELEON_WEBHOOK_ALLOWED_HOSTS` (nomes de host separados por vírgula; um ponto no início libera um sufixo de domínio), por exemplo `host.docker.internal` para alcançar a máquina local. Com a variável definida, hosts fora da lista são recusados.
- Receptores em http puro são aceitos no conector `pix`. Um PSP real exige https com mTLS.

## MCP

O Chameleon expõe um endpoint Model Context Protocol (HTTP em streaming, sem sessão) em `/_chameleon/mcp`, e dentro de um sandbox em `/s/<id>/_chameleon/mcp`. Aponte um cliente MCP para `http://localhost:5000/_chameleon/mcp`. As ferramentas (`chameleon_create_sandbox`, `chameleon_advance_clock`, `chameleon_arm_fault`, `chameleon_run_action`, `chameleon_search_interactions` e outras) envolvem a API de controle, então um agente pode montar um cenário, conduzi-lo e conferir o resultado. Com `oidc`, o endpoint exige um token pessoal ou OAuth, veja [Autenticação](authentication.md).

## Desenvolvimento local

O `docker-compose.yml` do repositório constrói a imagem a partir do código e aponta o host para um feed e um catálogo locais, então os pacotes precisam existir antes:

```bash
dotnet pack -c Release -o artifacts/packages
dotnet run --project tools/catalog -- artifacts/packages artifacts/packages/catalog.json --providers catalog/providers
docker compose up -d --build
```

- O painel fica em <http://localhost:5000/ui> e a porta mTLS em `https://localhost:5443`.
- `docker compose down -v` também apaga os segredos gerados e os dados do Postgres.
- Rode os testes com `dotnet test Chameleon.sln`; o smoke ponta a ponta é `python tests/smoke/smoke_test.py --skip-dotnet --skip-build`. Veja o [CONTRIBUTING](../../CONTRIBUTING.md) (em inglês).
- Para trabalhar em um conector, veja [Criando um conector](creating-a-connector.md).
