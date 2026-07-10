# Changelog

Todas as mudanças relevantes do EasyShare devem ser registradas neste arquivo antes da publicação de uma release.

## [1.0.0.22] - 2026-07-10

### Estrutura e WebView

- Separa o fluxo da sessão WebView e atualizações da tela principal em um arquivo parcial por responsabilidade.
- Adiciona limpeza do cache de disco e Cache Storage da WebView sem remover cookies ou o login salvo.
- Inclui ação manual para liberar cache e executa a limpeza ao sair da aba Sessão.

### Atualizações e assinatura

- Aceita assets incrementais assinados no formato `EasySharePatch_from_<versão>_to_<versão>.exe`, mantendo o instalador completo como fallback.
- Documenta que patches de MSIX precisam ser executáveis assinados; diffs binários diretos quebrariam a assinatura do pacote.
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
