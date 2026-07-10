# EasyShare

> O SharePoint que a equipe usa no navegador, acessível pelo Windows Explorer.

O EasyShare é um aplicativo Windows que aproxima o SharePoint da rotina de quem trabalha com arquivos todos os dias. Ele cria uma unidade virtual no Explorer e permite fixar pastas do SharePoint para que elas sejam acessadas por um caminho familiar, sem transformar o trabalho em uma sequência de abas, URLs e logins repetidos.

## A ideia

O SharePoint é excelente como plataforma de colaboração, mas nem sempre oferece a experiência mais simples para o trabalho operacional. Muitas equipes precisam abrir o navegador, localizar o site correto, navegar por várias camadas e repetir esse processo sempre que precisam consultar ou enviar um arquivo.

O EasyShare existe para reduzir essa distância: manter o SharePoint como fonte de dados e oferecer uma experiência mais próxima do Windows, com pastas organizadas, unidade virtual e acesso centralizado.

## A dor que o EasyShare resolve

No dia a dia, o usuário pode enfrentar:

- dificuldade para encontrar o site, biblioteca ou pasta correta;
- dependência do navegador para uma tarefa que normalmente seria feita no Explorer;
- múltiplas URLs e logins espalhados entre equipes e projetos;
- necessidade de configurar manualmente cada pasta que precisa acessar;
- confusão entre sincronizar arquivos, abrir uma pasta online e trabalhar com a estrutura real do SharePoint;
- perda de contexto quando o app é fechado ou o Windows é reiniciado.

Esses atritos aumentam o tempo gasto em tarefas simples e favorecem cópias locais desnecessárias, caminhos incorretos e erros de operação.

## A solução

O EasyShare combina uma sessão autenticada do SharePoint com uma unidade virtual no Windows:

1. O usuário entra no SharePoint pelo próprio EasyShare ou usa o modo corporativo configurado pela TI.
2. Navega até a pasta desejada.
3. Fixa essa pasta no aplicativo.
4. O EasyShare organiza as pastas fixadas em uma unidade virtual, acessível pelo Explorer.
5. O conteúdo é consultado sob demanda, mantendo o SharePoint como origem dos dados.

O resultado é um fluxo mais direto: o usuário continua trabalhando com o SharePoint, mas encontra suas pastas no lugar em que já sabe trabalhar.

## Por que usar o EasyShare?

### Menos passos para chegar ao arquivo

As pastas importantes ficam fixadas e organizadas em um único lugar. O usuário não precisa lembrar qual site ou URL contém cada documento.

### Experiência familiar para o usuário Windows

O acesso acontece pelo Explorer, com uma unidade virtual e nomes de pastas definidos pela equipe. Isso reduz a curva de aprendizado e facilita a adoção por usuários menos técnicos.

### SharePoint continua sendo a fonte oficial

O EasyShare não cria uma nova plataforma de documentos. Ele funciona como uma ponte de acesso ao conteúdo já existente no SharePoint.

### Login adequado ao cenário da empresa

O modo principal permite entrar pelo próprio aplicativo usando a conta Microsoft do usuário. Ambientes corporativos que possuem configuração no Microsoft Entra ID podem usar o modo com Client ID e Tenant fornecidos pela TI.

### Menos dependência de configuração manual

O aplicativo mantém as pastas fixadas, restaura a sessão quando possível, pode iniciar com o Windows e permanece na bandeja para manter a unidade virtual disponível.

### Administração mais simples

As configurações ficam concentradas no aplicativo: modo de acesso, inicialização, ponto de montagem, sessão, cache e reset dos dados locais.

## Recursos atuais

- Unidade virtual do Windows baseada em WinFsp.
- Navegação do SharePoint por sessão integrada com WebView2.
- Pastas fixadas com nome personalizado no Explorer.
- Adição, edição, remoção e teste de pastas configuradas.
- Leitura e download sob demanda de arquivos.
- Upload inicial e renomeação conectados ao SharePoint.
- Restauração da sessão e atualização da unidade após o carregamento.
- Inicialização com o Windows e opção de iniciar minimizado.
- Ícone na bandeja do sistema para manter o app disponível sem ocupar a tela.
- Tela de envios para acompanhar operações pendentes.
- Ajuda integrada para orientar usuários não técnicos.
- Reset completo das configurações locais sem apagar arquivos do SharePoint.
- Verificação de novas versões pelo GitHub Releases.
- Interface WinUI 3 responsiva, com tema escuro e claro e layouts adaptáveis.

## Como começar

### Para usuários

