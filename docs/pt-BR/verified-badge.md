# Selo de verificado

[English](../verified-badge.md)

Um conector no catálogo (`catalog/connectors.json`) é "verificado". `CHAMELEON_CONNECTORS=<nome>` carrega só conectores verificados, com os pacotes fixados por SHA-512. Qualquer outro é carregado como `Id@version` com `CHAMELEON_ALLOW_UNVERIFIED=true` e aparece como não verificado. O selo significa que o pacote é o que o catálogo diz e passou nas checagens abaixo, e que um mantenedor leu o código-fonte. Não garante que o simulador esteja correto.

## Dois passos

1. **`catalog-verify` (automático).** Uma ferramenta reconstrói cada entrada do catálogo a partir do pacote no nuget.org e confere os requisitos abaixo. Roda no workflow `catalog-update` e em todo pull request que toca `catalog/**` ou as ferramentas do catálogo. O pull request aberto pelo `catalog-update` não recebe a verificação `catalog-verify` automática, porque o GitHub não dispara workflows para pull requests abertos com `GITHUB_TOKEN`; a descrição dele traz o link da execução que verificou as entradas. Um mantenedor que faz push de um commit nesse pull request dispara a verificação.
2. **Revisão humana.** Um mantenedor lê o código do conector no que uma ferramenta não julga: fidelidade ao contrato público, ausência de acesso oculto à rede, ausência de material proprietário, nome honesto e uso correto de marcas.

Os dois precisam passar antes de a entrada ser mergeada.

## Requisitos

- **Licença.** Uma expressão SPDX aprovada pela OSI no pacote (`Apache-2.0`, `MIT`, `BSD-2-Clause`, `BSD-3-Clause`, `ISC`, `MPL-2.0`, as famílias LGPL e GPL, `EPL-2.0`).
- **Repositório público.** Uma URL de repositório `https` nos metadados do pacote que responda sem credenciais.
- **Ficha do provedor.** `catalog/providers/<nome>.md` com uma descrição curta e uma seção `## Sources` cujo primeiro link `https://` é a documentação pública do provedor. Esse link vira a URL do provedor no catálogo.
- **Smoke de contrato, sem rede.** A ferramenta instala o pacote, sobe o host real offline com um proxy contador e confere que: o conector carrega como verificado (os hashes do catálogo conferiram), o `Describe()` tem título, pelo menos uma rota do provedor está mapeada e nada abre conexão de saída durante o carregamento.
- **SDK compatível.** Todo assembly do pacote que referencia `Chameleon.Sdk` deve ser compatível com o SDK do host (em 0.x, a mesma minor).
- **Integridade.** O fecho do catálogo (o pacote e as bibliotecas de que ele precisa) confere com o pacote e o SHA-512 de cada arquivo confere.
- **Nome.** O nome do conector casa com `^[a-z0-9][a-z0-9-]*$` e é único no catálogo. Pacotes da comunidade não devem usar o prefixo reservado `Chameleon.Mock.` (o template evita isso).

## Como pedir

1. Publique seu pacote no nuget.org com as tags `chameleon-connector` e `chameleon-name:<nome>` (o template faz isso).
2. Abra um pull request que adiciona `catalog/providers/<nome>.md`.
3. Um mantenedor roda o workflow `catalog-update` com `<PackageId>@<versão>`. Ele baixa o pacote do nuget.org, monta a entrada, roda o `catalog-verify` e abre um pull request com a mudança no catálogo.
4. O mantenedor revisa o código e faz o merge.

Para atualizar um conector verificado, publique a nova versão e repita o passo 3. A entrada passa então a apontar para a nova versão para todos que usam o nome do catálogo.

## Revogação

Se uma versão verificada se mostrar errada ou insegura, um mantenedor preenche o campo `revoked` da entrada com um motivo, em um pull request em `catalog/connectors.json`. Um host que lê o catálogo passa a recusar o carregamento desse conector, com o motivo na mensagem, e o `catalog-verify` ignora entradas revogadas. Um host que não consegue ler o catálogo usa o embutido na imagem e não vê revogações mais novas; veja as [limitações](limitations.md). Revogar não remove o pacote do nuget.org.
