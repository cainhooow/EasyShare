# Explorador SharePoint nativo

## Objetivo

O Explorador SharePoint elimina a necessidade de localizar e colar URLs. Depois do login delegado pelo Client ID, o EasyShare apresenta os sites descobertos para a conta, suas bibliotecas e as pastas disponíveis. A pasta atual pode ser fixada na unidade virtual com uma única ação.

Fluxo principal:

`Entrar → Sites → Bibliotecas → Pastas → Fixar`

A entrada manual de URL continua disponível apenas como alternativa para sites que ainda não apareceram na pesquisa do Microsoft 365. No modo Graph, essa URL é validada contra a política corporativa e resolvida para `siteId`, `driveId` e `itemId` antes de a rota ser persistida; não existe fallback silencioso para cookies do WebView.

## Autorização

O Client ID identifica o aplicativo público; o acesso é concedido pelo token delegado do usuário obtido via MSAL. O EasyShare não usa nem armazena Client Secret.

Os escopos atuais permitem descoberta, leitura e operações de arquivo:

- `User.Read`
- `Sites.Read.All`
- `Files.ReadWrite.All`
- `offline_access`

As políticas do tenant podem exigir consentimento administrativo mesmo quando a permissão delegada aceita consentimento do usuário.

O app não solicita `Sites.ReadWrite.All`: a descoberta precisa apenas de leitura de sites, enquanto `Files.ReadWrite.All` cobre as operações nos arquivos que o usuário já pode acessar.

## Descoberta e navegação

- Sites seguidos: `GET /me/followedSites`.
- Descoberta inicial best-effort: `GET /sites?search=*`.
- Pesquisa explícita: `GET /sites?search={termo}`.
- Bibliotecas: `GET /sites/{siteId}/drives`.
- Raiz: `GET /drives/{driveId}/root/children`.
- Subpastas: `GET /drives/{driveId}/items/{itemId}/children`.
- Todas as coleções seguem `@odata.nextLink` e são carregadas sob demanda.

As respostas são deduplicadas por ID e URL. Resultados inválidos ou que passam a retornar `403`/`404` deixam de ser oferecidos até uma nova descoberta.

## Fixação

Uma rota descoberta armazena:

- ID do site;
- ID da biblioteca (`drive`);
- ID estável da pasta raiz fixada;
- URL do site e da pasta para exibição/fallback;
- caminho amigável para a interface.

Os IDs são a identidade operacional. A URL não é usada como chave, pois nomes e caminhos podem mudar no SharePoint.

Depois de fixada, a rota passa pelos mesmos componentes de montagem, cache offline, fila de upload, conflitos, diagnóstico e políticas corporativas das rotas existentes.

## Interface

O recurso usa controles WinUI nativos, recursos de tema e tokens do EasyShare:

- pesquisa remota;
- lista de sites;
- seletor de biblioteca;
- breadcrumb da pasta atual;
- lista virtualizada de pastas e arquivos;
- barra de comandos com atualizar, voltar, carregar mais, fixar e usar URL manual;
- estados de autenticação, carregamento, vazio, permissão negada e falha transitória.

A tela suporta tema claro, escuro e alto contraste, navegação por teclado, nomes de automação e layout adaptativo.

## Limitação da plataforma

O Microsoft Graph não oferece uma enumeração delegada garantidamente exaustiva de todos os sites do tenant. A experiência combina sites seguidos, busca ampla, busca explícita e cache para apresentar os recursos descobertos aos quais o usuário tem acesso. A interface comunica essa condição sem prometer uma lista absoluta do tenant.
