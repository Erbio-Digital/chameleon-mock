# Autenticação

[English](../authentication.md)

O Chameleon tem dois modos, definidos por `CHAMELEON_AUTH`.

| Modo | Para quê | O que faz |
|---|---|---|
| `none` (padrão) | Uma máquina local | Sem login. Quem alcança a instância é `OPERATOR`. O host registra um aviso na inicialização. |
| `oidc` | Qualquer instância compartilhada | Login com qualquer provedor OpenID Connect que publique discovery. Só e-mails permitidos e verificados recebem um papel. |

A autenticação protege a superfície de controle em `/_chameleon` (API do painel, sandboxes, falhas, MCP). As rotas simuladas do provedor (`/pix/...`, `/s/<id>/pix/...`) mantêm o próprio OAuth simulado e não ficam atrás do login. O Chameleon é uma ferramenta de teste: mesmo com `oidc`, não o exponha na internet como se fosse um produto.

## Variáveis do `oidc`

A instância não sobe com configuração incompleta. Obrigatórias:

| Variável | Significado |
|---|---|
| `CHAMELEON_AUTH=oidc` | Liga o login. |
| `CHAMELEON_OIDC_ISSUER` | URL do issuer. Deve ser https; http só vale para um issuer local (loopback ou `host.docker.internal`). O discovery é lido em `{issuer}/.well-known/openid-configuration`. |
| `CHAMELEON_OIDC_CLIENT_ID` | Client id do painel (um cliente público). |
| `CHAMELEON_ALLOWED_EMAIL_DOMAINS` e/ou `CHAMELEON_ALLOWED_EMAILS` | Lista de permissão separada por vírgulas: `example.com` permite `user@example.com` (sem subdomínios); `ana@example.com` permite um endereço. Pelo menos uma é obrigatória. |
| `CHAMELEON_WEBHOOK_ALLOWED_HOSTS` | Hosts que os webhooks podem alcançar (veja [Primeiros passos](getting-started.md)). |
| `CHAMELEON_SECRET_KEY` | Base64 de 32 bytes aleatórios: `openssl rand -base64 32`. Sela as credenciais emitidas. |
| `CHAMELEON_CA_CERT`, `CHAMELEON_CA_KEY` | PEM da autoridade certificadora do Chameleon (veja abaixo). O arquivo de segredos de desenvolvimento é ignorado com `oidc`. |

Opcionais:

| Variável | Significado |
|---|---|
| `CHAMELEON_OIDC_AUDIENCE` | Defina quando o provedor emite access tokens JWT para esta API. O painel passa a enviar o access token e o Chameleon exige essa audiência. Sem ela, o painel envia o id token e a audiência é o client id. |
| `CHAMELEON_CI_KEY` | Uma chave compartilhada para automação. Deve começar com `chm_ci_`. |
| `CHAMELEON_PUBLIC_URL` | O endereço que os clientes usam, quando o Chameleon está atrás de um proxy. Usado nos metadados de descoberta do MCP. |

Crie uma CA uma vez e guarde com seus outros segredos:

```bash
openssl ecparam -name prime256v1 -genkey -noout | openssl pkcs8 -topk8 -nocrypt -out ca.key
openssl req -x509 -new -key ca.key -subj "/CN=Chameleon CA" -days 3650 \
  -addext "basicConstraints=critical,CA:TRUE" -addext "keyUsage=critical,keyCertSign,cRLSign" -out ca.crt
```

A URI de redirecionamento do painel é `{origem}/ui/callback` e ele pede os escopos `openid email profile`.

## Quem entra

Uma requisição precisa de `Authorization: Bearer <credencial>` com uma destas credenciais:

| Credencial | Papel | Observações |
|---|---|---|
| Um JWT do provedor configurado | `OPERATOR` | Emitido para o cliente do painel. A claim `email` deve existir, `email_verified` deve ser `true` e o endereço deve estar na lista de permissão. |
| O mesmo tipo de JWT emitido para outro cliente do provedor | `AGENT` | A claim `azp` ou `client_id` difere de `CHAMELEON_OIDC_CLIENT_ID`: uma ferramenta agindo pelo operador. |
| Token pessoal `chm_pat_...` | `AGENT` | Criado por um operador. Mostrado uma vez, guardado só como hash, revogável. |
| `CHAMELEON_CI_KEY` (`chm_ci_...`) | `CI` | Para pipelines. Comparada em tempo constante. |

