# EasyShare

EasyShare centraliza pastas do SharePoint no Windows Explorer usando um app WinUI 3.

## Atualizações

O app consulta automaticamente a release mais recente em `cainhooow/EasyShare` ao abrir e também pela aba **Sobre**. A tag da release deve usar uma versão numérica, como `v1.0.0.14`, maior que a versão em `src/EasyShare/Package.appxmanifest`.

Assets reconhecidos na release, em ordem de preferência:

- `EasyShareSetup.exe`
- instalador `.exe` com `EasyShare` no nome
- instalador `.msi` com `EasyShare` no nome
- pacote `.msix` com `EasyShare` no nome

Para publicar uma release depois de gerar os instaladores em `dist/`:

```powershell
.\scripts\Publish-GitHubRelease.ps1 -Repository cainhooow/EasyShare
```

O script exige GitHub CLI (`gh`) instalado e autenticado.

Em máquinas com Smart App Control/Controle Inteligente de Aplicativos ativo, o instalador e o pacote publicados na release precisam estar assinados com certificado confiável. Builds com certificado de teste ou binários sem assinatura podem ser bloqueados antes de abrir.

Para compilar apontando o updater para outro repositório:

```powershell
dotnet build .\src\EasyShare\EasyShare.csproj -p:GitHubRepositoryOwner=owner -p:GitHubRepositoryName=repo
```
