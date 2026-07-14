# CI do EasyShare

O workflow `Windows CI` usa Windows e o SDK do .NET 10. Para reproduzir localmente exatamente as mesmas verificações, execute na raiz do repositório:

```powershell
pwsh -NoProfile -File .github/scripts/Invoke-CI.ps1 -Target All
```

Esse comando executa, nesta ordem:

1. restore com auditoria NuGet de dependências diretas e transitivas; os avisos `NU1901` a `NU1904` interrompem o build;
2. gates de manifesto MSIX, paridade de localização, integridade de update, confiança no publicador, política de URLs e transporte HTTP;
3. a suíte completa de testes em `x64`;
4. compilação e geração de pacote MSIX sem assinatura em `x86`;
5. compilação e geração de pacote MSIX sem assinatura em `ARM64`.

Os pacotes de validação são gravados em `dist-test/ci/<arquitetura>`. Eles verificam a compilação e o empacotamento, mas não são artefatos instaláveis de produção porque a assinatura fica deliberadamente desativada.

## Execução seletiva

```powershell
pwsh -NoProfile -File .github/scripts/Invoke-CI.ps1 -Target Audit
pwsh -NoProfile -File .github/scripts/Invoke-CI.ps1 -Target Gates
pwsh -NoProfile -File .github/scripts/Invoke-CI.ps1 -Target Test
pwsh -NoProfile -File .github/scripts/Invoke-CI.ps1 -Target Package -Architecture x86
pwsh -NoProfile -File .github/scripts/Invoke-CI.ps1 -Target Package -Architecture ARM64
```

## Confiança no instalador de atualização

Builds que usam o canal GitHub devem incorporar a identidade do certificado autorizado. O script encaminha automaticamente as variáveis abaixo para as propriedades MSBuild correspondentes quando elas estão definidas:

- `EASYSHARE_UPDATE_PUBLISHER_SUBJECTS` → `EasyShareUpdatePublisherSubjects`;
- `EASYSHARE_UPDATE_PUBLISHER_THUMBPRINTS` → `EasyShareUpdatePublisherThumbprints`.

No GitHub Actions, configure esses nomes como secrets do repositório ou do ambiente de release. Valores múltiplos são separados por ponto e vírgula. O canal Microsoft Store não depende dessas propriedades.