Um JWT válido cujo e-mail não é permitido ou não é verificado recebe `403`; sem credencial, `401`.

| Capacidade | OPERATOR | AGENT | CI |
|---|---|---|---|
| Sandboxes, relógio, falhas, entidades, ações, webhooks, histórico | sim | sim | sim |
| Gerenciar tokens pessoais (`/_chameleon/tokens`) | sim | não | não |
| Zerar um sandbox permanente | sim | não | não |
| Baixar o certificado do cliente em `.pfx` e definir sua senha | sim | não | sim |
| Ler a chave privada e a senha do certificado em uma assinatura | sim | não | sim |

Públicos, sem login: `/_chameleon/config` e `/_chameleon/ca.crt` (o painel inicia a partir deles), logos dos conectores, `/swagger`, `/scalar` e `/_chameleon/health`. Veja as [limitações](limitations.md).

## Tokens pessoais e a chave de CI

```bash
# Um operador cria um token (no painel, ou com uma credencial de operador):
curl -X POST https://chameleon.example.com/_chameleon/tokens \
  -H "Authorization: Bearer $OPERATOR_JWT" -H "Content-Type: application/json" -d '{"name":"my-agent"}'
# -> {"id":"...","name":"my-agent","token":"chm_pat_..."}   guarde o valor, ele não aparece de novo

curl https://chameleon.example.com/_chameleon/me -H "Authorization: Bearer $AGENT_TOKEN"
curl -X DELETE https://chameleon.example.com/_chameleon/tokens/<id> -H "Authorization: Bearer $OPERATOR_JWT"
```

Use `chm_pat_` para clientes MCP e scripts que agem por uma pessoa, e `chm_ci_` para pipelines. Clientes MCP que falam OAuth também descobrem o provedor em `/.well-known/oauth-protected-resource/_chameleon/mcp` (RFC 9728); o registro dinâmico de clientes depende do provedor.

## Exemplos de provedores

Crie a aplicação como cliente público (authorization code com PKCE, sem segredo) com URI de redirecionamento `https://chameleon.example.com/ui/callback` (para uma instância local, `http://localhost:5000/ui/callback`).

### Keycloak

É o provedor exercitado pelo smoke test do repositório (`docker-compose.oidc.yml`).

```
CHAMELEON_AUTH=oidc
CHAMELEON_OIDC_ISSUER=https://keycloak.example.com/realms/main
CHAMELEON_OIDC_CLIENT_ID=chameleon-panel
CHAMELEON_ALLOWED_EMAIL_DOMAINS=example.com
```

Crie um cliente público `chameleon-panel` e marque os e-mails dos usuários como verificados.

### Entra ID

```
CHAMELEON_OIDC_ISSUER=https://login.microsoftonline.com/<tenant-id>/v2.0
CHAMELEON_OIDC_CLIENT_ID=<id do aplicativo (cliente)>
CHAMELEON_ALLOWED_EMAILS=ana@example.com,bia@example.com
```

Registre o app com a plataforma "Aplicativo de página única". O Entra não envia `email_verified` e o Chameleon recusa tokens sem ele, então uma configuração simples do Entra responde `403`. Adicione uma claim personalizada `email_verified` com o valor `true` aos tokens deste app e mantenha a lista de e-mails como o controle de verdade. Garanta que a claim `email` também esteja presente (claim opcional `email`).

### Auth0

```
CHAMELEON_OIDC_ISSUER=https://<tenant>.auth0.com
CHAMELEON_OIDC_CLIENT_ID=<client id da aplicação de página única>
CHAMELEON_ALLOWED_EMAIL_DOMAINS=example.com
```

Deixe `CHAMELEON_OIDC_AUDIENCE` sem valor. Com audiência, o Auth0 emite um access token sem a claim `email`, que o Chameleon não consegue associar a um usuário permitido; sem ela, o painel envia o id token, que traz o e-mail.

Os exemplos de Entra ID e Auth0 seguem as claims documentadas de cada provedor e não foram executados contra um tenant real. Se algum não funcionar para você, abra uma issue com as claims do seu token (sem a assinatura).
