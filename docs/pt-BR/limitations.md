# Limitações conhecidas

[English](../limitations.md)

O Chameleon é um simulador de testes. Estas são as arestas conhecidas no primeiro lançamento.

## Autenticação

- **O Entra ID não envia `email_verified`.** O Chameleon exige `email_verified: true`. Os tokens do Entra não trazem essa claim; use uma lista de e-mails (`CHAMELEON_ALLOWED_EMAILS`) junto com uma claim personalizada `email_verified`. Veja [Autenticação](authentication.md).
- **O access token do Auth0 não tem `email`.** Use o id token: deixe `CHAMELEON_OIDC_AUDIENCE` sem valor para que o painel envie o id token.
- **`/swagger` e `/scalar` são públicos no modo `oidc`.** Eles descrevem a API, mas não executam nada sem credencial. Bloqueie no seu proxy se isso importar.
- **Uma conexão SignalR aberta não revalida o token.** O feed ao vivo continua funcionando até a página desconectar, mesmo depois de o token expirar ou de um token pessoal ser revogado.
- **MCP com OAuth depende do provedor.** O Chameleon publica o Protected Resource Metadata (RFC 9728) para o cliente achar o servidor de autorização, mas o registro dinâmico de clientes fica por conta do provedor. Um token pessoal (`chm_pat_`) funciona em qualquer caso.
- Os setups de Entra ID e Auth0 não foram executados contra tenants reais; o Keycloak é o exercitado pelo smoke test.

## Conectores e catálogo

- **Sem pin de versão verificada.** Um nome do catálogo (`pix`) sempre resolve para a versão que o catálogo lista. `Id@version` fixa uma versão, mas é não verificado e exige `CHAMELEON_ALLOW_UNVERIFIED=true`.
- **O catálogo embutido ignora revogações mais novas.** Quando a URL do catálogo não pode ser lida (ou está vazia, ou `CHAMELEON_OFFLINE=true`), o host usa o catálogo embutido na imagem, que só conhece as revogações até o build da imagem.
- **A atribuição ao pacote casa só o assembly principal.** O host liga um conector carregado ao seu pacote pelo assembly principal do pacote. Uma classe de conector em outro assembly do pacote é registrada como in-process e não é atribuída (nem marcada como verificada) a esse pacote.
- **O smoke de contrato não roda em contêiner com `--network none`.** Um proxy contador já reprova qualquer conexão de saída; um contêiner sem rede seria uma segunda camada.
- **Uma dll residual na pasta `lib` do pacote não é checada.**
- **Um conector por pacote no smoke.** O smoke de contrato exercita um conector por pacote.
- **A CLI não tem testes para instalar um conector revogado ou não verificado, nem para `search`.**

## Comportamento

- **O estado fica em memória por padrão.** Use `CHAMELEON_STORE=postgres` para mantê-lo.
- **Ordem no endpoint de token do `pix`.** Ele confere os escopos pedidos antes de autenticar o cliente: um escopo desconhecido com credenciais erradas responde `400 invalid_scope`, não `401`.
- **Webhooks no `pix` podem usar http puro.** Um PSP real exige https com mTLS.
- **Conectores que identificam o cliente por certificado** (`bradesco`, `inter`, `pix`) derrubam chamadas a um sandbox que não é o `default` quando elas não usam a porta mTLS com o certificado do sandbox.

## Desenvolvimento

- **O teste do template precisa de rede.** Ele restaura targeting packs do nuget.org.
- **Duas execuções de teste ao mesmo tempo compartilham `bin/plugin-run`.** Rode a suíte de testes uma por vez em uma máquina.