1. Instale o EasyShare a partir da [release mais recente](https://github.com/cainhooow/EasyShare/releases/latest).
2. Abra o aplicativo e escolha **Entrar pelo app**, salvo quando a TI tiver fornecido um Client ID.
3. Faça login no SharePoint e marque a opção para manter a sessão conectada quando disponível.
4. Abra a pasta desejada na aba **Sessão**.
5. Clique em **Fixar pasta atual**.
6. Acesse a unidade criada pelo EasyShare no Windows Explorer.

Se a unidade não aparecer, verifique se o WinFsp está instalado, se existe pelo menos uma pasta fixada e se a opção **Criar unidade automaticamente** está habilitada em **Ajustes**.

### Requisitos

- Windows 10 versão 19041 ou superior, ou Windows 11.
- WebView2 Runtime para a sessão integrada do SharePoint.
- WinFsp para a unidade virtual.
- Conectividade com o ambiente Microsoft 365/SharePoint da organização.

O instalador pode incluir os pré-requisitos necessários, conforme a versão publicada.

## Modos de acesso

### Entrar pelo app

É o caminho recomendado para a maioria dos usuários. A autenticação acontece na sessão integrada do EasyShare e não exige que o usuário conheça Client ID ou Tenant.

### Client ID da empresa

É destinado a ambientes em que a TI registrou o aplicativo no Microsoft Entra ID e forneceu os dados necessários. O uso desse modo depende de permissões, redirect URI e consentimento administrativo configurados corretamente.

## Como o projeto funciona

```text
Usuário
  ↓
EasyShare / WinUI 3
  ├─ Sessão SharePoint via WebView2
  ├─ Rotas e ajustes locais via SQLite
  ├─ Unidade virtual via WinFsp
  └─ Serviços de conteúdo SharePoint
          ↓
      SharePoint / Microsoft 365
```

### Componentes principais

- **WinUI 3 e Windows App SDK**: interface e janela nativas do Windows.
- **WebView2**: sessão integrada para autenticação no SharePoint.
- **WinFsp e WinFsp.Net**: criação e operação da unidade virtual.
- **SQLite**: armazenamento local de rotas e preferências.
- **Fila de uploads**: grava cada alteração em `LocalApplicationData\EasyShare\UploadQueue` antes de tentar a rede, com retry e preservação do arquivo em caso de queda.
- **Microsoft Graph/MSAL**: modo corporativo opcional quando configurado pela TI.
- **GitHub Releases**: origem da verificação de novas versões.

### Cache e uploads

As listagens de pastas ficam em cache no SQLite pelo intervalo definido em **Ajustes > Cache de listagem**. O cache guarda somente metadados da listagem, não o conteúdo dos arquivos, e é invalidado após criar, enviar, renomear ou excluir itens. Ao sair da sessão ou resetar o aplicativo, o cache é limpo.

Alterações feitas pelo Explorer são gravadas primeiro na fila local. O EasyShare tenta o envio em segundo plano com retentativa progressiva. Se o arquivo remoto mudou durante a edição, o envio fica marcado como conflito e o payload local é mantido para revisão; ele não sobrescreve silenciosamente a versão de outra pessoa.

## Segurança e limitações atuais

O projeto já possui validação HTTPS e allowlist de hosts SharePoint para impedir que URLs arbitrárias recebam cookies da sessão. A limpeza de cache também é acionada durante o reset e a limpeza da sessão.

Ainda assim, o projeto está em desenvolvimento ativo e não deve ser tratado como uma distribuição de produção sem concluir:

- testes automatizados para os fluxos de autenticação e conteúdo;
- validação após reboot e fechamento completo do aplicativo;
- assinatura confiável de MSI, EXE e MSIX;
- validação criptográfica do instalador antes da execução pelo updater;
- revisão final do instalador e da política de confiança do certificado.

Não distribua certificados de teste como se fossem certificados de produção. Em máquinas com Smart App Control ou proteção equivalente, binários não assinados ou assinados apenas localmente podem ser bloqueados.

### Guia para TI: Microsoft Entra ID

Para habilitar o modo **Client ID da empresa**:

1. Registre um aplicativo em **Microsoft Entra ID > Registros de aplicativo** como aplicativo público/nativo.
2. Em **Autenticação**, adicione o Redirect URI de aplicativo móvel e desktop `http://localhost` e habilite o fluxo de cliente público.
3. Em **Permissões de API > Microsoft Graph > Delegadas**, adicione somente as permissões necessárias ao ambiente. Para o fluxo atual, normalmente são `User.Read`, `Files.ReadWrite.All`, `Sites.ReadWrite.All` e `offline_access`; confirme a política de menor privilégio da organização.
4. Conceda consentimento administrativo quando o tenant exigir.
5. Informe no EasyShare o **Application (client) ID** e o Tenant ID. Se a autenticação entrar em loop, remova a conta/cache do aplicativo e confirme o Redirect URI e o consentimento.

O modo **Entrar pelo app** continua sendo o caminho recomendado quando a TI não deseja registrar um aplicativo corporativo.

### Checklist de implantação

- Instalar WebView2, WinFsp e o Windows App Runtime compatíveis.
- Instalar o MSI/EXE somente depois de validar a assinatura Authenticode dos arquivos.
- Ativar **Abrir EasyShare junto com o Windows** apenas após confirmar que o pacote foi instalado para o usuário correto.
- Validar uma pasta fixada, criação/renomeação, upload com a rede desligada e remontagem após reinício.
- Publicar somente assets assinados por certificado de code signing confiável. O script de release aceita `-RequireTrustedSignature` para bloquear a publicação quando essa condição não for atendida.

## Desenvolvimento

### Pré-requisitos

- .NET 10 SDK.
- Windows SDK compatível com o alvo `10.0.26100.0`.
- Dependências restauráveis via NuGet.
- WinFsp e WebView2 para executar e validar todos os fluxos.

### Clonar e compilar

```powershell
git clone https://github.com/cainhooow/EasyShare.git
cd EasyShare

dotnet restore .\src\EasyShare\EasyShare.csproj --runtime win-x64
dotnet build .\EasyShare.slnx --configuration Release --no-restore
```

Para gerar um pacote MSIX de teste:

```powershell
dotnet publish .\src\EasyShare\EasyShare.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:Platform=x64 `
  -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageDir=.\dist\package\
```

O pacote MSIX precisa ser assinado antes de ser copiado para os instaladores EXE/MSI. Sem essa etapa, o `Add-AppxPackage` falha com `0x800B0100` mesmo que o certificado esteja instalado na máquina.

Use o certificado de code signing disponível no repositório local de certificados do Windows (ou informe o thumbprint do certificado de produção):

```powershell
.\scripts\Sign-EasyShareArtifacts.ps1 `
  -MsixPath .\dist\package\EasyShare_1.0.0.21_x64.msix `
  -CertificateThumbprint B3BF66137620B35E9AAB46642B8790C7DBFB8273
```

Depois de assinar o MSIX, copie-o para os payloads e gere os instaladores. Assine também o EXE/MSI finais antes de publicar:

```powershell
.\scripts\Sign-EasyShareArtifacts.ps1 `
  -MsixPath .\dist\payload-exe\EasyShare_1.0.0.21_x64.msix `
  -ExePath .\dist\EasyShareSetup.exe `
  -MsiPath .\dist\EasyShareSetup.msi `
  -CertificateThumbprint B3BF66137620B35E9AAB46642B8790C7DBFB8273
```

### Configurar outro repositório de atualizações

O repositório consultado pelo updater pode ser alterado no build:

```powershell
dotnet build .\src\EasyShare\EasyShare.csproj `
  -p:GitHubRepositoryOwner=owner `
  -p:GitHubRepositoryName=repo
```

### Publicar uma release

Antes de publicar, adicione em `CHANGELOG.md` uma seção com a versão exata do `Package.appxmanifest`, por exemplo `## [1.0.0.21] - 2026-07-10`, descrevendo o que mudou. O script bloqueia a publicação quando essa seção não existe ou está vazia.

Depois de gerar os instaladores em `dist/`, use o script abaixo. É necessário ter o GitHub CLI instalado e autenticado com `gh auth login`.

```powershell
.\scripts\Publish-GitHubRelease.ps1 `
  -Repository cainhooow/EasyShare `
  -ExePath dist/EasyShareSetup.exe `
  -MsiPath dist/EasyShareSetup.msi `
  -RequireTrustedSignature
```

O updater reconhece, nesta ordem, `EasyShareSetup.exe`, outros instaladores `.exe` com `EasyShare` no nome, instaladores `.msi` e pacotes `.msix`.

Quando uma nova versão estiver disponível, o conteúdo da seção correspondente do GitHub Release aparece na tela **Sobre**, em **O que há de novo**, antes das ações de download e instalação.

## Direção do projeto

Os próximos avanços prioritários são:

- concluir os testes de inicialização, bandeja e restauração da sessão;
- fortalecer downloads, retentativas e conflitos de upload;
- finalizar a assinatura e a validação do fluxo de atualização;
- publicar instaladores de produção com pré-requisitos confiáveis;
- automatizar build, testes e releases com CI/CD.

## Contribuição

Issues, melhorias de UX, testes de integração e revisões de segurança são bem-vindos. Ao relatar um problema, inclua a versão do EasyShare, a versão do Windows, o modo de acesso usado e os passos para reproduzir o comportamento.

## Status

O EasyShare é um MVP funcional em evolução. A proposta principal — acessar pastas do SharePoint pelo Windows Explorer com uma configuração simples — já está implementada, enquanto os fluxos de distribuição e validação de produção continuam sendo aprimorados.
