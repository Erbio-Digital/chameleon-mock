# Chameleon

[English](README.md)

O Chameleon é um simulador de código aberto, com estado, de provedores HTTP externos (pagamentos, notas fiscais, central de atendimento e APIs parecidas), para testes de integração. Sua aplicação chama o Chameleon no lugar do provedor real, e o Chameleon guarda o estado: uma cobrança é criada, paga, expira ou é devolvida, e os webhooks são entregues quando isso acontece.

> **Não use em produção.** O Chameleon é um simulador. Nunca aponte clientes reais, credenciais reais ou dinheiro real para ele. Com o padrão `CHAMELEON_AUTH=none`, qualquer pessoa que alcance a instância é operadora: rode em uma máquina local ou em rede privada.

O que ele oferece:

- Um host .NET 8 com painel web, API de controle e endpoint MCP.
- Sandboxes isolados, cada um com estado, histórico, falhas e relógio próprios.
- Injeção de falhas (timeout, erro, latência, corpo malformado, duplicação, reordenação e retenção de webhooks).
- Conectores são plugins NuGet. Os verificados ficam em um catálogo; qualquer pessoa pode publicar o seu.

## Início rápido

```bash
docker run -p 5000:8080 -e CHAMELEON_CONNECTORS=pix ghcr.io/erbio-digital/chameleon-mock:v0.1.0
```

O painel fica em <http://localhost:5000/ui>. Obtenha um token e crie uma cobrança no conector `pix` (no sandbox `default` qualquer client id é aceito):

```bash
CLIENT_ID=my-client CLIENT_SECRET=my-secret   # qualquer valor serve no sandbox default
TOKEN=$(curl -s -X POST http://localhost:5000/pix/oauth/token \
  -u "$CLIENT_ID:$CLIENT_SECRET" -d grant_type=client_credentials | sed -E 's/.*"access_token":"([^"]+)".*/\1/')

curl -s -X PUT http://localhost:5000/pix/v2/cob/chameleonexample0000000000001 \
  -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
  -d '{"calendario":{"expiracao":3600},"valor":{"original":"10.00"},"chave":"11111111-1111-1111-1111-111111111111"}'
```

Os conectores são baixados do NuGet na inicialização, então o contêiner precisa de internet na primeira vez. Os próximos passos estão em [Primeiros passos](docs/pt-BR/getting-started.md).

## Conectores

| Nome | Simula | Ficha |
|---|---|---|
| `bradesco` | API Pix e de contas do Bradesco | [catalog/providers/bradesco.md](catalog/providers/bradesco.md) |
| `inter` | API Pix, cobrança e banking do Banco Inter | [catalog/providers/inter.md](catalog/providers/inter.md) |
| `nfse-sp` | Web service da nota fiscal de serviço (NFS-e) de São Paulo | [catalog/providers/nfse-sp.md](catalog/providers/nfse-sp.md) |
| `pix` | API Pix do Banco Central (cob, cobv, consulta, devolução, webhook por chave) | [catalog/providers/pix.md](catalog/providers/pix.md) |
| `zendesk` | API Zendesk Support | [catalog/providers/zendesk.md](catalog/providers/zendesk.md) |

Carregue vários com `CHAMELEON_CONNECTORS=bradesco,inter,nfse-sp,pix,zendesk`.

## Documentação

- [Primeiros passos](docs/pt-BR/getting-started.md): docker, compose com Postgres, sandboxes, relógio, falhas, webhooks, MCP, desenvolvimento local.
- [Autenticação](docs/pt-BR/authentication.md): `none` ou `oidc`, papéis, tokens, exemplos de provedores.
- [Criando um conector](docs/pt-BR/creating-a-connector.md)
- [Selo de verificado](docs/pt-BR/verified-badge.md)
- [Limitações conhecidas](docs/pt-BR/limitations.md)
- [Releases](docs/releasing.md) (mantenedores, só em inglês)
- [Contribuindo](CONTRIBUTING.md), [Segurança](SECURITY.md), [Código de conduta](CODE_OF_CONDUCT.md) (em inglês)

## Licença

Apache-2.0, veja [LICENSE](LICENSE) e [NOTICE](NOTICE). Os nomes de provedores são marcas de seus donos; o Chameleon não tem afiliação com nenhum deles.
