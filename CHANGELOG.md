# Changelog

Todas as mudanças relevantes do EasyShare devem ser registradas neste arquivo antes da publicação de uma release.

## [1.0.26.0] - 2026-07-14

### Hotfix do instalador

- Corrige o payload do Windows App Runtime usado pelo instalador manual em sistemas sem o framework previamente instalado.
- Registra nome, tamanho e SHA-256 de todos os payloads críticos e valida a estrutura, identidade e assinaturas antes da implantação e da publicação.
- Isola a elevação somente nos pré-requisitos, usa staging protegido e mantém a instalação do MSIX no contexto do usuário que iniciou o setup.
- Fixa a identidade criptográfica aprovada do WinFsp e congela os digests validados antes do upload da release.

### Assistente de configuração

- Substitui o diálogo inicial simplificado por um assistente responsivo de sete etapas: idioma, aparência, modo de acesso, conexão, integração com o Windows, cache/offline/notificações e revisão.
- Aplica imediatamente a localização em Português (Brasil) ou English (US) e mantém a identidade visual EasyShare em temas claro, escuro, sistema e alto contraste.
- Recomenda a melhor configuração disponível, compara Microsoft Graph e Sessão do navegador e orienta Client ID, Tenant, URL, login e consentimento em linguagem acessível.
- Valida letra da unidade, montagem automática, inicialização com o Windows, cache de listagens, limite offline, rede limitada, bateria e notificações antes da conclusão.
- Mantém as escolhas em rascunho durante o fluxo e realiza um único commit validado ao concluir, sem persistir parcialmente as etapas canceladas.
- Respeita e bloqueia valores gerenciados por política corporativa, incorporando essas restrições às recomendações e validações.
- Versiona a conclusão da primeira execução e adiciona em Ajustes a ação para executar o assistente novamente com as preferências atuais pré-preenchidas.

### Explorador SharePoint

- Adiciona descoberta nativa de sites acessíveis à conta, bibliotecas e pastas via Microsoft Graph após o login delegado por Client ID.
- Permite pesquisar, navegar por breadcrumb e fixar a pasta atual sem copiar URLs; a entrada manual permanece como fallback.
- Resolve URLs manuais em identidades estáveis do Microsoft Graph antes de persistir a rota, sem depender de cookies no modo Graph.
- Persiste IDs estáveis de site, drive e item e usa o Graph também nas operações da unidade virtual, cache offline e fila de upload.
- Filtra resultados pela política corporativa de hosts, confina caminhos à pasta fixada e preserva o último cache válido em falhas remotas.
- Reduz o consentimento de site de `Sites.ReadWrite.All` para `Sites.Read.All`, mantendo escrita somente nos arquivos acessíveis ao usuário.

### Atualizações incrementais

- Torna o canal GitHub estritamente patch-only: o app seleciona apenas o asset canônico que liga exatamente a versão instalada à release mais recente e nunca baixa EXE, MSI ou MSIX completo como fallback.
- Corrige a comparação das versões com underscores no nome do patch, exige o MSIX-base no cache e falha de forma segura diante de patch ausente, duplicado ou malformado.

## [1.0.25.0] - 2026-07-11

### Microsoft Store

- Corrige o nome de exibição do fornecedor para ArchGTi.Tech.
- Move o incremento da release para o terceiro campo da versão e mantém a revisão reservada pela Store em zero.
- Restringe o pacote MSIX a dispositivos Windows Desktop, evitando a oferta incompatível no Xbox.
- Documenta a justificativa da funcionalidade restrita runFullTrust para a certificação no Partner Center.
- Separa o canal de atualização: instalações da Microsoft Store atualizam pela Store e instalações externas continuam usando o GitHub Releases.
- Associa o pacote à identidade ArchGTi.Tech.EasyPointShare e ao fornecedor atribuídos pelo Partner Center.
- Usa EasyPointShare como nome de exibição reservado do pacote da Microsoft Store.

### Personalização

- Centraliza a grade da seção em uma largura máxima consistente com a referência visual.
- Mantém Tema e Alto contraste alinhados no topo e a Cor de destaque centralizada abaixo.

## [1.0.0.24] - 2026-07-10

### Personalização

- Remove a prévia visual da tela Personalização.
- Reorganiza Tema e Alto contraste no topo e move a Cor de destaque para uma seção inferior alinhada.
- Ajusta o reflow para manter os controles utilizáveis em janelas estreitas.

## [1.0.0.23] - 2026-07-10

### Personalização e interface

