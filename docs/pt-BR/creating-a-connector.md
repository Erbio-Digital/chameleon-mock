# Criando um conector

[English](../creating-a-connector.md)

Um conector é uma biblioteca .NET pequena que simula a API pública de um provedor. Ele é empacotado como pacote NuGet e o host do Chameleon o carrega na inicialização. Você não edita o motor nem o host.

## Comece pelo template

```bash
dotnet new install Chameleon.Mock.Templates
dotnet new chameleon-connector -n Acme.Chameleon.Widgets --connectorName widgets --title "Acme Widgets"
```

- `-n` é o nome do projeto e o id do pacote NuGet. Ele não pode começar com `Chameleon.Mock.`, prefixo reservado aos pacotes oficiais.
- `--connectorName` é o nome curto usado em `CHAMELEON_CONNECTORS` e como prefixo das rotas. Use letras minúsculas, dígitos e hífens (`^[a-z0-9][a-z0-9-]*$`).
- `--title` aparece no painel. Não pode conter `"`, `&` nem `<`.

O projeto gerado referencia `Chameleon.Mock.Sdk` com a faixa `[0.1.0, 0.2.0)`, só para compilar: o host fornece o SDK em tempo de execução, então ele não vai no seu pacote. Em 0.x uma versão minor é uma quebra do SDK, por isso a faixa termina na próxima minor.

## O contrato `IConnector`

```csharp
public interface IConnector
{
    string Name { get; }
    ConnectorDescriptor Describe();
    void MapRoutes(IEndpointRouteBuilder routes);
    Task OnClockAdvancedAsync(ConnectorContext ctx) => Task.CompletedTask;
}
```

- `Name` é o nome do conector acima.
- `Describe()` devolve as entidades, os ciclos de vida e as ações que o painel e a rota genérica de ações usam, mais título, categoria, descrição, credenciais emitidas e a política de tentativas dos webhooks.
- `MapRoutes` mapeia a superfície do provedor como rotas comuns de minimal API do ASP.NET Core. O registro no histórico e a injeção de falhas valem para toda rota mapeada, sem código seu.
- Injete `ConnectorContext` em um handler para acessar o sandbox: `ctx.Store` (estado), `ctx.Now` (o relógio do sandbox), `ctx.Create` e `ctx.Move` (mudanças de ciclo de vida), `ctx.Save`, `ctx.NotifyAsync` e `ctx.CredentialMatches`.
- Opcional: implemente `IConnectorModule` no mesmo assembly para registrar serviços extras.

## Entidades e ações

Modele cada coisa que o provedor rastreia como uma classe com um `Lifecycle<TEntity, TState>`: um estado inicial e as transições permitidas. Uma transição não permitida vira `409` para quem chamou e as escritas da requisição são descartadas.

```csharp
private static readonly Lifecycle<Item, ItemStatus> ItemLifecycle =
    new Lifecycle<Item, ItemStatus>("item", ItemStatus.ACTIVE, i => i.Key, i => i.Status, (i, s) => i.Status = s)
        .Allow(ItemStatus.ACTIVE, ItemStatus.ARCHIVED, "archive");
```

`ItemLifecycle.Describe("id")` lista a entidade no descritor e `ItemLifecycle.Action(...)` adiciona uma ação. Uma ação é um botão no painel, uma chamada de ferramenta MCP e uma rota que você ganha de graça:

```
POST /_chameleon/connectors/<nome>/entities/<entidade>/<chave>/actions/<ação>
```

Use ações para o que o provedor real faz sozinho: um pagamento liquida, um ticket é resolvido, uma nota é rejeitada.

## OAuth simulado

Se o provedor usa OAuth2 client credentials, adicione uma rota de token e deixe o SDK cuidar do resto:

- `SimulatedOAuth.Credentials("/oauth/token")` no descritor declara `CLIENT_ID`, `CLIENT_SECRET` e a URL do token que um sandbox emite na assinatura (o caminho é relativo à URL base do conector).
- Na rota de token chame `SimulatedOAuth.Issue(ctx, grantType, clientId, clientSecret, scope)`; leia o cliente do HTTP Basic com `SimulatedOAuth.BasicClient(request)` se o provedor permitir.
- Nas outras rotas chame `SimulatedOAuth.Refuse(ctx, http, "escopo")`: devolve `null` quando o bearer é válido e tem o escopo, senão 401 ou 403.
- No sandbox `default` qualquer client id é aceito; os outros sandboxes conferem as credenciais emitidas.

## Webhooks

Depois de uma mudança de estado, chame `ctx.NotifyAsync(entityId, url, payload)`. O Chameleon entrega com novas tentativas, registra cada tentativa no histórico e permite reenviar. Defina a política de tentativas padrão no descritor (`WebhookPolicy`), como o provedor real tenta de novo. Se o provedor tem registros por chave ou por conta, dê ao descritor um `WebhookRegistrations`. Se o provedor assina seus webhooks, defina `WebhookSigner`.

## Relógio

O relógio do sandbox é `ctx.Now`, nunca `DateTime.UtcNow`: quem testa congela e avança o relógio. Para o que envelhece (uma cobrança que expira, uma nota processada depois de um atraso), sobrescreva `OnClockAdvancedAsync` e mova as entidades cujo prazo chegou, com `ctx.Due<T>()` ou sua própria checagem contra `ctx.Now`.

## Regras para conectores

- **Seja fiel ao provedor.** Mesmos caminhos, códigos de status, nomes de campos, formatos de erro e ciclo de vida que o provedor documenta. Um simulador "quase igual" ensina à sua aplicação a coisa errada.
- **O contrato vem de fonte pública.** Construa a partir da documentação publicada pelo provedor. Não copie material proprietário ou confidencial, e nunca inclua credenciais reais, dados pessoais reais nem tráfego capturado em produção.
- **Diga o que você cobre.** Liste as operações e a fonte na ficha do provedor (veja [Selo de verificado](verified-badge.md)).
- **Sem chamadas de saída nas rotas.** Um conector responde a partir do próprio estado. O único tráfego que sai do Chameleon são os webhooks, enviados por `ctx.NotifyAsync`.
- **Respeite as marcas.** Descreva o conector como uma simulação para testes, sem afiliação com o provedor.

## Empacotar, testar localmente, publicar

```bash
dotnet pack -c Release -o ./feed

# Rode a imagem oficial contra o seu feed local. Pacotes não verificados exigem CHAMELEON_ALLOW_UNVERIFIED.
docker run -p 5000:8080 -v "$PWD/feed:/feed:ro" \
  -e CHAMELEON_CONNECTORS=Acme.Chameleon.Widgets@0.1.0 \
  -e CHAMELEON_NUGET_SOURCES=/feed \
  -e CHAMELEON_ALLOW_UNVERIFIED=true \
  ghcr.io/erbio-digital/chameleon-mock:v0.1.0
```

`CHAMELEON_NUGET_SOURCES` é uma lista separada por vírgulas de pastas ou URLs de feed onde os pacotes são procurados; com `Id@version` o conector é carregado como não verificado. Exercite-o pelo painel em <http://localhost:5000/ui> ou com curl, e escreva seus próprios testes contra as rotas dele.

Para publicar, envie o pacote ao nuget.org com a tag `chameleon-connector` em `<PackageTags>` (o template já define, junto com `chameleon-name:<connectorName>`) e uma URL de repositório nos metadados do pacote. Pacotes com a tag aparecem na busca da comunidade no painel e em `chameleon search`, marcados como não verificados e carregáveis com `CHAMELEON_ALLOW_UNVERIFIED=true`. Para ganhar o selo de verificado, siga [Selo de verificado](verified-badge.md).