- Reorganiza o espaço da tela Personalização com uma prévia visual da cor de destaque e dos controles da aplicação.
- Corrige o reflow responsivo do seletor de cor em janelas estreitas.
- Corrige o contraste dos botões minimizar, maximizar e fechar quando o EasyShare está no tema claro e o Windows permanece no tema escuro.

## [1.0.0.22] - 2026-07-10

### Personalização

- Adiciona a seção Personalização em Ajustes com tema Sistema, Claro ou Escuro.
- Permite escolher a cor de destaque e ativar o alto contraste do aplicativo.
- Persiste as preferências e aplica as mudanças imediatamente sem alterar a sincronização.

### Estrutura e WebView

- Separa o fluxo da sessão WebView e atualizações da tela principal em um arquivo parcial por responsabilidade.
- Adiciona limpeza do cache de disco e Cache Storage da WebView sem remover cookies ou o login salvo.
- Inclui ação manual para liberar cache e executa a limpeza ao sair da aba Sessão.

### Atualizações e assinatura

- Aceita assets incrementais assinados no formato `EasySharePatch_from_<versão>_to_<versão>.exe`, mantendo o instalador completo como fallback.
- Gera e aplica patches por blocos sobre o MSIX-base em cache, reconstruindo exatamente o pacote-alvo e validando os hashes antes da instalação.
- O instalador agora armazena o MSIX assinado localmente para habilitar atualizações incrementais futuras.
- Fortalece o script de assinatura com timestamp opcional e verificação `signtool /pa`.
- Documenta o fluxo obrigatório de code signing comercial para produção.

## [1.0.0.21] - 2026-07-10

### Atualizador

- Instaladores baixados agora são copiados para uma pasta temporária de execução antes do lançamento.
- Corrige a falha do host .NET ao resolver o caminho do executável dentro de `AppData\Local\EasyShare\Updates`.
- Adiciona teste automatizado para o estágio seguro do instalador.

## [1.0.0.20] - 2026-07-10

### Release corrigida

- Rebuild da versão corrigida com novo número para substituir a release `1.0.0.19`.
- Mantém a validação da assinatura do MSIX e a assinatura dos artefatos antes da publicação.

## [1.0.0.19] - 2026-07-10

### Instalador

- O instalador agora valida a assinatura do pacote MSIX antes de chamar `Add-AppxPackage`.
- O fluxo de release documenta e automatiza a assinatura do MSIX, EXE e MSI, evitando o erro do Windows `0x800B0100` causado por pacotes sem assinatura.

## [1.0.0.18] - 2026-07-10

### Confiabilidade e segurança

- Preferência de inicialização agora desativa entradas antigas do Windows em vez de reativá-las silenciosamente.
- Uploads do Explorer passaram a usar fila local durável, retry progressivo e detecção de conflito.
- Listagens passaram a usar cache de metadados no SQLite com expiração e limpeza ao encerrar a sessão.
- Updater valida o digest SHA-256 publicado pelo GitHub antes de reutilizar ou abrir um instalador.
- Testes automatizados cobrem allowlist HTTPS do SharePoint e verificação de integridade dos downloads.

### Documentação

- README recebeu guia de configuração do Entra ID, implantação para TI e operação da fila/cache.

## [1.0.0.17] - 2026-07-10

### Interface

- Redesign responsivo das telas Ajuda, Sobre e Ajustes.
- Fundos opacos para diálogos e overlays.
- Melhor aproveitamento da largura disponível e correção de sobreposição nos campos de Ajustes.
- Superfícies dos cartões padronizadas com acabamento esmaecido, translúcido e bordas arredondadas.
- Raios de canto consistentes em cartões, estados vazios, InfoBars, campos e botões.

### Segurança

- Validação restrita de URLs SharePoint para HTTPS e hosts permitidos.
- Limpeza dos caches de conteúdo ao resetar ou encerrar a sessão.

### Atualizações

- Changelog da release passa a ser lido pelo updater e exibido na tela Sobre quando uma nova versão estiver disponível.
- Publicação de release passa a exigir uma seção de changelog correspondente à versão.

### Projeto e documentação

- README reformulado com a dor, a proposta e os benefícios do EasyShare.
- Configurações da solução corrigidas para Debug/Release em x86, x64 e ARM64.

## [1.0.0.16] - 2026-07-09

### Entregue

- Fluxo de atualização via GitHub Releases.
- Sessão integrada do SharePoint via WebView2.
- Unidade virtual baseada em WinFsp.
- Pastas fixadas, tela de ajuda, tela Sobre, ajustes e reset local.
- Ícone na bandeja e suporte à inicialização com o Windows.
